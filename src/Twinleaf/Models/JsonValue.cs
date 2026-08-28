// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Twinleaf.Models;

/// <summary>
/// A dynamically-typed value carried by RPC results, mirroring the Swift app's
/// <c>JSONValue</c>. RPC value types are only known from device metadata at
/// runtime, so results cannot be modelled as a fixed struct.
///
/// The binary wire form (see <c>write_json_value</c> in the Rust core) tags each
/// value with a leading byte: 0 null, 1 bool, 2 number, 3 string, 4 array,
/// 5 object.
/// </summary>
public abstract record JsonValue
{
    public sealed record Null : JsonValue
    {
        public static readonly Null Instance = new();
        public override string ToString() => "null";
    }

    public sealed record Bool(bool Value) : JsonValue
    {
        public override string ToString() => Value ? "true" : "false";
    }

    public sealed record Number(double Value) : JsonValue
    {
        public override string ToString() =>
            Value == Math.Floor(Value) && Math.Abs(Value) < 1e15
                ? ((long)Value).ToString(CultureInfo.InvariantCulture)
                : Value.ToString("G15", CultureInfo.InvariantCulture);
    }

    public sealed record Str(string Value) : JsonValue
    {
        public override string ToString() => Value;
    }

    public sealed record Array(IReadOnlyList<JsonValue> Items) : JsonValue
    {
        public override string ToString() => "[" + string.Join(", ", Items) + "]";
    }

    public sealed record Object(IReadOnlyList<KeyValuePair<string, JsonValue>> Fields) : JsonValue
    {
        public JsonValue? this[string key] =>
            Fields.FirstOrDefault(field => field.Key == key).Value;

        public override string ToString() =>
            "{" + string.Join(", ", Fields.Select(field => $"{field.Key}: {field.Value}")) + "}";
    }

    // -----------------------------------------------------------------
    // Convenience accessors used by the RPC sidebar
    // -----------------------------------------------------------------

    public bool? AsBool => this switch
    {
        Bool value => value.Value,
        Number value => value.Value != 0.0,
        _ => null,
    };

    public double? AsNumber => this switch
    {
        Number value => value.Value,
        Bool value => value.Value ? 1.0 : 0.0,
        Str value when double.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    public string? AsString => this switch
    {
        Str value => value.Value,
        _ => null,
    };

    /// <summary>Text shown in an editable RPC field.</summary>
    public string EditableText => this switch
    {
        Null => string.Empty,
        Str value => value.Value,
        _ => ToString() ?? string.Empty,
    };

    // -----------------------------------------------------------------
    // Conversion
    // -----------------------------------------------------------------

    /// <summary>Serializes to JSON text for the core's <c>argJson</c> parameter.</summary>
    public JsonNode? ToNode() => this switch
    {
        Null => null,
        Bool value => Leaf(value.Value),
        Number value => Leaf(value.Value),
        Str value => Leaf(value.Value),
        Array value => new JsonArray(value.Items.Select(item => item.ToNode()).ToArray()),
        Object value => BuildObject(value),
        _ => null,
    };

    private static JsonNode Leaf<T>(T value) => System.Text.Json.Nodes.JsonValue.Create(value)!;

    private static JsonNode BuildObject(Object value)
    {
        var node = new JsonObject();
        foreach (var (key, item) in value.Fields)
        {
            node[key] = item.ToNode();
        }

        return node;
    }

    public string ToJsonString() => ToNode()?.ToJsonString() ?? "null";

    /// <summary>Parses a <see cref="JsonElement"/> from a JSON-transport event.</summary>
    public static JsonValue FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => Null.Instance,
        JsonValueKind.True => new Bool(true),
        JsonValueKind.False => new Bool(false),
        JsonValueKind.Number => new Number(element.GetDouble()),
        JsonValueKind.String => new Str(element.GetString() ?? string.Empty),
        JsonValueKind.Array => new Array(element.EnumerateArray().Select(FromJson).ToList()),
        JsonValueKind.Object => new Object(
            element.EnumerateObject()
                .Select(property => new KeyValuePair<string, JsonValue>(property.Name, FromJson(property.Value)))
                .ToList()),
        _ => Null.Instance,
    };
}
