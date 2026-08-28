// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Twinleaf.Models;

// System.Text.Json.Nodes also defines a JsonValue; this file needs both
// namespaces, so the app's own type gets the unqualified name.
using JsonValue = Twinleaf.Models.JsonValue;

namespace Twinleaf.Interop;

/// <summary>
/// Owns one Rust core instance and turns its C callback into ordinary .NET
/// events. This is the Windows counterpart of the macOS app's
/// <c>RustRuntime.swift</c>.
///
/// Threading: the core invokes the callback from whichever worker thread
/// produced the event, and the payload pointer is only valid for the duration
/// of that call. Events are therefore decoded synchronously on the Rust thread
/// — decoding is pure and allocation-light — and the resulting immutable
/// <see cref="CoreEvent"/> is raised to subscribers, who marshal to the UI
/// thread themselves.
/// </summary>
public sealed unsafe class TwinleafRuntime : IDisposable
{
    private nint _handle;
    private GCHandle _selfHandle;
    private int _disposed;

    /// <summary>
    /// Raised for every decoded event, on a Rust worker thread. Handlers must
    /// not touch UI state directly.
    /// </summary>
    public event Action<CoreEvent>? EventReceived;

    public bool IsValid => _handle != nint.Zero;

    public TwinleafRuntime()
    {
        // Pinned so the raw context value stays a stable key back to this
        // instance for the whole lifetime of the Rust runtime.
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        var context = (nuint)(nint)GCHandle.ToIntPtr(_selfHandle);

        _handle = CoreFfi.RuntimeCreate(&OnCoreEvent, context);
        if (_handle == nint.Zero)
        {
            _selfHandle.Free();
            throw new InvalidOperationException(
                "twinleaf_runtime_create returned null: the Rust core failed to start.");
        }
    }

    /// <summary>
    /// The unmanaged entry point handed to Rust. Must never let an exception
    /// escape — unwinding across the FFI boundary into Rust is undefined
    /// behaviour and would take the process down.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnCoreEvent(uint kind, byte* data, nuint len, nuint context)
    {
        try
        {
            if (context == 0)
            {
                return;
            }

            var handle = GCHandle.FromIntPtr((nint)context);
            if (handle.Target is not TwinleafRuntime runtime)
            {
                return;
            }

            var payload = data is null || len == 0
                ? ReadOnlySpan<byte>.Empty
                : new ReadOnlySpan<byte>(data, checked((int)len));

            var decoded = EventDecoder.Decode((CoreFfi.EventKind)kind, payload);
            if (decoded is not null)
            {
                runtime.EventReceived?.Invoke(decoded);
            }
        }
        catch (Exception exception)
        {
            // Swallow: there is no Rust-side error channel, and propagating
            // would unwind into Rust.
            try
            {
                AppLog.Error($"event callback failed: {exception}");
            }
            catch
            {
                // Logging itself failing must not escape either.
            }
        }
    }

    // -----------------------------------------------------------------
    // Commands
    // -----------------------------------------------------------------

    public void ListDevices(bool includeAll)
    {
        if (IsValid) CoreFfi.RuntimeListDevices(_handle, includeAll ? (byte)1 : (byte)0);
    }

    public void SetDiscovery(bool active, bool includeAll)
    {
        if (IsValid) CoreFfi.RuntimeSetDiscovery(_handle, active ? (byte)1 : (byte)0, includeAll ? (byte)1 : (byte)0);
    }

    public void Connect(string url, string? route = null, string? logPath = null)
    {
        if (IsValid) CoreFfi.RuntimeConnect(_handle, url, route, logPath);
    }

    public void Disconnect()
    {
        if (IsValid) CoreFfi.RuntimeDisconnect(_handle);
    }

    public void SetLogging(bool enabled, string? logPath)
    {
        if (IsValid) CoreFfi.RuntimeSetLogging(_handle, enabled ? (byte)1 : (byte)0, logPath);
    }

    public void OpenLog(string path)
    {
        if (IsValid) CoreFfi.RuntimeOpenLog(_handle, path);
    }

    public void ExportLog(string requestId, string sourcePath, string outputPath, ExportFormat format)
    {
        if (IsValid)
        {
            var code = format == ExportFormat.Hdf5
                ? (byte)CoreFfi.ExportFormatCode.Hdf5
                : (byte)CoreFfi.ExportFormatCode.Csv;
            CoreFfi.RuntimeExportLog(_handle, requestId, sourcePath, outputPath, code);
        }
    }

    public void SetPlayback(double position)
    {
        if (IsValid) CoreFfi.RuntimeSetPlayback(_handle, position);
    }

    public void CopyViewData(string requestId, int paneId, double? viewportEnd)
    {
        if (!IsValid) return;
        CoreFfi.RuntimeCopyViewData(
            _handle,
            requestId,
            (nuint)Math.Max(0, paneId),
            viewportEnd.HasValue ? (byte)1 : (byte)0,
            viewportEnd ?? 0.0);
    }

    public void CallRpc(string requestId, string route, string name, JsonValue? argument)
    {
        if (IsValid) CoreFfi.RuntimeCallRpc(_handle, requestId, route, name, argument?.ToJsonString());
    }

    public void CheckUpgrade()
    {
        if (IsValid) CoreFfi.RuntimeCheckUpgrade(_handle);
    }

    public void PerformUpgrade(string route)
    {
        if (IsValid) CoreFfi.RuntimePerformUpgrade(_handle, route);
    }

    // -----------------------------------------------------------------
    // JSON commands
    //
    // The core exposes a handful of commands only through the JSON escape
    // hatch. Field names are camelCase to match the ClientCommand enum's
    // serde attributes.
    // -----------------------------------------------------------------

    /// <summary>Sets which columns the core should stream and decimate.</summary>
    public void SetActiveColumns(IReadOnlyCollection<ColumnKey> columns)
    {
        var array = new JsonArray();
        foreach (var column in columns)
        {
            array.Add(ColumnKeyNode(column));
        }

        SendCommand(new JsonObject
        {
            ["type"] = "setActiveColumns",
            ["columns"] = array,
        });
    }

    /// <summary>
    /// Pushes the pane layout. Every pane carries its own <see cref="ViewConfig"/>,
    /// so panes can sit in different modes (one timeseries, one FFT) and the core
    /// decimates each accordingly.
    /// </summary>
    public void SetPlotPanes(IReadOnlyList<PanePush> panes)
    {
        var array = new JsonArray();
        for (var index = 0; index < panes.Count; index++)
        {
            var pane = panes[index];
            var columns = new JsonArray();
            foreach (var column in pane.Columns)
            {
                columns.Add(ColumnKeyNode(column));
            }

            array.Add(new JsonObject
            {
                ["id"] = pane.Id,
                ["view"] = pane.View.ToNode(),
                ["columns"] = columns,
            });
        }

        SendCommand(new JsonObject
        {
            ["type"] = "setPlotPanes",
            ["panes"] = array,
        });
    }

    /// <summary>One pane's contribution to a <c>setPlotPanes</c> push.</summary>
    public readonly record struct PanePush(int Id, ViewConfig View, IReadOnlyList<ColumnKey> Columns);

    /// <summary>Pushes plot-window/FFT settings that change how the core decimates.</summary>
    public void SetView(ViewConfig view)
    {
        SendCommand(new JsonObject
        {
            ["type"] = "setView",
            ["view"] = view.ToNode(),
        });
    }

    private static JsonObject ColumnKeyNode(ColumnKey key)
    {
        var node = new JsonObject
        {
            ["route"] = key.Route,
            ["streamId"] = key.StreamId,
            ["columnIndex"] = key.ColumnIndex,
        };

        var derivation = key.Derivation.ToJsonName();
        if (derivation is not null)
        {
            node["derivation"] = derivation;
        }

        return node;
    }

    private void SendCommand(JsonObject command)
    {
        if (!IsValid)
        {
            return;
        }

        var json = command.ToJsonString();
        if (!CoreFfi.RuntimeSendCommandJson(_handle, json))
        {
            AppLog.Error($"core rejected command: {json}");
        }
    }

    // -----------------------------------------------------------------
    // Teardown
    // -----------------------------------------------------------------

    public void Dispose()
    {
        // Guard against a second Dispose racing the first: destroy must run
        // exactly once, and the GCHandle must outlive the last callback.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var handle = Interlocked.Exchange(ref _handle, nint.Zero);
        if (handle != nint.Zero)
        {
            // Blocks until the runtime's threads have joined, so no callback
            // can be in flight once this returns.
            CoreFfi.RuntimeDestroy(handle);
        }

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }
}
