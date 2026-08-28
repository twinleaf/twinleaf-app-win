// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        _client.ConnectedDevices.CollectionChanged += (_, _) => RebuildChannelTree();
        _client.StreamValuesUpdated += UpdateChannelValues;
        _client.SelectionChanged += SyncChannelSelection;
        _client.PlotFrameReceived += OnPlotFrame;

        ChannelTree.ItemsSource = _channelRoots;
        Plot.LabelProvider = LabelFor;

        BuildWindowMenu();
        UpdateChrome();

        // Live discovery keeps the picker current without a polling timer.
        _client.SetDiscovery(active: true);
        _client.RefreshDevices();

        Closed += (_, _) =>
        {
            _client.SetDiscovery(active: false);
            _client.Dispose();
        };
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

        _client.Connect(url);
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
        // A null path lets the core pick its own temporary backing file, which
        // is what the macOS app does until the document is saved.
        _client.SetRecording(enabled, logPath: null);
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
    // Plot
    // -----------------------------------------------------------------

    private void OnPlotFrame(PlotFrame frame)
    {
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

        ConnectButtonText.Text = connected ? "Disconnect" : "Connect";
        DeviceCombo.IsEnabled = !connected;
        UrlBox.IsEnabled = !connected;
        RecordButton.IsEnabled = connected;
        PauseButton.IsEnabled = connected;

        StatusText.Text = _client.ErrorMessage ?? _client.StatusMessage;

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
