// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text.Json.Nodes;

namespace Twinleaf.Models;

/// <summary>
/// Plot settings that change how the <em>core</em> decimates, so they have to be
/// pushed across the FFI rather than applied at draw time. Field names and
/// casing here must match the Rust <c>ViewConfig</c> struct, which is
/// <c>serde(rename_all = "camelCase")</c>.
///
/// Defaults deliberately mirror the Rust <c>Default</c> impl so a freshly opened
/// window and a freshly started core agree before the first push.
/// </summary>
public sealed record ViewConfig
{
    public PlotMode Mode { get; init; } = PlotMode.Timeseries;

    /// <summary>Width of the displayed time window, in seconds.</summary>
    public double WindowSeconds { get; init; } = 10.0;

    /// <summary>
    /// Decimation buckets per plot pixel. Higher keeps more min/max detail at
    /// the cost of points on the wire.
    /// </summary>
    public int ResolutionMultiplier { get; init; } = 100;

    /// <summary>
    /// Canvas width the core decimates for. Updated from the real canvas size so
    /// the core never sends materially more points than the display can resolve.
    /// </summary>
    public int PlotWidthPixels { get; init; } = 800;

    public DecimationMethod DecimationMethod { get; init; } = DecimationMethod.Fpcs;

    public DetrendMethod Detrend { get; init; } = DetrendMethod.Quadratic;

    public bool FftLogX { get; init; } = true;

    public bool FftLogY { get; init; } = true;

    /// <summary>
    /// Log vertical axis in timeseries mode. Separate from <see cref="FftLogY"/>
    /// so a pane toggling between modes keeps each axis choice.
    /// </summary>
    public bool LogY { get; init; } = false;

    public JsonObject ToNode() => new()
    {
        ["mode"] = Mode == PlotMode.Fft ? "fft" : "timeseries",
        ["windowSeconds"] = WindowSeconds,
        ["resolutionMultiplier"] = ResolutionMultiplier,
        ["plotWidthPixels"] = PlotWidthPixels,
        ["decimationMethod"] = DecimationMethod == DecimationMethod.None ? "none" : "fpcs",
        ["detrend"] = Detrend switch
        {
            DetrendMethod.None => "none",
            DetrendMethod.Mean => "mean",
            DetrendMethod.Linear => "linear",
            _ => "quadratic",
        },
        ["fftLogX"] = FftLogX,
        ["fftLogY"] = FftLogY,
        ["logY"] = LogY,
    };

    /// <summary>Whether the vertical axis is logarithmic in the current mode.</summary>
    public bool VerticalAxisIsLog => Mode == PlotMode.Fft ? FftLogY : LogY;

    /// <summary>Selectable window durations, matching the macOS app's menu.</summary>
    public static readonly double[] WindowDurations =
        [0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 30, 60, 120, 300, 600, 1000];

    public static double ClampWindow(double seconds) =>
        double.IsFinite(seconds) ? Math.Clamp(seconds, 0.1, 1000.0) : 10.0;
}
