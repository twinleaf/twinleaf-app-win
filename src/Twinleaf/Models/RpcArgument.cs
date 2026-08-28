// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;

namespace Twinleaf.Models;

/// <summary>
/// A typed RPC argument, matching the <c>TwinleafRpcArg</c> tagged union in
/// <c>twinleaf_core.h</c>.
///
/// The core exposes both a JSON path and this typed one. The typed path is what
/// the macOS app uses and what this app uses: JSON collapses every number to a
/// double, which would lose the distinction between <c>u64</c>, <c>i64</c> and
/// <c>f64</c> that a device's RPC signature actually cares about.
/// </summary>
public readonly record struct RpcArgument
{
    /// <summary>Tag values are fixed by the C header; anything else means "no argument".</summary>
    public const byte TagNone = 0;
    public const byte TagBool = 1;
    public const byte TagString = 2;
    public const byte TagDouble = 3;
    public const byte TagInt = 4;
    public const byte TagUInt = 5;

    public byte Tag { get; private init; }
    public bool BoolValue { get; private init; }
    public string? StringValue { get; private init; }
    public double DoubleValue { get; private init; }
    public long IntValue { get; private init; }
    public ulong UIntValue { get; private init; }

    /// <summary>No argument — an RPC read, or an action that takes nothing.</summary>
    public static RpcArgument None => new() { Tag = TagNone };

    public static RpcArgument FromBool(bool value) => new() { Tag = TagBool, BoolValue = value };

    public static RpcArgument FromString(string value) => new() { Tag = TagString, StringValue = value };

    public static RpcArgument FromDouble(double value) => new() { Tag = TagDouble, DoubleValue = value };

    public static RpcArgument FromInt(long value) => new() { Tag = TagInt, IntValue = value };

    public static RpcArgument FromUInt(ulong value) => new() { Tag = TagUInt, UIntValue = value };

    public bool HasValue => Tag != TagNone;

    /// <summary>
    /// Coerces user-entered text to the type the RPC declares. Returns
    /// <see cref="None"/> when the text is blank or does not parse, which the
    /// caller treats as "don't send" rather than "send nothing" — silently
    /// writing a default to a device setting would be worse than doing nothing.
    ///
    /// Parsing is invariant-culture on purpose: these are wire values for an
    /// instrument, so a comma decimal separator must not change what is sent.
    /// </summary>
    public static RpcArgument Parse(RpcInfo rpc, string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;

        switch (rpc.BaseArgType)
        {
            case "unit":
            case "":
                return None;

            case "bool":
                return ParseBool(trimmed) is { } flag ? FromBool(flag) : None;

            case "string":
                return trimmed.Length == 0 ? None : FromString(trimmed);

            case "f32":
            case "f64":
                return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
                    ? FromDouble(real)
                    : None;

            case "i8":
            case "i16":
            case "i32":
            case "i64":
                return long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed)
                    ? FromInt(signed)
                    : None;

            case "u8":
            case "u16":
            case "u32":
            case "u64":
                return ulong.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned)
                    ? FromUInt(unsigned)
                    : None;

            default:
                return trimmed.Length == 0 ? None : FromString(trimmed);
        }
    }

    /// <summary>Accepts the spellings a person actually types for a flag.</summary>
    public static bool? ParseBool(string text) => text.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" => false,
        _ => null,
    };

    /// <summary>
    /// The value as it should appear in the field after a successful write, so
    /// the UI can show the new setting without waiting for the device to echo.
    /// </summary>
    public JsonValue? ToJsonValue() => Tag switch
    {
        TagBool => new JsonValue.Bool(BoolValue),
        TagString => new JsonValue.Str(StringValue ?? string.Empty),
        TagDouble => new JsonValue.Number(DoubleValue),
        TagInt => new JsonValue.Number(IntValue),
        TagUInt => new JsonValue.Number(UIntValue),
        _ => null,
    };
}
