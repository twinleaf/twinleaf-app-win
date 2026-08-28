// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Twinleaf.Controls;

/// <summary>
/// Maps data coordinates to pixels for one axis and produces its tick marks.
///
/// Ticks land on "nice" values (1, 2 or 5 times a power of ten) so the labels
/// stay readable as the window pans and zooms — a plain <c>range/n</c> division
/// would make every label change on every frame.
/// </summary>
public sealed class AxisScale
{
    private readonly double _min;
    private readonly double _max;
    private readonly double _pixelMin;
    private readonly double _pixelMax;
    private readonly bool _logarithmic;

    /// <summary>
    /// Smallest positive value plotted on a log axis, used as the floor when
    /// data contains zeros or negatives that a log axis cannot represent.
    /// </summary>
    private const double LogFloor = 1e-30;

    public AxisScale(double min, double max, double pixelMin, double pixelMax, bool logarithmic)
    {
        _logarithmic = logarithmic;
        _pixelMin = pixelMin;
        _pixelMax = pixelMax;

        if (logarithmic)
        {
            // Guard against a non-positive bound before taking logs.
            min = min > 0 ? min : LogFloor;
            max = max > min ? max : min * 10;
        }
        else if (max <= min)
        {
            // A flat trace still needs a finite span or every point maps to one pixel.
            var padding = Math.Abs(min) > 0 ? Math.Abs(min) * 0.05 : 0.5;
            min -= padding;
            max += padding;
        }

        _min = min;
        _max = max;
    }

    public double Min => _min;

    public double Max => _max;

    public bool IsLogarithmic => _logarithmic;

    /// <summary>Maps a data value to a pixel coordinate.</summary>
    public double ToPixel(double value)
    {
        if (_logarithmic)
        {
            var clamped = value > 0 ? value : LogFloor;
            var span = Math.Log10(_max) - Math.Log10(_min);
            if (span <= 0)
            {
                return _pixelMin;
            }

            var fraction = (Math.Log10(clamped) - Math.Log10(_min)) / span;
            return _pixelMin + fraction * (_pixelMax - _pixelMin);
        }

        var range = _max - _min;
        if (range <= 0)
        {
            return _pixelMin;
        }

        return _pixelMin + (value - _min) / range * (_pixelMax - _pixelMin);
    }

    /// <summary>Maps a pixel coordinate back to a data value (for the cursor readout).</summary>
    public double ToValue(double pixel)
    {
        var pixelRange = _pixelMax - _pixelMin;
        if (pixelRange == 0)
        {
            return _min;
        }

        var fraction = (pixel - _pixelMin) / pixelRange;

        if (_logarithmic)
        {
            var span = Math.Log10(_max) - Math.Log10(_min);
            return Math.Pow(10, Math.Log10(_min) + fraction * span);
        }

        return _min + fraction * (_max - _min);
    }

    /// <summary>
    /// Tick positions with their labels. <paramref name="targetCount"/> is a
    /// hint; the nice-number rounding decides the actual count.
    /// </summary>
    public IReadOnlyList<(double Value, string Label)> Ticks(int targetCount)
    {
        return _logarithmic ? LogTicks() : LinearTicks(targetCount);
    }

    private List<(double, string)> LinearTicks(int targetCount)
    {
        var ticks = new List<(double, string)>();
        var range = _max - _min;
        if (range <= 0 || targetCount < 1)
        {
            return ticks;
        }

        var step = NiceStep(range / targetCount);
        if (step <= 0 || !double.IsFinite(step))
        {
            return ticks;
        }

        // Decimals needed to distinguish adjacent ticks; without this a 0.25
        // step would label three consecutive ticks "0".
        var decimals = Math.Max(0, (int)Math.Ceiling(-Math.Log10(step)));

        var first = Math.Ceiling(_min / step) * step;
        for (var value = first; value <= _max + step * 1e-9; value += step)
        {
            // Re-snap: repeated addition accumulates error over many ticks.
            var snapped = Math.Round(value / step) * step;
            if (snapped < _min - step * 1e-9 || snapped > _max + step * 1e-9)
            {
                continue;
            }

            ticks.Add((snapped, FormatTick(snapped, decimals)));
        }

        return ticks;
    }

    private List<(double, string)> LogTicks()
    {
        var ticks = new List<(double, string)>();
        var lowDecade = (int)Math.Floor(Math.Log10(_min));
        var highDecade = (int)Math.Ceiling(Math.Log10(_max));

        if (highDecade - lowDecade > 40)
        {
            return ticks;
        }

        for (var decade = lowDecade; decade <= highDecade; decade++)
        {
            var value = Math.Pow(10, decade);
            if (value < _min || value > _max)
            {
                continue;
            }

            ticks.Add((value, FormatDecade(decade)));
        }

        return ticks;
    }

    /// <summary>Rounds a raw step up to the next 1, 2 or 5 times a power of ten.</summary>
    private static double NiceStep(double rawStep)
    {
        if (rawStep <= 0 || !double.IsFinite(rawStep))
        {
            return 0;
        }

        var exponent = Math.Floor(Math.Log10(rawStep));
        var magnitude = Math.Pow(10, exponent);
        var normalized = rawStep / magnitude;

        var nice = normalized switch
        {
            <= 1 => 1.0,
            <= 2 => 2.0,
            <= 5 => 5.0,
            _ => 10.0,
        };

        return nice * magnitude;
    }

    private static string FormatTick(double value, int decimals)
    {
        if (NumericDisplayPolicy.UsesScientificNotation(value))
        {
            return value.ToString("0.###e+00", CultureInfo.InvariantCulture);
        }

        return value.ToString("N" + Math.Min(decimals, 6), CultureInfo.CurrentCulture);
    }

    private static string FormatDecade(int decade)
    {
        // Plain digits read better than 1e0/1e1 across the common sensor range.
        if (decade is >= -4 and <= 4)
        {
            return Math.Pow(10, decade).ToString(
                decade < 0 ? "0." + new string('#', -decade) : "0",
                CultureInfo.InvariantCulture);
        }

        return $"1e{decade}";
    }
}
