// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Twinleaf.Interop;
using Twinleaf.Models;

namespace Twinleaf.Core;

/// <summary>
/// Observable application model driven by the Rust core, mirroring the macOS
/// app's <c>BridgeClient</c>.
///
/// Every mutation happens on the UI thread: <see cref="TwinleafRuntime"/> raises
/// events from Rust worker threads, and this class hops them onto the window's
/// <see cref="DispatcherQueue"/> before touching any observable state. That keeps
/// the collections safe to bind directly.
/// </summary>
public sealed class BridgeClient : INotifyPropertyChanged, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly TwinleafRuntime _runtime;
    private int _requestCounter;

    /// <summary>Columns the user has ticked, in selection order.</summary>
    private readonly List<ColumnKey> _selectedColumns = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when a pane receives a new decimated frame.</summary>
    public event Action<PlotFrame>? PlotFrameReceived;

    public BridgeClient(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _runtime = new TwinleafRuntime();
        _runtime.EventReceived += OnCoreEvent;
    }

    // -----------------------------------------------------------------
    // Observable state
    // -----------------------------------------------------------------

    /// <summary>Devices the core can currently reach (serial + mDNS).</summary>
    public ObservableCollection<AvailableDevice> AvailableDevices { get; } = [];

    /// <summary>Connected devices with their stream/RPC metadata.</summary>
    public ObservableCollection<DeviceInfo> ConnectedDevices { get; } = [];

    /// <summary>Device log lines, newest last, capped by <see cref="MaxLogMessages"/>.</summary>
    public ObservableCollection<LogMessage> LogMessages { get; } = [];

    private const int MaxLogMessages = 2000;

    private ConnectionPhase _phase = ConnectionPhase.Idle;
    public ConnectionPhase Phase
    {
        get => _phase;
        private set => Set(ref _phase, value);
    }

    private string _statusMessage = "Idle";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => Set(ref _errorMessage, value);
    }

    private string _connectedUrl = string.Empty;
    public string ConnectedUrl
    {
        get => _connectedUrl;
        private set => Set(ref _connectedUrl, value);
    }

    public bool IsConnected => Phase is ConnectionPhase.Connected or ConnectionPhase.Streaming
        or ConnectionPhase.Discovering or ConnectionPhase.Metadata;

    private ViewConfig _view = new();
    public ViewConfig View
    {
        get => _view;
        private set => Set(ref _view, value);
    }

    private LogProgress? _logProgress;
    public LogProgress? LogProgress
    {
        get => _logProgress;
        private set => Set(ref _logProgress, value);
    }

    private PlaybackState? _playback;
    public PlaybackState? Playback
    {
        get => _playback;
        private set => Set(ref _playback, value);
    }

    private bool _isRecording;
    public bool IsRecording
    {
        get => _isRecording;
        private set => Set(ref _isRecording, value);
    }

    /// <summary>Latest scalar value per column, for the sidebar readout.</summary>
    public IReadOnlyDictionary<ColumnKey, double> StreamValues => _streamValues;
    private readonly Dictionary<ColumnKey, double> _streamValues = [];

    /// <summary>Raised after <see cref="StreamValues"/> is refreshed.</summary>
    public event Action? StreamValuesUpdated;

    public IReadOnlyList<ColumnKey> SelectedColumns => _selectedColumns;

    /// <summary>Raised when the selected-column set changes, so the sidebar can re-render ticks.</summary>
    public event Action? SelectionChanged;

    // -----------------------------------------------------------------
    // Commands
    // -----------------------------------------------------------------

    /// <summary>One-shot device enumeration.</summary>
    public void RefreshDevices(bool includeAll = false) => _runtime.ListDevices(includeAll);

    /// <summary>
    /// Live discovery. While active the core pushes a fresh device list whenever
    /// the reachable set changes, so the picker needs no polling timer.
    /// </summary>
    public void SetDiscovery(bool active, bool includeAll = false) =>
        _runtime.SetDiscovery(active, includeAll);

    public void Connect(string url, string? route = null, string? logPath = null)
    {
        ErrorMessage = null;
        Phase = ConnectionPhase.Connecting;
        StatusMessage = $"Connecting to {url}…";
        ConnectedUrl = url;
        _runtime.Connect(url, route, logPath);
    }

    public void Disconnect()
    {
        _runtime.Disconnect();
        Phase = ConnectionPhase.Idle;
        StatusMessage = "Disconnected";
        ConnectedUrl = string.Empty;
        ConnectedDevices.Clear();
        _selectedColumns.Clear();
        _streamValues.Clear();
        SelectionChanged?.Invoke();
        OnPropertyChanged(nameof(IsConnected));
    }

    public void SetRecording(bool enabled, string? logPath)
    {
        _runtime.SetLogging(enabled, logPath);
        IsRecording = enabled;
    }

    public void OpenLog(string path)
    {
        ErrorMessage = null;
        StatusMessage = $"Opening {path}…";
        _runtime.OpenLog(path);
    }

    public void SetPlaybackPosition(double position) => _runtime.SetPlayback(position);

    public string ExportLog(string sourcePath, string outputPath, ExportFormat format)
    {
        var requestId = NextRequestId();
        _runtime.ExportLog(requestId, sourcePath, outputPath, format);
        return requestId;
    }

    public string CopyViewData(int paneId, double? viewportEnd)
    {
        var requestId = NextRequestId();
        _runtime.CopyViewData(requestId, paneId, viewportEnd);
        return requestId;
    }

    public string CallRpc(string route, string name, JsonValue? argument)
    {
        var requestId = NextRequestId();
        _runtime.CallRpc(requestId, route, name, argument);
        return requestId;
    }

    public void CheckUpgrade() => _runtime.CheckUpgrade();

    public void PerformUpgrade(string route) => _runtime.PerformUpgrade(route);

    /// <summary>Adds or removes a column from the plotted set and re-pushes to the core.</summary>
    public void ToggleColumn(ColumnKey key)
    {
        if (!_selectedColumns.Remove(key))
        {
            _selectedColumns.Add(key);
        }

        PushSelection();
    }

    public bool IsSelected(ColumnKey key) => _selectedColumns.Contains(key);

    public void ClearSelection()
    {
        if (_selectedColumns.Count == 0)
        {
            return;
        }

        _selectedColumns.Clear();
        PushSelection();
    }

    private void PushSelection()
    {
        _runtime.SetActiveColumns(_selectedColumns);
        // A single pane for now; the multi-pane layout pushes one entry per pane.
        _runtime.SetPlotPanes([new TwinleafRuntime.PanePush(0, View, _selectedColumns.ToList())]);
        SelectionChanged?.Invoke();
    }

    /// <summary>Updates plot settings and re-pushes them to the core.</summary>
    public void UpdateView(Func<ViewConfig, ViewConfig> transform)
    {
        View = transform(View);
        _runtime.SetView(View);
        // Panes carry their own copy of the view, so they need the update too.
        _runtime.SetPlotPanes([new TwinleafRuntime.PanePush(0, View, _selectedColumns.ToList())]);
    }

    private string NextRequestId() =>
        $"req-{Interlocked.Increment(ref _requestCounter)}";

    // -----------------------------------------------------------------
    // Event handling
    // -----------------------------------------------------------------

    /// <summary>
    /// Called on a Rust worker thread. Hops to the UI thread before touching any
    /// observable state.
    /// </summary>
    private void OnCoreEvent(CoreEvent coreEvent)
    {
        if (!_dispatcher.TryEnqueue(() => Apply(coreEvent)))
        {
            // The dispatcher shuts down before the runtime during teardown;
            // dropping late events is correct, not an error worth surfacing.
            AppLog.Debug($"dropped event after dispatcher shutdown: {coreEvent.GetType().Name}");
        }
    }

    private void Apply(CoreEvent coreEvent)
    {
        switch (coreEvent)
        {
            case CoreEvent.Status status:
                ApplyStatus(status);
                break;

            case CoreEvent.Error error:
                ErrorMessage = error.Message;
                StatusMessage = error.Message;
                if (Phase is ConnectionPhase.Connecting)
                {
                    Phase = ConnectionPhase.Failed;
                    OnPropertyChanged(nameof(IsConnected));
                }

                AppLog.Error($"core error: {error.Message}");
                break;

            case CoreEvent.Debug debug:
                AppLog.Debug(debug.Message);
                break;

            case CoreEvent.DeviceList list:
                ReplaceAll(AvailableDevices, list.Devices);
                break;

            case CoreEvent.Metadata metadata:
                ReplaceAll(ConnectedDevices, metadata.Devices);
                PruneSelection();
                break;

            case CoreEvent.StreamValues values:
                foreach (var (key, value) in values.Values)
                {
                    _streamValues[key] = value;
                }

                StreamValuesUpdated?.Invoke();
                break;

            case CoreEvent.ActiveColumns active:
                // The core is authoritative: it may drop columns it cannot serve.
                //
                // Careful when derived channels get wired up: the typed-event
                // column key has no derivation field (only the plot frame does),
                // so every key here decodes as a raw column. Taking this list
                // wholesale would silently rewrite a selected derived channel
                // into its source. Reconcile against the outgoing selection then
                // rather than replacing it.
                _selectedColumns.Clear();
                _selectedColumns.AddRange(active.Columns);
                SelectionChanged?.Invoke();
                break;

            case CoreEvent.Plot plot:
                PlotFrameReceived?.Invoke(plot.Frame);
                break;

            case CoreEvent.Progress progress:
                LogProgress = progress.State;
                break;

            case CoreEvent.Playback playback:
                Playback = playback.State;
                break;

            case CoreEvent.DeviceLogMessage message:
                AppendLogMessage(message.Message);
                break;

            case CoreEvent.DeviceEvent device:
                AppLog.Debug($"device {device.Route}: {device.Event}");
                break;

            case CoreEvent.RpcResult result:
                RpcResultReceived?.Invoke(result);
                break;

            case CoreEvent.RpcInvalidated invalidated:
                RpcInvalidatedReceived?.Invoke(invalidated);
                break;

            case CoreEvent.ExportResult export:
                ExportResultReceived?.Invoke(export);
                break;

            case CoreEvent.ViewData viewData:
                ViewDataReceived?.Invoke(viewData);
                break;

            case CoreEvent.ProxyEvent proxy:
                AppLog.Debug($"proxy: {proxy.Event}");
                break;

            case CoreEvent.Json json:
                JsonEventReceived?.Invoke(json);
                break;
        }
    }

    /// <summary>Firmware upgrade + stream-health events arrive over the JSON transport.</summary>
    public event Action<CoreEvent.Json>? JsonEventReceived;

    public event Action<CoreEvent.RpcResult>? RpcResultReceived;
    public event Action<CoreEvent.RpcInvalidated>? RpcInvalidatedReceived;
    public event Action<CoreEvent.ExportResult>? ExportResultReceived;
    public event Action<CoreEvent.ViewData>? ViewDataReceived;

    private void ApplyStatus(CoreEvent.Status status)
    {
        StatusMessage = status.Message;

        var phase = status.State switch
        {
            "ready" or "idle" or "disconnected" => ConnectionPhase.Idle,
            "connecting" => ConnectionPhase.Connecting,
            "connected" => ConnectionPhase.Connected,
            "discovering" => ConnectionPhase.Discovering,
            "metadata" => ConnectionPhase.Metadata,
            "streaming" => ConnectionPhase.Streaming,
            "cancelled" => ConnectionPhase.Cancelled,
            "failed" or "error" => ConnectionPhase.Failed,
            _ => (ConnectionPhase?)null,
        };

        if (phase is { } resolved)
        {
            Phase = resolved;
            OnPropertyChanged(nameof(IsConnected));
        }
    }

    private void AppendLogMessage(LogMessage message)
    {
        LogMessages.Add(message);
        while (LogMessages.Count > MaxLogMessages)
        {
            LogMessages.RemoveAt(0);
        }
    }

    /// <summary>
    /// Drops selected columns that no longer exist after a metadata refresh —
    /// a device can reboot with a different stream layout mid-session.
    /// </summary>
    private void PruneSelection()
    {
        if (_selectedColumns.Count == 0)
        {
            return;
        }

        var known = ConnectedDevices
            .SelectMany(device => device.Streams)
            .SelectMany(stream => stream.Columns)
            .Select(column => column.Key)
            .ToHashSet();

        // Derived channels hang off a raw column, so validate against the source.
        var removed = _selectedColumns.RemoveAll(key => !known.Contains(key.Source));
        if (removed > 0)
        {
            PushSelection();
        }
    }

    private static void ReplaceAll<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    // -----------------------------------------------------------------
    // INotifyPropertyChanged
    // -----------------------------------------------------------------

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _runtime.EventReceived -= OnCoreEvent;
        _runtime.Dispose();
    }
}
