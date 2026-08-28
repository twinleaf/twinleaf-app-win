// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text.Json;
using Twinleaf.Models;

namespace Twinleaf.Interop;

/// <summary>
/// One decoded event from the Rust core. The binary transport covers the
/// sixteen event shapes the UI consumes on the hot path; anything else arrives
/// as <see cref="Json"/> and is handled by field name.
/// </summary>
public abstract record CoreEvent
{
    /// <summary>Runtime lifecycle: <c>ready</c>, <c>connecting</c>, <c>streaming</c>, …</summary>
    public sealed record Status(string State, string Message) : CoreEvent;

    public sealed record Error(string Message) : CoreEvent;

    public sealed record Debug(string Message) : CoreEvent;

    /// <summary>Full snapshot of reachable devices; replaces the previous list.</summary>
    public sealed record DeviceList(IReadOnlyList<AvailableDevice> Devices) : CoreEvent;

    /// <summary>Stream/RPC metadata for every connected device on the tree.</summary>
    public sealed record Metadata(IReadOnlyList<DeviceInfo> Devices) : CoreEvent;

    public sealed record Playback(PlaybackState State) : CoreEvent;

    public sealed record Progress(LogProgress State) : CoreEvent;

    /// <summary>Latest scalar value per column, for the sidebar readout.</summary>
    public sealed record StreamValues(IReadOnlyList<KeyValuePair<ColumnKey, double>> Values) : CoreEvent;

    // Failure text is `ErrorMessage`, not `Error`: a positional parameter named
    // Error would collide with the nested `CoreEvent.Error` record above.

    /// <summary>Result of a <c>copyViewData</c> request (clipboard export).</summary>
    public sealed record ViewData(string RequestId, bool Ok, string? Text, ulong? Rows, string? ErrorMessage) : CoreEvent;

    public sealed record ExportResult(
        string RequestId,
        bool Ok,
        string? OutputPath,
        string? Format,
        ulong? Rows,
        ulong? Bytes,
        string? ErrorMessage) : CoreEvent;

    /// <summary>A log line emitted by the device itself.</summary>
    public sealed record DeviceLogMessage(LogMessage Message) : CoreEvent;

    public sealed record RpcResult(
        string RequestId,
        bool Ok,
        string? Route,
        string? Name,
        JsonValue? Value,
        string? ErrorMessage) : CoreEvent;

    /// <summary>A device-side change invalidated a cached RPC value; re-read it.</summary>
    public sealed record RpcInvalidated(string Route, string? Name, ulong? RpcId) : CoreEvent;

    /// <summary>Per-device lifecycle (e.g. <c>reset</c>, <c>disconnected</c>).</summary>
    public sealed record DeviceEvent(string Route, string Event) : CoreEvent;

    /// <summary>The core's authoritative set of columns it is streaming.</summary>
    public sealed record ActiveColumns(IReadOnlyList<ColumnKey> Columns) : CoreEvent;

    public sealed record ProxyEvent(string Event) : CoreEvent;

    /// <summary>Decimated data for one pane. The streaming hot path.</summary>
    public sealed record Plot(PlotFrame Frame) : CoreEvent;

    /// <summary>
    /// An event with no binary encoding — firmware upgrade status/progress and
    /// stream-health diagnostics arrive this way. <see cref="Type"/> is the
    /// payload's <c>type</c> discriminator.
    /// </summary>
    public sealed record Json(string Type, JsonElement Payload) : CoreEvent;
}
