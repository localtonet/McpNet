using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpNet.Core.Serialization
{
    /// <summary>
    /// Handles JSON Schema "type" which can be either a plain string ("integer") or
    /// an array (["integer", "null"]). Always serialises back as a plain string,
    /// picking the first non-"null" entry from an array (falling back to "string").
    /// </summary>
    public sealed class JsonSchemaTypeConverter : JsonConverter<string>
    {
        public static readonly JsonSchemaTypeConverter Instance = new JsonSchemaTypeConverter();

        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
                return reader.GetString() ?? "string";

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                string? first = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var val = reader.GetString();
                        if (first == null && !string.Equals(val, "null", StringComparison.OrdinalIgnoreCase))
                            first = val;
                    }
                }
                return first ?? "string";
            }

            // Unexpected token – skip and return default
            reader.Skip();
            return "string";
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }
}
