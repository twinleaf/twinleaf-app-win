// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;

namespace Twinleaf;

/// <summary>
/// Number formatting shared by the sidebar readout, plot axes and RPC fields.
/// Ported from the macOS app's <c>NumericDisplayPolicy</c> so the two apps
/// render the same value identically.
/// </summary>
public static class NumericDisplayPolicy
{
    /// <summary>
    /// Decades away from 1 at which fixed notation gives way to scientific.
    /// Sensor values span many orders of magnitude, so this keeps a reading
    /// legible without letting it run to twenty characters.
    /// </summary>
    public const int ScientificDecimalDistance = 5;

    public const double DefaultRpcFloatPrecisionPpm = 10.0;

    private const double MinRpcFloatPrecisionPpm = 0.001;
    private const double MaxRpcFloatPrecisionPpm = 1_000_000.0;

    public static double LargeScientificThreshold { get; } = Math.Pow(10, ScientificDecimalDistance);

    public static double SmallScientificThreshold { get; } = Math.Pow(10, -ScientificDecimalDistance);

    public static bool UsesScientificNotation(double value)
    {
        var magnitude = Math.Abs(value);
        if (magnitude == 0)
        {
            return false;
        }

        return magnitude >= LargeScientificThreshold || magnitude < SmallScientificThreshold;
    }

    public static string Fixed(double value, int fractionDigits) =>
        NormalizeZero(value).ToString("N" + fractionDigits, CultureInfo.CurrentCulture);

    public static string Significant(double value, int maximumDigits)
    {
        var normalized = NormalizeZero(value);
        var text = normalized.ToString("G" + maximumDigits, CultureInfo.CurrentCulture);
        // "G" yields E+05 style exponents; normalise to the shorter form used on macOS.
        return text.Replace("E+0", "e", StringComparison.OrdinalIgnoreCase)
                   .Replace("E-0", "e-", StringComparison.OrdinalIgnoreCase)
                   .Replace("E+", "e", StringComparison.OrdinalIgnoreCase)
                   .Replace("E-", "e-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Formats a live channel reading. Chooses scientific or fixed notation by
    /// magnitude so a column's readout stays a stable width as values drift.
    /// </summary>
    public static string Reading(double value)
    {
        if (double.IsNaN(value))
        {
            return "—";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "∞" : "-∞";
        }

        return UsesScientificNotation(value)
            ? value.ToString("0.####e+00", CultureInfo.InvariantCulture)
            : Significant(value, 6);
    }

    /// <summary>
    /// Formats an RPC float to the precision its metadata advertises, in parts
    /// per million: a device that resolves 10 ppm gets 6 significant digits.
    /// </summary>
    public static string RpcFloat(double value, double precisionPpm)
    {
        if (!double.IsFinite(value))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        var digits = SignificantDigits(precisionPpm);
        var formatted = NormalizeZero(value).ToString("G" + digits, CultureInfo.InvariantCulture);
        return formatted == "-0" ? "0" : formatted;
    }

    public static double ClampRpcFloatPrecisionPpm(double value) =>
        Math.Clamp(value, MinRpcFloatPrecisionPpm, MaxRpcFloatPrecisionPpm);

    private static int SignificantDigits(double precisionPpm)
    {
        var ppm = ClampRpcFloatPrecisionPpm(precisionPpm);
        return Math.Clamp((int)Math.Ceiling(Math.Log10(10_000_000 / ppm)), 1, 17);
    }

    /// <summary>Collapses negative zero, which otherwise renders as "-0".</summary>
    private static double NormalizeZero(double value) => value == 0 ? 0 : value;
}
