// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IceScreenTouch.Api.Tests;

/// <summary>
/// A light JSON Schema check for the schemas of docs/api/openapi.yaml: only what that document uses (<c>$ref</c>,
/// <c>type</c>, <c>enum</c>, <c>const</c>, <c>required</c>, <c>properties</c>, <c>additionalProperties: false</c>,
/// <c>items</c>, <c>oneOf</c>, <c>allOf</c>, <c>if</c>/<c>then</c>, the numeric and size limits). It is not a general
/// validator; the Python client's tests validate against the same document with the full <c>jsonschema</c> package.
/// </summary>
internal static class SchemaCheck
{
    /// <summary>The problems of <paramref name="instance"/> against <paramref name="schema"/> (empty when it is valid).</summary>
    public static List<string> Validate(JsonNode root, JsonNode schema, JsonNode? instance)
    {
        var errors = new List<string>();
        Check(root, schema, instance, "$", errors);
        return errors;
    }

    /// <summary>The problems of <paramref name="instance"/> against <c>components.schemas.&lt;name&gt;</c>.</summary>
    public static List<string> ValidateNamed(JsonNode root, string name, JsonNode? instance) =>
        Validate(root, root["components"]!["schemas"]![name]!, instance);

    private static bool Matches(JsonNode root, JsonNode schema, JsonNode? instance)
    {
        var errors = new List<string>();
        Check(root, schema, instance, "$", errors);
        return errors.Count == 0;
    }

    private static void Check(JsonNode root, JsonNode schema, JsonNode? instance, string where, List<string> errors)
    {
        if (schema is JsonValue)
        {
            return; // true / false schemas are not used
        }

        var obj = schema.AsObject();
        if (obj["$ref"] is JsonValue reference)
        {
            Check(root, Resolve(root, reference.GetValue<string>()), instance, where, errors);
            return; // a sibling of $ref is a description in this document
        }

        if (obj["type"] is { } type && !TypeMatches(type, instance))
        {
            errors.Add($"{where}: expected {type.ToJsonString()}, got {KindOf(instance)}");
            return;
        }

        if (obj["const"] is { } constant && !JsonNode.DeepEquals(constant, instance))
        {
            errors.Add($"{where}: must be {constant.ToJsonString()}");
        }

        if (obj["enum"] is JsonArray allowed && !allowed.Any(a => JsonNode.DeepEquals(a, instance)))
        {
            errors.Add($"{where}: {(instance?.ToJsonString() ?? "null")} is not one of {allowed.ToJsonString()}");
        }

        if (obj["oneOf"] is JsonArray alternatives)
        {
            var matching = alternatives.Count(a => Matches(root, a!, instance));
            if (matching != 1)
            {
                errors.Add($"{where}: matches {matching} of the oneOf alternatives (exactly 1 is needed)");
            }
        }

        if (obj["allOf"] is JsonArray all)
        {
            foreach (var part in all)
            {
                Check(root, part!, instance, where, errors);
            }
        }

        if (obj["if"] is { } condition && obj["then"] is { } then && Matches(root, condition, instance))
        {
            Check(root, then, instance, where, errors);
        }

        if (instance is JsonValue number && KindOf(instance) is "integer" or "number")
        {
            var value = Num(number);
            if (obj["minimum"] is { } minimum && value < Num(minimum))
            {
                errors.Add($"{where}: {value.ToString(CultureInfo.InvariantCulture)} is below the minimum {minimum.ToJsonString()}");
            }

            if (obj["maximum"] is { } maximum && value > Num(maximum))
            {
                errors.Add($"{where}: {value.ToString(CultureInfo.InvariantCulture)} is above the maximum {maximum.ToJsonString()}");
            }
        }

        if (instance is JsonArray array)
        {
            if (obj["minItems"] is { } minItems && array.Count < (int)Num(minItems))
            {
                errors.Add($"{where}: fewer than {minItems.ToJsonString()} items");
            }

            if (obj["maxItems"] is { } maxItems && array.Count > (int)Num(maxItems))
            {
                errors.Add($"{where}: more than {maxItems.ToJsonString()} items");
            }

            if (obj["items"] is { } items)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    Check(root, items, array[i], $"{where}[{i}]", errors);
                }
            }
        }

        if (instance is JsonObject target)
        {
            var properties = obj["properties"] as JsonObject;
            if (obj["required"] is JsonArray required)
            {
                foreach (var name in required.Select(r => r!.GetValue<string>()).Where(n => !target.ContainsKey(n)))
                {
                    errors.Add($"{where}: the required member '{name}' is missing");
                }
            }

            if (obj["minProperties"] is { } minProperties && target.Count < (int)Num(minProperties))
            {
                errors.Add($"{where}: fewer than {minProperties.ToJsonString()} members");
            }

            foreach (var (name, value) in target)
            {
                if (properties is not null && properties[name] is { } member)
                {
                    Check(root, member, value, $"{where}.{name}", errors);
                }
                else if (obj["additionalProperties"] is JsonValue { } extra && extra.GetValueKind() == JsonValueKind.False)
                {
                    errors.Add($"{where}: the member '{name}' is not allowed");
                }
            }
        }
    }

    private static JsonNode Resolve(JsonNode root, string reference)
    {
        Assert.StartsWith("#/", reference, StringComparison.Ordinal);
        var node = root;
        foreach (var part in reference[2..].Split('/'))
        {
            node = node[part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)]
                ?? throw new InvalidOperationException($"{reference} does not resolve");
        }

        return node;
    }

    private static string KindOf(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            JsonValueKind.Number => IsWhole(value) ? "integer" : "number",
            _ => "other",
        },
        _ => "other",
    };

    // A number of the YAML document is a long or a double, one of a parsed body is a JsonElement: read it through its text.
    private static double Num(JsonNode node) => double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture);

    private static bool IsWhole(JsonValue value) => Math.Abs(Num(value) % 1) < double.Epsilon;

    private static bool TypeMatches(JsonNode type, JsonNode? instance)
    {
        var kind = KindOf(instance);
        var names = type is JsonArray list ? list.Select(t => t!.GetValue<string>()) : [type.GetValue<string>()];
        return names.Any(name => name == kind || (name == "number" && kind == "integer"));
    }
}
