// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Twinleaf.Interop;

/// <summary>
/// P/Invoke surface for <c>twinleaf_core.dll</c>, the Rust core shared with the
/// macOS app (<c>rust/tio-bridge</c>). This mirrors the Swift app's
/// <c>RustRuntime.swift</c> one-for-one; the authoritative contract is
/// <c>rust/tio-bridge/include/twinleaf_core.h</c>.
///
/// The runtime owns its own worker threads and invokes <see cref="EventCallback"/>
/// from whichever one produced the event, so the callback must never touch UI
/// state directly — <see cref="Core.BridgeClient"/> hops to the dispatcher queue.
/// </summary>
internal static unsafe partial class CoreFfi
{
    /// <summary>cdylib name; cargo emits <c>twinleaf_core.dll</c> on Windows.</summary>
    internal const string Library = "twinleaf_core";

    /// <summary>
    /// Event transport discriminator, matching the <c>EVENT_*</c> constants in
    /// <c>main.rs</c>. Every event arrives as one of these three shapes.
    /// </summary>
    internal enum EventKind : uint
    {
        /// <summary>UTF-8 JSON object with a <c>type</c> field. Fallback for
        /// events the binary encoder does not cover.</summary>
        Json = 0,
        /// <summary>Binary plot frame; see <see cref="PlotFrameDecoder"/>.</summary>
        Plot = 1,
        /// <summary>Binary typed event; see <see cref="TypedEventDecoder"/>.</summary>
        Typed = 2,
    }

    /// <summary>
    /// Callback the Rust runtime invokes for every event. <paramref name="context"/>
    /// is the value handed to <see cref="RuntimeCreate"/> — we pass a pinned
    /// <see cref="GCHandle"/> so the static callback can find its owner.
    /// </summary>
    internal delegate void EventCallback(uint kind, byte* data, nuint len, nuint context);

    /// <summary>
    /// Tagged union for a typed RPC argument, matching <c>TwinleafRpcArg</c>.
    /// Only the field named by <see cref="Tag"/> is read by the core.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RpcArg
    {
        /// <summary>1 = bool, 2 = string, 3 = double, 4 = int, 5 = uint; anything
        /// else means "no argument" (an RPC read rather than a write).</summary>
        public byte Tag;
        public byte BoolValue;
        public long IntValue;
        public ulong UIntValue;
        public double DoubleValue;
        /// <summary>Only read when <see cref="Tag"/> is 2. Must stay alive for
        /// the duration of the call; the core copies it before returning.</summary>
        public byte* StringValue;

        public const byte TagNone = 0;
        public const byte TagBool = 1;
        public const byte TagString = 2;
        public const byte TagDouble = 3;
        public const byte TagInt = 4;
        public const byte TagUInt = 5;
    }

    /// <summary>Export format code for <see cref="RuntimeExportLog"/>.</summary>
    internal enum ExportFormatCode : byte
    {
        Csv = 0,
        Hdf5 = 1,
    }

    // ---------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "twinleaf_runtime_create")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint RuntimeCreate(delegate* unmanaged[Cdecl]<uint, byte*, nuint, nuint, void> callback, nuint context);

    /// <summary>
    /// Stops the runtime and joins its threads. Blocks until the worker exits,
    /// so no further callbacks can fire once this returns — which is what makes
    /// it safe to free the callback's <see cref="GCHandle"/> afterwards.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "twinleaf_runtime_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RuntimeDestroy(nint runtime);

    // ---------------------------------------------------------------------
    // Discovery + connection
    // ---------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "twinleaf_runtime_list_devices")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RuntimeListDevices(nint runtime, byte includeAll);

    /// <summary>
    /// Starts (<paramref name="active"/> != 0) or stops live discovery. While
    /// active the core pushes a full <c>deviceList</c> snapshot whenever the set
    /// of reachable devices changes, so the picker needs no polling.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "twinleaf_runtime_set_discovery")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RuntimeSetDiscovery(nint runtime, byte active, byte includeAll);

    [LibraryImport(Library, EntryPoint = "twinleaf_runtime_connect", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RuntimeConnect(nint runtime, string url, string? route, string? logPath);

    [LibraryImport(Library, EntryPoint = "twinleaf_runtime_disconnect")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RuntimeDisconnect(nint runtime);

    // ---------------------------------------------------------------------
    // Logging, playback, export
    // ---------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "twinleaf_runtime_set_logging", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RuntimeSetLogging(nint runtime, byte enabled, string? logPath);

    [LibraryImport(Library, EntryPoint = "twinleaf_runtime_open_log", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RuntimeOpenLog(nint runtime, string path);

    [LibraryImport(Library, EntryPoint = "twinleaf_runtime_export_log", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RuntimeExportLog(nint runtime, string requestId, string sourcePath, string outputPath, byte format);

    [LibraryImport(Library, EntryPoint = "twinleaf_runtime_set_playback")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RuntimeSetPlayback(nint runtime, double position);

    [LibraryImport(Library, EntryPoint = "twinleaf_runtime_copy_view_data", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RuntimeCopyViewData(nint runtime, string requestId, nuint paneId, byte hasViewportEnd, double viewportEnd);

    // ---------------------------------------------------------------------
    // Commands + RPC
    // ---------------------------------------------------------------------

    /// <summary>
    /// Sends a serialized <c>ClientCommand</c>. This is the escape hatch for the
    /// commands with no dedicated entry point (<c>setActiveColumns</c>,
    /// <c>setView</c>, <c>setPlotPanes</c>, …); returns false if the payload does
    /// not parse as a known command.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "twinleaf_runtime_send_command_json", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool RuntimeSendCommandJson(nint runtime, string json);

    [LibraryImport(Library, EntryPoint = "twinleaf_runtime_call_rpc", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RuntimeCallRpc(nint runtime, string requestId, string route, string name, string? argJson);

    [LibraryImport(Library, EntryPoint = "twinleaf_runtime_call_rpc_value")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RuntimeCallRpcValue(nint runtime, nint requestId, nint route, nint name, RpcArg* arg);

    // ---------------------------------------------------------------------
    // Firmware
    // ---------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "twinleaf_runtime_check_upgrade")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RuntimeCheckUpgrade(nint runtime);

    [LibraryImport(Library, EntryPoint = "twinleaf_runtime_perform_upgrade", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RuntimePerformUpgrade(nint runtime, string route);
}
