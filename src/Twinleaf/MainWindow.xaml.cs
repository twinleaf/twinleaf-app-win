// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Twinleaf.Controls;
using Twinleaf.Core;
using Twinleaf.Models;

namespace Twinleaf;

/// <summary>
/// The main window. Layout follows the GTK app: one sidebar grouped per device
/// carrying that device's channels <em>and</em> its settings, a plot, and a
/// header where the sidebar toggle is the only leading control and file
/// commands live in a menu.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly BridgeClient _client;

    /// <summary>Channels and settings in one flat, per-device grouped list.</summary>
    private readonly ObservableCollection<SidebarRow> _sidebarRows = [];

    /// <summary>Rows by column key, so a stream-values event updates in O(1).</summary>
    private readonly Dictionary<ColumnKey, SidebarRow> _channelRows = [];

    /// <summary>Rows by RPC id, so a readback updates in O(1).</summary>
    private readonly Dictionary<string, SidebarRow> _settingRows = [];

    private TioDocument _document = new();

    private bool _isPaused;
    private bool _suppressRowEvents;
    private bool _suppressPlaybackEvents;
    private string _sidebarQuery = string.Empty;

    /// <summary>Format used for the last export, so the next one defaults to it.</summary>
    private ExportFormat _lastExportFormat = ExportFormat.Csv;

    /// <summary>In-flight export requests, so a result can name its destination.</summary>
    private readonly Dictionary<string, string> _pendingExports = [];

    /// <summary>In-flight clipboard requests.</summary>
    private readonly HashSet<string> _pendingCopies = [];

    public MainWindow()
    {
        InitializeComponent();

        Title = "Twinleaf";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _client = new BridgeClient(DispatcherQueue);
        _client.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateChrome);
        _client.AvailableDevices.CollectionChanged += OnAvailableDevicesChanged;
        _client.ConnectedDevices.CollectionChanged += (_, _) => RebuildSidebar();
        _client.StreamValuesUpdated += UpdateChannelValues;
        _client.SelectionChanged += SyncChannelSelection;
        _client.PlotFrameReceived += OnPlotFrame;
        _client.RpcValueUpdated += OnRpcValueUpdated;
        _client.PlaybackChanged += OnPlaybackChanged;
        _client.ExportResultReceived += OnExportResult;
        _client.ViewDataReceived += OnViewData;

        SidebarList.ItemsSource = _sidebarRows;
        Plot.LabelProvider = LabelFor;

        BuildWindowMenu();
        UpdateChrome();

        // Live discovery keeps the picker current without a polling timer.
        _client.SetDiscovery(active: true);
        _client.RefreshDevices();

        StartSelfTestIfRequested();

        Closed += (_, _) =>
        {
            _client.SetDiscovery(active: false);
            // Dispose the client first: it joins the core's threads, so the
            // temporary log is closed by the time we try to delete it.
            _client.Dispose();
            _document.Dispose();
        };
    }

    // -----------------------------------------------------------------
    // Headless self-test
    //
    // Mirrors the GTK sibling's TWINLEAF_SELFTEST_* hooks so CI can drive a
    // real session against the simulator without a human at the keyboard.
    // Inert unless the variables are set.
    // -----------------------------------------------------------------

    private void StartSelfTestIfRequested()
    {
        var target = Environment.GetEnvironmentVariable("TWINLEAF_SELFTEST_CONNECT");
        if (!string.IsNullOrWhiteSpace(target))
        {
            UrlBox.Text = target;
            _client.Connect(target.Trim(), logPath: _document.TemporaryPath);
        }

        // TWINLEAF_SELFTEST_OPEN=<path> opens a saved .tio for inspection.
        var openPath = Environment.GetEnvironmentVariable("TWINLEAF_SELFTEST_OPEN");
        if (!string.IsNullOrWhiteSpace(openPath))
        {
            _client.PlaybackChanged += () => AppLog.Error(
                $"[selftest] playback start={_client.PlaybackStart:F3} end={_client.PlaybackEnd:F3} " +
                $"pos={_client.PlaybackPosition:F3} span={_client.PlaybackEnd - _client.PlaybackStart:F3}s " +
                $"canScrub={_client.CanScrubPlayback} inspection={_client.IsInspectionMode}");

            AppLog.Error($"[selftest] opening {openPath}");
            OpenDocument(openPath.Trim());
        }

        // TWINLEAF_SELFTEST_RPC="name=value[,name=value]" writes settings once
        // metadata lands and logs the device's replies.
        var rpcScript = Environment.GetEnvironmentVariable("TWINLEAF_SELFTEST_RPC");
        if (!string.IsNullOrWhiteSpace(rpcScript))
        {
            _client.RpcResultReceived += result =>
                AppLog.Error(
                    $"[selftest] rpc reply {result.Route}#{result.Name} ok={result.Ok} " +
                    $"value={result.Value?.ToString() ?? "null"} error={result.ErrorMessage ?? "-"}");

            _client.ConnectedDevices.CollectionChanged += (_, _) => SelfTestRunRpcScript(rpcScript);
        }

        // TWINLEAF_SELFTEST_EXPORT=<path> exports once data is available.
        var exportPath = Environment.GetEnvironmentVariable("TWINLEAF_SELFTEST_EXPORT");
        if (!string.IsNullOrWhiteSpace(exportPath))
        {
            _selfTestExportPath = exportPath.Trim();
        }

        var quitAfter = Environment.GetEnvironmentVariable("TWINLEAF_SELFTEST_QUIT");
        if (double.TryParse(quitAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            // Held in a field: a DispatcherQueueTimer with no rooted reference
            // can be collected before it fires.
            _selfTestQuitTimer = DispatcherQueue.CreateTimer();
            var timer = _selfTestQuitTimer;
            timer.Interval = TimeSpan.FromSeconds(seconds);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                if (_selfTestExportPath is { } path)
                {
                    AppLog.Error($"[selftest] exporting to {path}");
                    StartExport(path, FormatFromExtension(path));
                    // Give the export a moment to finish before exiting.
                    _selfTestExportPath = null;
                    timer.Interval = TimeSpan.FromSeconds(10);
                    timer.Start();
                    return;
                }

                AppLog.Error(
                    $"[selftest] quitting; frames={_selfTestFrameCount} sawData={_selfTestSawData} " +
                    $"selected={_client.SelectedColumns.Count} devices={_client.ConnectedDevices.Count} " +
                    $"phase={_client.Phase}");
                Console.Error.Flush();
                Application.Current.Exit();
            };
            timer.Start();
        }
    }

    private string? _selfTestExportPath;
    private bool _selfTestRpcScriptRan;
    private int _selfTestFrameCount;
    private bool _selfTestSawData;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _selfTestQuitTimer;

    /// <summary>
    /// Applies the <c>TWINLEAF_SELFTEST_RPC</c> script through the same
    /// <see cref="BridgeClient.WriteRpc"/> the settings field uses, so the text
    /// coercion and the typed FFI marshalling are the code under test.
    /// </summary>
    private void SelfTestRunRpcScript(string script)
    {
        if (_selfTestRpcScriptRan || _client.ConnectedDevices.Count == 0)
        {
            return;
        }

        _selfTestRpcScriptRan = true;

        foreach (var assignment in script.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = assignment.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var name = parts[0].Trim();
            var rpc = _client.ConnectedDevices
                .SelectMany(device => device.Rpcs)
                .FirstOrDefault(candidate => candidate.Name == name);

            if (rpc is null)
            {
                AppLog.Error($"[selftest] rpc '{name}' not found on device");
                continue;
            }

            var request = _client.WriteRpc(rpc, parts[1].Trim());
            AppLog.Error(
                $"[selftest] write {name}={parts[1].Trim()} (type {rpc.ArgType}) -> " +
                (request is null ? "REJECTED by coercion" : $"sent as {request}"));
        }
    }

    // -----------------------------------------------------------------
    // Toolbar
    // -----------------------------------------------------------------

    private void OnSidebarToggled(object sender, RoutedEventArgs e)
    {
        var show = SidebarToggle.IsChecked == true;
        SidebarPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = SidebarPanel.Visibility;
        SidebarColumn.Width = show ? new GridLength(320) : new GridLength(0);
        SidebarColumn.MinWidth = show ? 240 : 0;
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => _client.RefreshDevices();

    private void OnConnectClicked(object sender, RoutedEventArgs e)
    {
        var url = ResolveTargetUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusText.Text = "Pick a device or enter a URL first.";
            return;
        }

        ConnectFlyout.Hide();
        // The core needs the log path up front so Record can be switched on
        // mid-session without reconnecting.
        _client.Connect(url, logPath: _document.TemporaryPath);
    }

    private void OnDeviceListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        OnConnectClicked(sender, e);

    private void OnDisconnectClicked(object sender, RoutedEventArgs e) => _client.Disconnect();

    /// <summary>An explicitly typed URL wins over the picker selection.</summary>
    private string? ResolveTargetUrl()
    {
        var typed = UrlBox.Text?.Trim();
        if (!string.IsNullOrEmpty(typed))
        {
            return typed;
        }

        return (DeviceList.SelectedItem as AvailableDevice)?.Url;
    }

    private void OnRecordClicked(object sender, RoutedEventArgs e)
    {
        var enabled = RecordButton.IsChecked == true;
        // The core rejects logging with no path, so recording always targets the
        // document's temporary file; Save snapshots it to wherever the user
        // chooses, which is what lets an untitled window record straight away.
        _client.SetRecording(enabled, _document.TemporaryPath);
        UpdateChrome();
    }

    private void OnPauseClicked(object sender, RoutedEventArgs e)
    {
        _isPaused = PauseButton.IsChecked == true;
        UpdateChrome();
    }

    private void OnModeSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string tag })
        {
            return;
        }

        _client.UpdateView(view => view with { Mode = tag == "Fft" ? PlotMode.Fft : PlotMode.Timeseries });
        Plot.UpdateView(_client.View);
    }

    private void BuildWindowMenu()
    {
        foreach (var seconds in ViewConfig.WindowDurations)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = FormatDuration(seconds),
                GroupName = "PlotWindow",
                IsChecked = Math.Abs(seconds - _client.View.WindowSeconds) < 1e-9,
                Tag = seconds,
            };

            item.Click += (s, _) =>
            {
                if (s is RadioMenuFlyoutItem { Tag: double value })
                {
                    _client.UpdateView(view => view with { WindowSeconds = value });
                    Plot.UpdateView(_client.View);
                    OnPlaybackChanged();
                }
            };

            WindowFlyout.Items.Add(item);
        }
    }

    private static string FormatDuration(double seconds) => seconds < 1
        ? $"{seconds * 1000:0} ms"
        : seconds < 60
            ? $"{seconds:0.##} s"
            : $"{seconds / 60:0.##} min";

    // -----------------------------------------------------------------
    // Device picker
    // -----------------------------------------------------------------

    private void OnAvailableDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Preserve the selection across a discovery refresh; the list is
        // replaced wholesale whenever the reachable set changes.
        var selectedUrl = (DeviceList.SelectedItem as AvailableDevice)?.Url;

        DeviceList.ItemsSource = _client.AvailableDevices.ToList();

        if (selectedUrl is not null)
        {
            DeviceList.SelectedItem = _client.AvailableDevices
                .FirstOrDefault(device => device.Url == selectedUrl);
        }

        if (DeviceList.SelectedItem is null && _client.AvailableDevices.Count > 0)
        {
            DeviceList.SelectedIndex = 0;
        }
    }

    // -----------------------------------------------------------------
    // .tio document
    // -----------------------------------------------------------------

    private async void OnOpenLogClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        // Unpackaged apps have no implicit window to parent the dialog to.
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".tio");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            OpenDocument(file.Path);
        }
    }

    private void OpenDocument(string path)
    {
        try
        {
            var document = TioDocument.Open(path);
            _document.Dispose();
            _document = document;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not open {System.IO.Path.GetFileName(path)}: {exception.Message}";
            AppLog.Error($"open failed: {exception}");
            return;
        }

        // Inspect the copy, not the original: the core keeps the file open, and
        // a later recording would otherwise append to the user's saved log.
        _client.OpenLog(_document.TemporaryPath);
        UpdateChrome();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (_document.SavedPath is null)
        {
            OnSaveAsClicked(sender, e);
            return;
        }

        SaveDocumentTo(_document.SavedPath);
    }

    private async void OnSaveAsClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add("Twinleaf log", [".tio"]);
        picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_document.DisplayName);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            SaveDocumentTo(file.Path);
        }
    }

    private void SaveDocumentTo(string path)
    {
        try
        {
            _document.SaveTo(path);
            StatusText.Text = $"Saved {FormatBytes((ulong)_document.ByteCount)} to {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not save: {exception.Message}";
            AppLog.Error($"save failed: {exception}");
        }

        UpdateChrome();
    }

    // -----------------------------------------------------------------
    // Export
    // -----------------------------------------------------------------

    private async void OnExportClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        // Both formats are offered; the chosen extension picks which one, the
        // way the macOS app does it. HDF5 needs the core built with its
        // feature, and the core says so plainly if it was not.
        picker.FileTypeChoices.Add("CSV", [".csv"]);
        picker.FileTypeChoices.Add("HDF5", [".h5", ".hdf5"]);
        picker.SuggestedFileName = ExportBaseName();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        StartExport(file.Path, FormatFromExtension(file.Path));
    }

    private void StartExport(string path, ExportFormat format)
    {
        if (_document.IsEmpty)
        {
            StatusText.Text = "Nothing to export yet — record or open a log first.";
            return;
        }

        _lastExportFormat = format;

        // Always export from the temporary log: it is the copy the core has
        // been appending to, and for an opened document it is the one that is
        // guaranteed to still exist.
        var requestId = _client.ExportLog(_document.TemporaryPath, path, format);
        _pendingExports[requestId] = path;
        StatusText.Text = $"Exporting to {System.IO.Path.GetFileName(path)}…";
    }

    private string ExportBaseName()
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(_document.DisplayName);
        return string.IsNullOrEmpty(stem) ? "Twinleaf Export" : stem;
    }

    /// <summary>Format implied by the chosen filename, falling back to the last used.</summary>
    private ExportFormat FormatFromExtension(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csv" => ExportFormat.Csv,
            ".h5" or ".hdf5" => ExportFormat.Hdf5,
            _ => _lastExportFormat,
        };

    private void OnExportResult(Interop.CoreEvent.ExportResult result)
    {
        var destination = _pendingExports.Remove(result.RequestId, out var path)
            ? System.IO.Path.GetFileName(path)
            : result.OutputPath is { } output
                ? System.IO.Path.GetFileName(output)
                : "export";

        if (!result.Ok)
        {
            StatusText.Text = $"Export failed: {result.ErrorMessage ?? "unknown error"}";
            AppLog.Error($"export failed: {result.ErrorMessage}");
            return;
        }

        var detail = result.Rows is { } rows
            ? $"{rows:N0} rows"
            : result.Bytes is { } bytes
                ? FormatBytes(bytes)
                : string.Empty;

        StatusText.Text = detail.Length > 0
            ? $"Exported {detail} to {destination}"
            : $"Exported to {destination}";
    }

    /// <summary>Copies the plotted window as text, matching GTK's "Copy data".</summary>
    private void OnCopyDataClicked(object sender, RoutedEventArgs e)
    {
        if (_client.SelectedColumns.Count == 0)
        {
            StatusText.Text = "Tick a channel to copy its data.";
            return;
        }

        var requestId = _client.CopyViewData(paneId: 0, viewportEnd: null);
        _pendingCopies.Add(requestId);
        StatusText.Text = "Copying plotted data…";
    }

    private void OnViewData(Interop.CoreEvent.ViewData data)
    {
        if (!_pendingCopies.Remove(data.RequestId))
        {
            return;
        }

        if (!data.Ok || data.Text is not { Length: > 0 } text)
        {
            StatusText.Text = $"Copy failed: {data.ErrorMessage ?? "no data in the current window"}";
            return;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);

        StatusText.Text = data.Rows is { } rows
            ? $"Copied {rows:N0} rows to the clipboard"
            : "Copied plotted data to the clipboard";
    }

    // -----------------------------------------------------------------
    // Playback scrubber
    // -----------------------------------------------------------------

    private void OnPlaybackChanged()
    {
        _suppressPlaybackEvents = true;
        try
        {
            // The slider works in seconds elapsed from the start of the log, not
            // absolute log time. Log timestamps are Unix epoch seconds (~1.79e9);
            // handing those to a Slider gives it a range of billions of steps,
            // which its step arithmetic will not accept — and elapsed seconds are
            // what the axis and the labels show anyway.
            var (min, max) = _client.PlaybackSliderRange;
            var start = _client.PlaybackStart;

            PlaybackSlider.Minimum = Math.Max(0, min - start);
            PlaybackSlider.Maximum = Math.Max(PlaybackSlider.Minimum + 0.001, max - start);
            PlaybackSlider.StepFrequency = Math.Max(0.001, (PlaybackSlider.Maximum - PlaybackSlider.Minimum) / 1000);
            PlaybackSlider.Value = Math.Clamp(
                _client.PlaybackPosition - start,
                PlaybackSlider.Minimum,
                PlaybackSlider.Maximum);
            PlaybackSlider.IsEnabled = _client.CanScrubPlayback;

            PlaybackElapsedText.Text = FormatPlaybackTime(_client.PlaybackPosition - start);
            PlaybackTotalText.Text = FormatPlaybackTime(_client.PlaybackEnd - start);
        }
        finally
        {
            _suppressPlaybackEvents = false;
        }

        UpdateChrome();
    }

    private void OnPlaybackSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressPlaybackEvents || !_client.IsInspectionMode)
        {
            return;
        }

        // Slider values are elapsed seconds; the core wants absolute log time.
        _client.SetPlaybackPosition(_client.PlaybackStart + e.NewValue);

        PlaybackElapsedText.Text = FormatPlaybackTime(_client.PlaybackPosition - _client.PlaybackStart);
    }

    /// <summary>m:ss, or h:mm:ss once the log runs past an hour.</summary>
    private static string FormatPlaybackTime(double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            return "--:--";
        }

        var total = Math.Max(0, (int)Math.Round(seconds));
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;

        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{minutes}:{secs:00}";
    }

    // -----------------------------------------------------------------
    // Sidebar
    // -----------------------------------------------------------------

    /// <summary>
    /// Rebuilds the sidebar as one section per device: the device's name and
    /// serial, then each stream with its channels, then that device's settings.
    /// This is the GTK app's layout — settings sit with the streams they belong
    /// to rather than in a panel of their own.
    /// </summary>
    private void RebuildSidebar()
    {
        _suppressRowEvents = true;
        try
        {
            _sidebarRows.Clear();
            _channelRows.Clear();
            _settingRows.Clear();

            foreach (var device in _client.ConnectedDevices)
            {
                var streams = device.Streams
                    .Select(stream => (Stream: stream, Columns: stream.Columns.Where(MatchesQuery).ToList()))
                    .Where(entry => entry.Columns.Count > 0)
                    .ToList();

                var rpcs = device.Rpcs
                    .Where(rpc => rpc.IsVisibleInRpcList && MatchesQuery(rpc, device))
                    .ToList();

                if (streams.Count == 0 && rpcs.Count == 0)
                {
                    continue;
                }

                _sidebarRows.Add(SidebarRow.DeviceHeader(device.DisplayName));
                if (!string.IsNullOrEmpty(device.Meta.SerialNumber))
                {
                    _sidebarRows.Add(SidebarRow.Subtitle(device.Meta.SerialNumber));
                }

                foreach (var (stream, columns) in streams)
                {
                    _sidebarRows.Add(SidebarRow.SectionHeader(
                        stream.EffectiveSamplingRate > 0
                            ? $"{stream.Name}   ·   {stream.EffectiveSamplingRate:0.0} Hz"
                            : stream.Name));

                    foreach (var column in columns)
                    {
                        var row = SidebarRow.Channel(column, _client.IsSelected(column.Key));
                        _channelRows[column.Key] = row;
                        _sidebarRows.Add(row);
                    }
                }

                if (rpcs.Count > 0)
                {
                    _sidebarRows.Add(SidebarRow.SectionHeader("Settings"));
                    foreach (var rpc in rpcs)
                    {
                        var row = SidebarRow.Setting(rpc, _client.ValueFor(rpc));
                        _settingRows[rpc.Id] = row;
                        _sidebarRows.Add(row);
                    }
                }
            }
        }
        finally
        {
            _suppressRowEvents = false;
        }

        UpdateChannelValues();
        UpdateSidebarChrome();
    }

    private bool MatchesQuery(ColumnInfo column) =>
        _sidebarQuery.Length == 0
        || column.Name.Contains(_sidebarQuery, StringComparison.OrdinalIgnoreCase)
        || column.Units.Contains(_sidebarQuery, StringComparison.OrdinalIgnoreCase);

    private bool MatchesQuery(RpcInfo rpc, DeviceInfo device) =>
        _sidebarQuery.Length == 0
        || rpc.Name.Contains(_sidebarQuery, StringComparison.OrdinalIgnoreCase)
        || rpc.ArgType.Contains(_sidebarQuery, StringComparison.OrdinalIgnoreCase)
        || device.DisplayName.Contains(_sidebarQuery, StringComparison.OrdinalIgnoreCase);

    private void OnSidebarSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _sidebarQuery = sender.Text?.Trim() ?? string.Empty;
        RebuildSidebar();
    }

    private void UpdateSidebarChrome()
    {
        var hasRows = _sidebarRows.Count > 0;
        SidebarList.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        SidebarEmptyText.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;

        SidebarEmptyText.Text = _client.ConnectedDevices.Count == 0
            ? "Connect a Twinleaf device, or open a .tio log."
            : _sidebarQuery.Length > 0
                ? $"Nothing matches “{_sidebarQuery}”."
                : "This device reports no channels or settings.";
    }

    private void OnChannelChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressRowEvents)
        {
            return;
        }

        if (sender is not CheckBox { Tag: SidebarRow row } || row.Key is not { } key)
        {
            return;
        }

        // The row's IsSelected is already updated by the two-way binding; only
        // tell the core when the two disagree, so echoed activeColumns events
        // do not bounce.
        if (_client.IsSelected(key) != row.IsSelected)
        {
            _client.ToggleColumn(key);
        }
    }

    /// <summary>Re-applies the core's authoritative selection to the rows.</summary>
    private void SyncChannelSelection()
    {
        _suppressRowEvents = true;
        try
        {
            foreach (var (key, row) in _channelRows)
            {
                row.IsSelected = _client.IsSelected(key);
            }
        }
        finally
        {
            _suppressRowEvents = false;
        }

        // "Copy plotted data" depends on there being a selection.
        UpdateChrome();
    }

    private void UpdateChannelValues()
    {
        foreach (var (key, value) in _client.StreamValues)
        {
            if (_channelRows.TryGetValue(key, out var row))
            {
                row.Value = NumericDisplayPolicy.Reading(value);
            }
        }
    }

    private string LabelFor(ColumnKey key)
    {
        foreach (var device in _client.ConnectedDevices)
        {
            if (device.Route != key.Route)
            {
                continue;
            }

            foreach (var stream in device.Streams)
            {
                if (stream.StreamId != key.StreamId)
                {
                    continue;
                }

                var column = stream.Columns.FirstOrDefault(item => item.Key.ColumnIndex == key.ColumnIndex);
                if (column is null)
                {
                    continue;
                }

                var label = $"{stream.Name}.{column.Name}";
                return key.Derivation is { } derivation ? derivation.Label(label) : label;
            }
        }

        return key.ToString();
    }

    // -----------------------------------------------------------------
    // Settings controls
    // -----------------------------------------------------------------

    /// <summary>Applies a readback to its row without disturbing an in-progress edit.</summary>
    private void OnRpcValueUpdated(string rpcId)
    {
        if (!_settingRows.TryGetValue(rpcId, out var row) || row.Rpc is null)
        {
            return;
        }

        _suppressRowEvents = true;
        try
        {
            row.ApplyValue(_client.ValueFor(row.Rpc));
        }
        finally
        {
            _suppressRowEvents = false;
        }
    }

    private static SidebarRow? RowFrom(object sender) =>
        (sender as FrameworkElement)?.Tag as SidebarRow;

    private void OnCaptureRpcClicked(object sender, RoutedEventArgs e)
    {
        // Triggers the capture the way the macOS app's button does. Reading the
        // captured buffer back is a separate view that is not ported yet.
        if (RowFrom(sender)?.Rpc is { } rpc)
        {
            _client.RunRpc(rpc);
            StatusText.Text = $"Triggered {rpc.Name}";
        }
    }

    private void OnRunRpcClicked(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender)?.Rpc is { } rpc)
        {
            _client.RunRpc(rpc);
            StatusText.Text = $"Ran {rpc.Name}";
        }
    }

    private void OnRpcSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressRowEvents || sender is not ToggleSwitch toggle)
        {
            return;
        }

        if (RowFrom(sender) is not { Rpc: { } rpc })
        {
            return;
        }

        // Toggled also fires when the binding moves the switch to match a
        // readback; only write when the user actually changed it.
        var current = _client.ValueFor(rpc);
        var deviceState = current is not null && ValueIsOn(current);
        if (toggle.IsOn == deviceState)
        {
            return;
        }

        _client.WriteRpc(rpc, rpc.SwitchArgumentText(toggle.IsOn));
    }

    private static bool ValueIsOn(JsonValue value) => value switch
    {
        JsonValue.Bool flag => flag.Value,
        JsonValue.Number number => number.Value != 0,
        JsonValue.Str text => text.Value.Trim().ToLowerInvariant() is "true" or "yes" or "on" or "1",
        _ => false,
    };

    private void OnRpcFieldFocus(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender) is { } row)
        {
            row.IsEditing = true;
        }
    }

    private void OnRpcFieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        CommitRpcField(sender);
    }

    private void OnRpcFieldCommit(object sender, RoutedEventArgs e) => CommitRpcField(sender);

    private void CommitRpcField(object sender)
    {
        if (RowFrom(sender) is not { Rpc: { } rpc } row)
        {
            return;
        }

        row.IsEditing = false;

        // Unchanged text is not a write: committing on every focus loss would
        // otherwise re-send a setting each time the user tabbed past it.
        if (row.ValueText == row.ReadOnlyText)
        {
            return;
        }

        if (_client.WriteRpc(rpc, row.ValueText) is null)
        {
            // Rejected by the type coercion — restore what the device last said
            // rather than leaving unparseable text sitting in the field.
            StatusText.Text = $"“{row.ValueText}” is not a valid {rpc.DisplayType} for {rpc.Name}";
            row.ApplyValue(_client.ValueFor(rpc));
        }
    }

    // -----------------------------------------------------------------
    // Plot
    // -----------------------------------------------------------------

    private void OnPlotFrame(PlotFrame frame)
    {
        _selfTestFrameCount++;
        var points = frame.Series.Sum(item => item.Points.Count);
        if (_selfTestFrameCount == 1 || (points > 0 && !_selfTestSawData))
        {
            _selfTestSawData |= points > 0;
            AppLog.Error(
                $"[selftest] frame {_selfTestFrameCount}: pane={frame.PaneId} mode={frame.Mode} " +
                $"series={frame.Series.Count} points={points}");
        }

        // Pause freezes the display but leaves the core streaming, so resuming
        // shows current data rather than replaying a backlog.
        if (_isPaused)
        {
            return;
        }

        Plot.Update(frame);
    }

    // -----------------------------------------------------------------
    // Chrome
    // -----------------------------------------------------------------

    private void UpdateChrome()
    {
        var connected = _client.IsConnected;
        var inspecting = _client.IsInspectionMode;

        ConnectButton.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        DisconnectButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

        // A loaded log is a fixed recording: there is nothing to record and
        // nothing to pause, and the scrubber is what moves through it.
        RecordButton.IsEnabled = connected && !inspecting;
        PauseButton.IsEnabled = connected && !inspecting;

        // Recording starts with the session, so the toggle has to show that
        // rather than sitting unchecked while the log fills up. Click (not
        // Checked) drives the handler, so this cannot re-enter it.
        RecordButton.IsChecked = _client.IsRecording;

        PlaybackBar.Visibility = inspecting && _client.HasPlaybackRange
            ? Visibility.Visible
            : Visibility.Collapsed;

        var hasLog = !_document.IsEmpty;
        SaveMenuItem.IsEnabled = hasLog;
        SaveAsMenuItem.IsEnabled = hasLog;
        ExportMenuItem.IsEnabled = hasLog;
        CopyDataMenuItem.IsEnabled = _client.SelectedColumns.Count > 0;

        var badges = new List<string> { _document.DisplayName };
        if (_document.SavedPath is null && hasLog)
        {
            badges.Add("unsaved");
        }

        if (inspecting)
        {
            badges.Add("inspection");
        }

        if (_client.LogElapsedSeconds is { } elapsed && elapsed > 0)
        {
            badges.Add(FormatPlaybackTime(elapsed));
        }

        DocumentText.Text = string.Join("  ·  ", badges);
        StatusText.Text = _client.ErrorMessage ?? _client.StatusMessage;

        UpdateSidebarChrome();

        RecordingText.Text = _client.LogProgress is { } progress
            ? string.Format(
                CultureInfo.CurrentCulture,
                "{0:N0} packets · {1}",
                progress.Packets,
                FormatBytes(progress.Bytes))
            : string.Empty;
    }

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024UL * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
