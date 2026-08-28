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
using Windows.Storage.Pickers;
using WinRT.Interop;
using Twinleaf.Controls;
using Twinleaf.Core;
using Twinleaf.Models;

namespace Twinleaf;

/// <summary>
/// The main window: device picker, channel sidebar and plot, mirroring the
/// layout of the macOS app's main scene.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly BridgeClient _client;
    private readonly ObservableCollection<ChannelNode> _channelRoots = [];

    /// <summary>Column rows by key, so a stream-values event updates in O(1).</summary>
    private readonly Dictionary<ColumnKey, ChannelNode> _columnNodes = [];

    private bool _isPaused;
    private bool _suppressChannelEvents;

    public MainWindow()
    {
        InitializeComponent();

        Title = "Twinleaf";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _client = new BridgeClient(DispatcherQueue);
        _client.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateChrome);
        _client.AvailableDevices.CollectionChanged += OnAvailableDevicesChanged;
        _client.ConnectedDevices.CollectionChanged += (_, _) =>
        {
            RebuildChannelTree();
            RebuildSettingsList();
        };
        _client.StreamValuesUpdated += UpdateChannelValues;
        _client.SelectionChanged += SyncChannelSelection;
        _client.PlotFrameReceived += OnPlotFrame;
        _client.RpcValueUpdated += OnRpcValueUpdated;
        _client.PlaybackChanged += OnPlaybackChanged;

        ChannelTree.ItemsSource = _channelRoots;
        SettingsList.ItemsSource = _settingRows;
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
    // real connect-and-plot cycle against the simulator without a human at the
    // keyboard. Inert unless the variables are set.
    // -----------------------------------------------------------------

    private void StartSelfTestIfRequested()
    {
        var target = Environment.GetEnvironmentVariable("TWINLEAF_SELFTEST_CONNECT");
        if (!string.IsNullOrWhiteSpace(target))
        {
            UrlBox.Text = target;
            _client.Connect(target.Trim());

            // Tick the first available column once metadata lands, so the test
            // exercises the plot path and not just the connection.
            _client.ConnectedDevices.CollectionChanged += SelfTestSelectFirstColumn;
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
        // metadata lands and logs the device's replies, so an RPC round trip can
        // be verified without a human at the keyboard.
        var rpcScript = Environment.GetEnvironmentVariable("TWINLEAF_SELFTEST_RPC");
        if (!string.IsNullOrWhiteSpace(rpcScript))
        {
            _client.RpcResultReceived += result =>
                AppLog.Error(
                    $"[selftest] rpc reply {result.Route}#{result.Name} ok={result.Ok} " +
                    $"value={result.Value?.ToString() ?? "null"} error={result.ErrorMessage ?? "-"}");

            _client.ConnectedDevices.CollectionChanged += (_, _) => SelfTestRunRpcScript(rpcScript);
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
                AppLog.Error(
                    $"[selftest] quitting after {seconds}s; frames={_selfTestFrameCount} " +
                    $"sawData={_selfTestSawData} selected={_client.SelectedColumns.Count} " +
                    $"devices={_client.ConnectedDevices.Count} phase={_client.Phase}");
                Console.Error.Flush();
                // Close() only dismisses the window; Exit() ends the process,
                // which is what an unattended run needs.
                Application.Current.Exit();
            };
            timer.Start();
        }
    }

    private bool _selfTestRpcScriptRan;

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
            var text = parts[1].Trim();

            var rpc = _client.ConnectedDevices
                .SelectMany(device => device.Rpcs)
                .FirstOrDefault(candidate => candidate.Name == name);

            if (rpc is null)
            {
                AppLog.Error($"[selftest] rpc '{name}' not found on device");
                continue;
            }

            var request = _client.WriteRpc(rpc, text);
            AppLog.Error(
                $"[selftest] write {name}={text} (type {rpc.ArgType}) -> " +
                (request is null ? "REJECTED by coercion" : $"sent as {request}"));
        }
    }

    private int _selfTestFrameCount;
    private bool _selfTestSawData;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _selfTestQuitTimer;

    private void SelfTestSelectFirstColumn(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var first = _client.ConnectedDevices
            .SelectMany(device => device.Streams)
            .SelectMany(stream => stream.Columns)
            .FirstOrDefault();

        if (first is null || _client.SelectedColumns.Count > 0)
        {
            return;
        }

        AppLog.Error($"[selftest] selecting {first.Key}");
        _client.ToggleColumn(first.Key);
    }

    // -----------------------------------------------------------------
    // Toolbar
    // -----------------------------------------------------------------

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => _client.RefreshDevices();

    private void OnConnectClicked(object sender, RoutedEventArgs e)
    {
        if (_client.IsConnected)
        {
            _client.Disconnect();
            return;
        }

        var url = ResolveTargetUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusText.Text = "Pick a device or enter a URL first.";
            return;
        }

        // The core needs the log path up front so Record can be switched on
        // mid-session without reconnecting.
        _client.Connect(url, logPath: _document.TemporaryPath);
    }

    /// <summary>An explicitly typed URL wins over the picker selection.</summary>
    private string? ResolveTargetUrl()
    {
        var typed = UrlBox.Text?.Trim();
        if (!string.IsNullOrEmpty(typed))
        {
            return typed;
        }

        return (DeviceCombo.SelectedItem as AvailableDevice)?.Url;
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
        if (sender is not RadioMenuFlyoutItem item || item.Tag is not string tag)
        {
            return;
        }

        var mode = tag == "Fft" ? PlotMode.Fft : PlotMode.Timeseries;
        _client.UpdateView(view => view with { Mode = mode });
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
        var selectedUrl = (DeviceCombo.SelectedItem as AvailableDevice)?.Url;

        DeviceCombo.ItemsSource = _client.AvailableDevices.ToList();
        DeviceCombo.DisplayMemberPath = nameof(AvailableDevice.Label);

        if (selectedUrl is not null)
        {
            DeviceCombo.SelectedItem = _client.AvailableDevices
                .FirstOrDefault(device => device.Url == selectedUrl);
        }

        if (DeviceCombo.SelectedItem is null && _client.AvailableDevices.Count > 0)
        {
            DeviceCombo.SelectedIndex = 0;
        }
    }

    // -----------------------------------------------------------------
    // Channel sidebar
    // -----------------------------------------------------------------

    private void RebuildChannelTree()
    {
        _suppressChannelEvents = true;
        try
        {
            _channelRoots.Clear();
            _columnNodes.Clear();

            foreach (var device in _client.ConnectedDevices)
            {
                var deviceNode = new ChannelNode(
                    device.DisplayName,
                    $"{device.Url}  {device.Route}");

                foreach (var stream in device.Streams)
                {
                    var rate = stream.EffectiveSamplingRate > 0
                        ? $"{stream.EffectiveSamplingRate:0.###} Hz"
                        : "rate unknown";

                    var streamNode = new ChannelNode(stream.Name, rate);

                    foreach (var column in stream.Columns)
                    {
                        var columnNode = new ChannelNode(
                            column.DisplayLabel,
                            column.Description,
                            column.Key)
                        {
                            IsSelected = _client.IsSelected(column.Key),
                        };

                        _columnNodes[column.Key] = columnNode;
                        streamNode.Children.Add(columnNode);
                    }

                    deviceNode.Children.Add(streamNode);
                }

                _channelRoots.Add(deviceNode);
            }
        }
        finally
        {
            _suppressChannelEvents = false;
        }

        UpdateChannelValues();
    }

    private void OnChannelChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressChannelEvents)
        {
            return;
        }

        if (sender is not CheckBox { Tag: ChannelNode node } || node.Key is not { } key)
        {
            return;
        }

        // The node's IsSelected is already updated by the two-way binding; only
        // tell the core when the two disagree, so echoed activeColumns events
        // do not bounce.
        if (_client.IsSelected(key) != node.IsSelected)
        {
            _client.ToggleColumn(key);
        }
    }

    /// <summary>Re-applies the core's authoritative selection to the tree.</summary>
    private void SyncChannelSelection()
    {
        _suppressChannelEvents = true;
        try
        {
            foreach (var (key, node) in _columnNodes)
            {
                node.IsSelected = _client.IsSelected(key);
            }
        }
        finally
        {
            _suppressChannelEvents = false;
        }
    }

    private void UpdateChannelValues()
    {
        foreach (var (key, value) in _client.StreamValues)
        {
            if (_columnNodes.TryGetValue(key, out var node))
            {
                node.Value = NumericDisplayPolicy.Reading(value);
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
    // .tio document
    // -----------------------------------------------------------------

    private TioDocument _document = new();

    /// <summary>Guards the slider against the value changes we make ourselves.</summary>
    private bool _suppressPlaybackEvents;

    private async void OnOpenLogClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        // Unpackaged apps have no implicit window to parent the dialog to.
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".tio");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        OpenDocument(file.Path);
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
        if (file is null)
        {
            return;
        }

        SaveDocumentTo(file.Path);
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

        return hours > 0
            ? $"{hours}:{minutes:00}:{secs:00}"
            : $"{minutes}:{secs:00}";
    }

    // -----------------------------------------------------------------
    // Settings sidebar (device RPCs)
    // -----------------------------------------------------------------

    private readonly ObservableCollection<SettingRow> _settingRows = [];

    /// <summary>Setting rows by RPC id, so a readback updates in O(1).</summary>
    private readonly Dictionary<string, SettingRow> _settingRowsById = [];

    /// <summary>Guards against control events fired by programmatic updates.</summary>
    private bool _suppressSettingEvents;

    private string _settingsQuery = string.Empty;

    private void RebuildSettingsList()
    {
        _suppressSettingEvents = true;
        try
        {
            _settingRows.Clear();
            _settingRowsById.Clear();

            foreach (var device in _client.ConnectedDevices)
            {
                var matches = device.Rpcs
                    .Where(rpc => rpc.IsVisibleInRpcList && MatchesSettingsQuery(rpc, device))
                    .ToList();

                if (matches.Count == 0)
                {
                    continue;
                }

                // Only label the group when there is more than one device;
                // a single device's name above every setting is just noise.
                if (_client.ConnectedDevices.Count > 1)
                {
                    _settingRows.Add(SettingRow.Header(device.DisplayName));
                }

                foreach (var rpc in matches)
                {
                    var row = new SettingRow(rpc, _client.ValueFor(rpc));
                    _settingRowsById[rpc.Id] = row;
                    _settingRows.Add(row);
                }
            }
        }
        finally
        {
            _suppressSettingEvents = false;
        }

        UpdateSettingsChrome();
    }

    private bool MatchesSettingsQuery(RpcInfo rpc, DeviceInfo device)
    {
        if (_settingsQuery.Length == 0)
        {
            return true;
        }

        return rpc.Name.Contains(_settingsQuery, StringComparison.OrdinalIgnoreCase)
            || rpc.ArgType.Contains(_settingsQuery, StringComparison.OrdinalIgnoreCase)
            || device.DisplayName.Contains(_settingsQuery, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateSettingsChrome()
    {
        var hasRows = _settingRows.Count > 0;
        SettingsList.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        SettingsEmptyText.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;

        SettingsEmptyText.Text = !_client.IsConnected
            ? "Connect a Twinleaf device to list settings."
            : _settingsQuery.Length > 0
                ? $"No settings match “{_settingsQuery}”."
                : "This device reports no settings.";

        ReloadRpcsButton.Visibility = _client.RpcCacheNeedsReload
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReloadRpcsButton.IsEnabled = _client.ReloadableRpcs.Any();
    }

    private void OnSettingsSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _settingsQuery = sender.Text?.Trim() ?? string.Empty;
        RebuildSettingsList();
    }

    private void OnReloadRpcsClicked(object sender, RoutedEventArgs e) => _client.ReloadAllRpcs();

    /// <summary>Applies a readback to its row without disturbing an in-progress edit.</summary>
    private void OnRpcValueUpdated(string rpcId)
    {
        if (!_settingRowsById.TryGetValue(rpcId, out var row) || row.Rpc is null)
        {
            return;
        }

        _suppressSettingEvents = true;
        try
        {
            row.ApplyValue(_client.ValueFor(row.Rpc));
        }
        finally
        {
            _suppressSettingEvents = false;
        }
    }

    private static SettingRow? RowFrom(object sender) =>
        (sender as FrameworkElement)?.Tag as SettingRow;

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
        if (_suppressSettingEvents || sender is not ToggleSwitch toggle)
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
                $"series={frame.Series.Count} points={points}" +
                (points > 0
                    ? $" x=[{frame.Series[0].Points[0].X:G6}..{frame.Series[0].Points[^1].X:G6}]" +
                      $" y0={frame.Series[0].Points[0].Y:G6} rate={frame.Series[0].SampleRate:G6}"
                    : string.Empty));
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

        ConnectButtonText.Text = connected ? "Disconnect" : "Connect";
        DeviceCombo.IsEnabled = !connected;
        UrlBox.IsEnabled = !connected;
        // A loaded log is a fixed recording: there is nothing to record and
        // nothing to pause, and the scrubber is what moves through it.
        RecordButton.IsEnabled = connected && !inspecting;
        PauseButton.IsEnabled = connected && !inspecting;

        PlaybackBar.Visibility = inspecting && _client.HasPlaybackRange
            ? Visibility.Visible
            : Visibility.Collapsed;

        SaveButton.IsEnabled = !_document.IsEmpty;
        SaveAsButton.IsEnabled = !_document.IsEmpty;

        var badges = new List<string> { _document.DisplayName };
        if (_document.SavedPath is null && !_document.IsEmpty)
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

        UpdateSettingsChrome();

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
