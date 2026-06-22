using System.Collections.Generic;
using System.Text.Json;
using McpNet.Core.Capabilities;

namespace McpNet.Gateway.Routing
{
    /// <summary>
    /// Validates tool call arguments against a tool's <see cref="McpToolInputSchema"/>.
    /// Checks required fields and primitive type constraints (string/number/integer/boolean/
    /// array/object). Complex keywords (anyOf, allOf, $ref, pattern) are not enforced —
    /// those are left to the upstream server.
    /// Returns <c>null</c> when valid, or an English error message on first violation.
    /// </summary>
    public static class JsonSchemaValidator
    {
        public static string? Validate(McpToolInputSchema? schema, Dictionary<string, object?>? args)
        {
            if (schema == null) return null;
            args ??= new Dictionary<string, object?>();

            // 1. Required fields
            if (schema.Required != null)
            {
                foreach (var req in schema.Required)
                {
                    if (!args.TryGetValue(req, out var val) || val is null)
                        return $"Required argument '{req}' is missing.";
                    if (val is JsonElement je && je.ValueKind == JsonValueKind.Null)
                        return $"Required argument '{req}' must not be null.";
                }
            }

            // 2. Type checks for supplied arguments
            if (schema.Properties != null)
            {
                foreach (var kv in args)
                {
                    if (kv.Value is null) continue;
                    if (!schema.Properties.TryGetValue(kv.Key, out var prop)) continue;
                    if (string.IsNullOrEmpty(prop.Type)) continue;

                    var err = CheckType(kv.Key, kv.Value, prop.Type);
                    if (err != null) return err;
                }
            }

            return null;
        }

        private static string? CheckType(string name, object? value, string expectedType)
        {
            if (value is null) return null;

            JsonValueKind? kind = value is JsonElement je ? je.ValueKind : (JsonValueKind?)null;

            return expectedType switch
            {
                "string" => kind.HasValue
                    ? (kind == JsonValueKind.String ? null : $"Argument '{name}' must be a string, got {kind}.")
                    : (value is string ? null : $"Argument '{name}' must be a string."),

                "number" or "integer" => kind.HasValue
                    ? (kind == JsonValueKind.Number ? null : $"Argument '{name}' must be a number, got {kind}.")
                    : (value is int or long or float or double or decimal ? null : $"Argument '{name}' must be a number."),

                "boolean" => kind.HasValue
                    ? (kind is JsonValueKind.True or JsonValueKind.False ? null : $"Argument '{name}' must be a boolean, got {kind}.")
                    : (value is bool ? null : $"Argument '{name}' must be a boolean."),

                "array" => kind.HasValue
                    ? (kind == JsonValueKind.Array ? null : $"Argument '{name}' must be an array, got {kind}.")
                    : null,

                "object" => kind.HasValue
                    ? (kind == JsonValueKind.Object ? null : $"Argument '{name}' must be an object, got {kind}.")
                    : null,

                _ => null
            };
        }
    }
}
