// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Twinleaf.Models;

/// <summary>
/// One device with newer published firmware, from an <c>upgradeStatus</c> event.
/// </summary>
public sealed record FirmwareUpdate(
    string Route,
    string DeviceName,
    string CurrentVersion,
    string NewVersion,
    string NewHash,
    string Filename)
{
    public string VersionSummary => $"{CurrentVersion}  →  {NewVersion}";

    public static FirmwareUpdate FromJson(JsonElement element) => new(
        Route: Text(element, "route", "/"),
        DeviceName: Text(element, "deviceName", "device"),
        CurrentVersion: Text(element, "currentVersion", "?"),
        NewVersion: Text(element, "newVersion", "?"),
        NewHash: Text(element, "newHash"),
        Filename: Text(element, "filename"));

    internal static string Text(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
}

/// <summary>
/// Progress of an in-flight flash, from an <c>upgradeProgress</c> event.
///
/// Phases run downloading → stopping → stopped → uploading → committing →
/// finalizing → complete, with <c>error</c> possible at any point. Only
/// <c>uploading</c> and <c>complete</c> carry a fraction; the rest are
/// indeterminate.
/// </summary>
public sealed record FirmwareProgress(
    string Route,
    string Phase,
    string Message,
    double? Fraction)
{
    public bool IsComplete => Phase == "complete";

    public bool IsError => Phase == "error";

    /// <summary>True while the phase gives no measurable progress.</summary>
    public bool IsIndeterminate => Fraction is null && !IsComplete && !IsError;

    /// <summary>
    /// Flashing must not be interrupted, so the UI keeps the popover open and
    /// warns until the device settles.
    /// </summary>
    public bool IsRunning => !IsComplete && !IsError;

    public static FirmwareProgress FromJson(JsonElement element)
    {
        var phase = FirmwareUpdate.Text(element, "phase");
        var message = FirmwareUpdate.Text(element, "message", phase);

        double? fraction = element.TryGetProperty("fraction", out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var parsed)
            && double.IsFinite(parsed)
                ? Math.Clamp(parsed, 0, 1)
                : null;

        // "complete" is reported without a fraction; treat it as fully done so
        // the bar does not sit mid-way on success.
        if (fraction is null && phase == "complete")
        {
            fraction = 1.0;
        }

        return new FirmwareProgress(
            Route: FirmwareUpdate.Text(element, "route", "/"),
            Phase: phase,
            Message: message,
            Fraction: fraction);
    }
}

/// <summary>
/// Per-stream timing diagnostics from a <c>health</c> event, which the core
/// emits about twice a second while streaming. Mirrors what <c>tio health</c>
/// reports.
/// </summary>
public sealed record StreamHealth(
    string Route,
    byte StreamId,
    string Name,
    double? RateHz,
    double? Ppm,
    double? DriftSeconds,
    double? JitterMs,
    ulong Received,
    ulong Dropped,
    uint SessionId,
    bool Stale)
{
    public string Title => $"{Name}  ({Route})";

    /// <summary>Rate, jitter, clock error and losses on one line.</summary>
    public string Detail => string.Format(
        CultureInfo.CurrentCulture,
        "{0} · jitter {1} · {2} · dropped {3:N0}",
        RateHz is { } rate ? $"{rate:0.00} Hz" : "— Hz",
        JitterMs is { } jitter ? $"{jitter:0.00} ms" : "—",
        Ppm is { } ppm ? $"{ppm:0} ppm" : "— ppm",
        Dropped);

    /// <summary>
    /// Anything the user should notice: a stalled stream, or samples actually
    /// lost. Drives the toolbar button's alert state.
    /// </summary>
    public bool NeedsAttention => Stale || Dropped > 0;

    /// <summary>x:Bind wants a Visibility directly; a converter for one flag is not worth it.</summary>
    public Microsoft.UI.Xaml.Visibility StaleVisibility =>
        Stale ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public static StreamHealth FromJson(JsonElement element) => new(
        Route: FirmwareUpdate.Text(element, "route", "/"),
        StreamId: (byte)Math.Clamp(Number(element, "streamId") ?? 0, 0, 255),
        Name: FirmwareUpdate.Text(element, "name"),
        RateHz: Finite(element, "rateHz"),
        Ppm: Finite(element, "ppm"),
        DriftSeconds: Finite(element, "driftSeconds"),
        JitterMs: Finite(element, "jitterMs"),
        Received: (ulong)Math.Max(0, Number(element, "received") ?? 0),
        Dropped: (ulong)Math.Max(0, Number(element, "dropped") ?? 0),
        SessionId: (uint)Math.Clamp(Number(element, "sessionId") ?? 0, 0, uint.MaxValue),
        Stale: element.TryGetProperty("stale", out var stale) && stale.ValueKind == JsonValueKind.True);

    /// <summary>The core writes null rather than NaN for an unmeasurable value.</summary>
    private static double? Finite(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var parsed)
        && double.IsFinite(parsed)
            ? parsed
            : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var parsed)
            ? parsed
            : null;

    public static IReadOnlyList<StreamHealth> ListFromJson(JsonElement payload)
    {
        if (!payload.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<StreamHealth>();
        foreach (var stream in streams.EnumerateArray())
        {
            result.Add(FromJson(stream));
        }

        return result;
    }
}
