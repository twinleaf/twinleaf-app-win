// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

namespace Twinleaf.Models;

/// <summary>
/// How a channel is computed when it is not received from a device.
///
/// The tag rides on <see cref="ColumnKey"/> so a derived channel is addressable
/// exactly like a raw column — panes, legends and exports need no special case —
/// while clearing the tag recovers the source key.
/// </summary>
public enum Derivation
{
    /// <summary>Robust white-noise ASD of the source column's spectrum, sampled
    /// over time. One point per cadence interval, in <c>source units/√Hz</c>.</summary>
    NoiseFloor,
}

public static class DerivationExtensions
{
    /// <summary>Wire code used by the binary plot frame (0 means "raw column").</summary>
    public static byte ToCode(this Derivation? derivation) => derivation switch
    {
        null => 0,
        Derivation.NoiseFloor => 1,
        _ => 0,
    };

    public static Derivation? FromCode(byte code) => code switch
    {
        1 => Derivation.NoiseFloor,
        _ => null,
    };

    /// <summary>Serialized form understood by the Rust core's <c>ColumnKeyDto</c>.</summary>
    public static string? ToJsonName(this Derivation? derivation) => derivation switch
    {
        Derivation.NoiseFloor => "noiseFloor",
        _ => null,
    };

    public static int SortOrder(this Derivation derivation) => derivation switch
    {
        Derivation.NoiseFloor => 0,
        _ => 0,
    };

    public static string Title(this Derivation derivation) => derivation switch
    {
        Derivation.NoiseFloor => "Noise Floor",
        _ => "Derived",
    };

    /// <summary>Units for a channel derived from a source measured in <paramref name="units"/>.</summary>
    public static string Units(this Derivation derivation, string units) => derivation switch
    {
        Derivation.NoiseFloor => string.IsNullOrEmpty(units) ? "1/√Hz" : $"{units}/√Hz",
        _ => units,
    };

    public static string Label(this Derivation derivation, string label) => derivation switch
    {
        Derivation.NoiseFloor => $"{label} noise floor",
        _ => label,
    };
}

/// <summary>
/// Address of one plottable channel. Value semantics — it is used as a dictionary
/// key throughout, so it is a readonly record struct rather than a class.
/// </summary>
public readonly record struct ColumnKey(
    string Route,
    byte StreamId,
    int ColumnIndex,
    Derivation? Derivation = null) : IComparable<ColumnKey>
{
    public bool IsDerived => Derivation is not null;

    /// <summary>The raw column this key was derived from; identity for a raw key.</summary>
    public ColumnKey Source => new(Route, StreamId, ColumnIndex);

    public ColumnKey Derived(Derivation derivation) => this with { Derivation = derivation };

    public int CompareTo(ColumnKey other)
    {
        var route = string.CompareOrdinal(Route, other.Route);
        if (route != 0) return route;
        if (StreamId != other.StreamId) return StreamId.CompareTo(other.StreamId);
        if (ColumnIndex != other.ColumnIndex) return ColumnIndex.CompareTo(other.ColumnIndex);
        // Raw column first, then its derived channels in a stable order.
        var lhs = Derivation?.SortOrder() ?? -1;
        var rhs = other.Derivation?.SortOrder() ?? -1;
        return lhs.CompareTo(rhs);
    }

    public override string ToString() =>
        IsDerived ? $"{Route}#{StreamId}.{ColumnIndex}:{Derivation}" : $"{Route}#{StreamId}.{ColumnIndex}";
}

/// <summary>One route (sub-device) advertised by a discovered device.</summary>
public sealed record AvailableDeviceRoute(string Route, string? Name)
{
    public string DisplayName => string.IsNullOrEmpty(Name) ? "(no name)" : Name!;

    public bool IsRoot => string.IsNullOrEmpty(Route) || Route == "/";

    public int Depth => IsRoot
        ? 0
        : Route.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>A device the core found via serial enumeration or mDNS.</summary>
public sealed record AvailableDevice(
    string Url,
    string Label,
    string Kind,
    string Detail,
    IReadOnlyList<AvailableDeviceRoute> Routes)
{
    public string Id => Url;
}

public sealed record DeviceMeta(
    string SerialNumber,
    string FirmwareHash,
    int NStreams,
    uint SessionId,
    string Name);

public sealed record ColumnInfo(
    ColumnKey Key,
    string Name,
    string Units,
    string DataType,
    string Description,
    double? DisplayValue)
{
    /// <summary>Name with units appended, as shown in the sidebar and legend.</summary>
    public string DisplayLabel => string.IsNullOrEmpty(Units) ? Name : $"{Name} ({Units})";
}

public sealed record StreamInfo(
    byte StreamId,
    string Name,
    int NColumns,
    int SampleSize,
    double EffectiveSamplingRate,
    IReadOnlyList<ColumnInfo> Columns);

public sealed record DeviceInfo(
    string Url,
    string Route,
    DeviceMeta Meta,
    IReadOnlyList<StreamInfo> Streams,
    IReadOnlyList<RpcInfo> Rpcs)
{
    public string Id => $"{Url}#{Route}";

    /// <summary>Sidebar title: the device's configured name, falling back to its serial number.</summary>
    public string DisplayName =>
        !string.IsNullOrEmpty(Meta.Name) ? Meta.Name
        : !string.IsNullOrEmpty(Meta.SerialNumber) ? Meta.SerialNumber
        : Route;
}

public sealed record RpcInfo(
    string Route,
    string Name,
    int Size,
    string Permissions,
    string ArgType,
    bool Readable,
    bool Writable,
    bool Persistent,
    bool Unknown,
    JsonValue? Value)
{
    public string Id => $"{Route}#{Name}";
}

/// <summary>What the plot is showing.</summary>
public enum PlotMode
{
    Timeseries,
    Fft,
}

public enum VerticalAxisMode
{
    Auto,
    Manual,
}

public enum DetrendMethod
{
    None,
    Mean,
    Linear,
    Quadratic,
}

public enum DecimationMethod
{
    /// <summary>Send every sample in the window; only usable at low rates.</summary>
    None,
    /// <summary>FPCS-style min/max decimation — the same strategy Trendline uses.</summary>
    Fpcs,
}

public enum ExportFormat
{
    Csv,
    Hdf5,
}

/// <summary>A single decimated plot point.</summary>
public readonly record struct PlotPoint(double X, double Y);

/// <summary>One decimated series inside a <see cref="PlotFrame"/>.</summary>
public sealed record PlotSeries(
    ColumnKey Key,
    double SampleRate,
    IReadOnlyList<PlotPoint> Points,
    bool OutsideWindow,
    bool WarmingUp,
    double? NoiseFloor);

/// <summary>
/// One pane's worth of already-decimated data, decoded from the binary plot
/// frame. This is the hot path: the core sends these continuously while
/// streaming, so decoding allocates one array per series and nothing else.
/// </summary>
public sealed record PlotFrame(
    int PaneId,
    PlotMode Mode,
    double? ViewportEnd,
    IReadOnlyList<PlotSeries> Series);

/// <summary>Connection lifecycle, mirroring the Swift app's ConnectionProgress.</summary>
public enum ConnectionPhase
{
    Idle,
    Connecting,
    Connected,
    Discovering,
    Metadata,
    Streaming,
    Cancelled,
    Failed,
}

public sealed record LogMessage(string Route, double TimestampSeconds, string Message)
{
    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds((long)(TimestampSeconds * 1000.0));
}

/// <summary>Playback window for an opened <c>.tio</c> log.</summary>
public sealed record PlaybackState(
    double Start,
    double End,
    double Position,
    double? RecordingStart,
    double? TimeReferenceStart)
{
    public double Duration => Math.Max(0.0, End - Start);
}

/// <summary>Live recording counters.</summary>
public sealed record LogProgress(
    ulong Packets,
    ulong Bytes,
    ulong? FileBytes,
    double? StartSeconds,
    double? ElapsedSeconds,
    double? TimeReferenceStart,
    ulong? SerializeErrors);
