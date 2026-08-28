// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.Json;
using Twinleaf.Models;

namespace Twinleaf.Interop;

/// <summary>
/// Decodes the Rust core's three event transports into <see cref="CoreEvent"/>.
///
/// The layouts here are the mirror image of the encoders in
/// <c>rust/tio-bridge/src/main.rs</c> (<c>encode_*_event</c>,
/// <c>write_plot_payload</c>). Field order is load-bearing: the wire format
/// carries no names, so any reordering silently misreads every later field.
/// </summary>
internal static class EventDecoder
{
    // Typed event discriminators; must match the TYPED_* constants in main.rs.
    private const ushort TypedStatus = 1;
    private const ushort TypedError = 2;
    private const ushort TypedDebug = 3;
    private const ushort TypedDeviceList = 4;
    private const ushort TypedMetadata = 5;
    private const ushort TypedPlayback = 6;
    private const ushort TypedLogProgress = 7;
    private const ushort TypedStreamValues = 8;
    private const ushort TypedViewData = 9;
    private const ushort TypedExportResult = 10;
    private const ushort TypedLogMessage = 11;
    private const ushort TypedRpcResult = 12;
    private const ushort TypedRpcInvalidated = 13;
    private const ushort TypedDeviceEvent = 14;
    private const ushort TypedActiveColumns = 15;
    private const ushort TypedProxyEvent = 16;

    /// <summary>
    /// Entry point for the FFI callback. Returns null for a payload this build
    /// does not understand, which the caller logs and drops.
    /// </summary>
    internal static CoreEvent? Decode(CoreFfi.EventKind kind, ReadOnlySpan<byte> data) => kind switch
    {
        CoreFfi.EventKind.Typed => DecodeTyped(data),
        CoreFfi.EventKind.Plot => new CoreEvent.Plot(DecodePlotFrame(data)),
        CoreFfi.EventKind.Json => DecodeJson(data),
        _ => null,
    };

    // -----------------------------------------------------------------
    // Typed binary events
    // -----------------------------------------------------------------

    private static CoreEvent? DecodeTyped(ReadOnlySpan<byte> data)
    {
        var reader = new BinaryEventReader(data);
        var type = reader.ReadU16();

        return type switch
        {
            TypedStatus => new CoreEvent.Status(reader.ReadString(), reader.ReadString()),
            TypedError => new CoreEvent.Error(reader.ReadString()),
            TypedDebug => new CoreEvent.Debug(reader.ReadString()),
            TypedDeviceList => new CoreEvent.DeviceList(ReadAvailableDevices(ref reader)),
            TypedMetadata => new CoreEvent.Metadata(ReadDevices(ref reader)),
            TypedPlayback => ReadPlayback(ref reader),
            TypedLogProgress => ReadLogProgress(ref reader),
            TypedStreamValues => ReadStreamValues(ref reader),
            TypedViewData => ReadViewData(ref reader),
            TypedExportResult => ReadExportResult(ref reader),
            TypedLogMessage => new CoreEvent.DeviceLogMessage(
                new LogMessage(reader.ReadString(), reader.ReadF64(), reader.ReadString())),
            TypedRpcResult => ReadRpcResult(ref reader),
            TypedRpcInvalidated => new CoreEvent.RpcInvalidated(
                reader.ReadString(), reader.ReadOptionalString(), reader.ReadOptionalU64()),
            TypedDeviceEvent => new CoreEvent.DeviceEvent(reader.ReadString(), reader.ReadString()),
            TypedActiveColumns => ReadActiveColumns(ref reader),
            TypedProxyEvent => new CoreEvent.ProxyEvent(reader.ReadString()),
            _ => null,
        };
    }

    private static CoreEvent ReadPlayback(ref BinaryEventReader reader) =>
        new CoreEvent.Playback(new PlaybackState(
            Start: reader.ReadF64(),
            End: reader.ReadF64(),
            Position: reader.ReadF64(),
            RecordingStart: reader.ReadOptionalF64(),
            TimeReferenceStart: reader.ReadOptionalF64()));

    private static CoreEvent ReadLogProgress(ref BinaryEventReader reader) =>
        new CoreEvent.Progress(new LogProgress(
            Packets: reader.ReadU64(),
            Bytes: reader.ReadU64(),
            FileBytes: reader.ReadOptionalU64(),
            StartSeconds: reader.ReadOptionalF64(),
            ElapsedSeconds: reader.ReadOptionalF64(),
            TimeReferenceStart: reader.ReadOptionalF64(),
            SerializeErrors: reader.ReadOptionalU64()));

    private static CoreEvent ReadStreamValues(ref BinaryEventReader reader)
    {
        var count = reader.ReadLength();
        var values = new List<KeyValuePair<ColumnKey, double>>(count);
        for (var index = 0; index < count; index++)
        {
            var key = ReadColumnKey(ref reader);
            values.Add(new KeyValuePair<ColumnKey, double>(key, reader.ReadF64()));
        }

        return new CoreEvent.StreamValues(values);
    }

    private static CoreEvent ReadViewData(ref BinaryEventReader reader) =>
        new CoreEvent.ViewData(
            RequestId: reader.ReadString(),
            Ok: reader.ReadBool(),
            Text: reader.ReadOptionalString(),
            Rows: reader.ReadOptionalU64(),
            ErrorMessage: reader.ReadOptionalString());

    private static CoreEvent ReadExportResult(ref BinaryEventReader reader) =>
        new CoreEvent.ExportResult(
            RequestId: reader.ReadString(),
            Ok: reader.ReadBool(),
            OutputPath: reader.ReadOptionalString(),
            Format: reader.ReadOptionalString(),
            Rows: reader.ReadOptionalU64(),
            Bytes: reader.ReadOptionalU64(),
            ErrorMessage: reader.ReadOptionalString());

    private static CoreEvent ReadRpcResult(ref BinaryEventReader reader) =>
        new CoreEvent.RpcResult(
            RequestId: reader.ReadString(),
            Ok: reader.ReadBool(),
            Route: reader.ReadOptionalString(),
            Name: reader.ReadOptionalString(),
            Value: ReadOptionalJsonValue(ref reader),
            ErrorMessage: reader.ReadOptionalString());

    private static CoreEvent ReadActiveColumns(ref BinaryEventReader reader)
    {
        var count = reader.ReadLength();
        var columns = new List<ColumnKey>(count);
        for (var index = 0; index < count; index++)
        {
            columns.Add(ReadColumnKey(ref reader));
        }

        return new CoreEvent.ActiveColumns(columns);
    }

    // -----------------------------------------------------------------
    // Shared sub-records
    // -----------------------------------------------------------------

    /// <summary>
    /// Typed-event column keys carry no derivation tag — only the plot frame
    /// does — so every key decoded here is a raw device column. The Rust writer
    /// emits the index as i64; negatives are clamped away as the Swift app does.
    /// </summary>
    private static ColumnKey ReadColumnKey(ref BinaryEventReader reader)
    {
        var route = reader.ReadString();
        var streamId = reader.ReadU8();
        var columnIndex = reader.ReadI64();
        return new ColumnKey(route, streamId, (int)Math.Clamp(columnIndex, 0, int.MaxValue));
    }

    private static IReadOnlyList<AvailableDevice> ReadAvailableDevices(ref BinaryEventReader reader)
    {
        var count = reader.ReadLength();
        var devices = new List<AvailableDevice>(count);
        for (var index = 0; index < count; index++)
        {
            var url = reader.ReadString();
            var label = reader.ReadString();
            var kind = reader.ReadString();
            var detail = reader.ReadString();

            var routeCount = reader.ReadLength();
            var routes = new List<AvailableDeviceRoute>(routeCount);
            for (var routeIndex = 0; routeIndex < routeCount; routeIndex++)
            {
                routes.Add(new AvailableDeviceRoute(reader.ReadString(), reader.ReadOptionalString()));
            }

            devices.Add(new AvailableDevice(url, label, kind, detail, routes));
        }

        return devices;
    }

    private static IReadOnlyList<DeviceInfo> ReadDevices(ref BinaryEventReader reader)
    {
        var count = reader.ReadLength();
        var devices = new List<DeviceInfo>(count);
        for (var index = 0; index < count; index++)
        {
            var url = reader.ReadString();
            var route = reader.ReadString();
            var meta = ReadDeviceMeta(ref reader);
            var streams = ReadStreams(ref reader);
            var rpcs = ReadRpcs(ref reader);
            devices.Add(new DeviceInfo(url, route, meta, streams, rpcs));
        }

        return devices;
    }

    private static DeviceMeta ReadDeviceMeta(ref BinaryEventReader reader) =>
        new(
            SerialNumber: reader.ReadString(),
            FirmwareHash: reader.ReadString(),
            NStreams: (int)Math.Min(reader.ReadU64(), int.MaxValue),
            SessionId: (uint)Math.Min(reader.ReadU64(), uint.MaxValue),
            Name: reader.ReadString());

    private static IReadOnlyList<StreamInfo> ReadStreams(ref BinaryEventReader reader)
    {
        var count = reader.ReadLength();
        var streams = new List<StreamInfo>(count);
        for (var index = 0; index < count; index++)
        {
            var streamId = reader.ReadU8();
            var name = reader.ReadString();
            var nColumns = (int)Math.Min(reader.ReadU64(), int.MaxValue);
            var sampleSize = (int)Math.Min(reader.ReadU64(), int.MaxValue);
            var samplingRate = reader.ReadF64();

            var columnCount = reader.ReadLength();
            var columns = new List<ColumnInfo>(columnCount);
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                columns.Add(new ColumnInfo(
                    Key: ReadColumnKey(ref reader),
                    Name: reader.ReadString(),
                    Units: reader.ReadString(),
                    DataType: reader.ReadString(),
                    Description: reader.ReadString(),
                    DisplayValue: reader.ReadOptionalF64()));
            }

            streams.Add(new StreamInfo(streamId, name, nColumns, sampleSize, samplingRate, columns));
        }

        return streams;
    }

    private static IReadOnlyList<RpcInfo> ReadRpcs(ref BinaryEventReader reader)
    {
        var count = reader.ReadLength();
        var rpcs = new List<RpcInfo>(count);
        for (var index = 0; index < count; index++)
        {
            rpcs.Add(new RpcInfo(
                Route: reader.ReadString(),
                Name: reader.ReadString(),
                Size: (int)Math.Min(reader.ReadU64(), int.MaxValue),
                Permissions: reader.ReadString(),
                ArgType: reader.ReadString(),
                Readable: reader.ReadBool(),
                Writable: reader.ReadBool(),
                Persistent: reader.ReadBool(),
                Unknown: reader.ReadBool(),
                Value: ReadOptionalJsonValue(ref reader)));
        }

        return rpcs;
    }

    private static JsonValue? ReadOptionalJsonValue(ref BinaryEventReader reader) =>
        reader.ReadBool() ? ReadJsonValue(ref reader) : null;

    /// <summary>Tag byte layout from <c>write_json_value</c>.</summary>
    private static JsonValue ReadJsonValue(ref BinaryEventReader reader)
    {
        var tag = reader.ReadU8();
        switch (tag)
        {
            case 0:
                return JsonValue.Null.Instance;
            case 1:
                return new JsonValue.Bool(reader.ReadBool());
            case 2:
                return new JsonValue.Number(reader.ReadF64());
            case 3:
                return new JsonValue.Str(reader.ReadString());
            case 4:
            {
                var count = reader.ReadLength();
                var items = new List<JsonValue>(count);
                for (var index = 0; index < count; index++)
                {
                    items.Add(ReadJsonValue(ref reader));
                }

                return new JsonValue.Array(items);
            }

            case 5:
            {
                var count = reader.ReadLength();
                var fields = new List<KeyValuePair<string, JsonValue>>(count);
                for (var index = 0; index < count; index++)
                {
                    var key = reader.ReadString();
                    fields.Add(new KeyValuePair<string, JsonValue>(key, ReadJsonValue(ref reader)));
                }

                return new JsonValue.Object(fields);
            }

            default:
                throw new EventDecodeException($"unknown JSON value tag {tag}");
        }
    }

    // -----------------------------------------------------------------
    // Binary plot frame
    // -----------------------------------------------------------------

    /// <summary>
    /// Decodes the 20-byte frame header plus one variable-length record per
    /// series. Older frame versions omit trailing per-series fields, so the
    /// version gates below match the Swift decoder rather than assuming 5.
    /// </summary>
    internal static PlotFrame DecodePlotFrame(ReadOnlySpan<byte> data)
    {
        var reader = new BinaryEventReader(data);

        var mode = reader.ReadU8() == 1 ? PlotMode.Fft : PlotMode.Timeseries;
        var hasViewportEnd = reader.ReadBool();
        var frameVersion = reader.ReadU16();
        var paneId = (int)reader.ReadU32();
        var seriesCount = (int)reader.ReadU32();
        var viewportEndValue = reader.ReadF64();
        var viewportEnd = hasViewportEnd ? viewportEndValue : (double?)null;

        var series = new List<PlotSeries>(seriesCount);
        for (var index = 0; index < seriesCount; index++)
        {
            var routeLength = reader.ReadU16();
            var route = ReadFixedString(ref reader, routeLength);
            var streamId = reader.ReadU8();
            var columnIndex = (int)reader.ReadU32();
            var derivation = frameVersion >= 5
                ? DerivationExtensions.FromCode(reader.ReadU8())
                : null;
            var sampleRate = reader.ReadF64();
            var flags = frameVersion >= 3 ? reader.ReadU8() : (byte)0;
            var encodedNoiseFloor = frameVersion >= 4 ? reader.ReadF64() : double.NaN;
            // The core encodes "absent" as NaN; a non-positive ASD is equally meaningless.
            var noiseFloor = double.IsFinite(encodedNoiseFloor) && encodedNoiseFloor > 0
                ? encodedNoiseFloor
                : (double?)null;

            var pointCount = (int)reader.ReadU32();
            var points = new PlotPoint[pointCount];
            for (var pointIndex = 0; pointIndex < pointCount; pointIndex++)
            {
                points[pointIndex] = new PlotPoint(reader.ReadF64(), reader.ReadF64());
            }

            series.Add(new PlotSeries(
                Key: new ColumnKey(route, streamId, columnIndex, derivation),
                SampleRate: sampleRate,
                Points: points,
                OutsideWindow: (flags & 0x1) != 0,
                WarmingUp: (flags & 0x2) != 0,
                NoiseFloor: noiseFloor));
        }

        return new PlotFrame(paneId, mode, viewportEnd, series);
    }

    /// <summary>Plot-frame routes carry an explicit u16 length rather than the u32 prefix used elsewhere.</summary>
    private static string ReadFixedString(ref BinaryEventReader reader, int byteCount)
    {
        if (byteCount == 0)
        {
            return string.Empty;
        }

        var bytes = new byte[byteCount];
        for (var index = 0; index < byteCount; index++)
        {
            bytes[index] = reader.ReadU8();
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    // -----------------------------------------------------------------
    // JSON transport
    // -----------------------------------------------------------------

    private static CoreEvent? DecodeJson(ReadOnlySpan<byte> data)
    {
        var document = JsonDocument.Parse(data.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return new CoreEvent.Json(typeElement.GetString()!, root.Clone());
    }
}
