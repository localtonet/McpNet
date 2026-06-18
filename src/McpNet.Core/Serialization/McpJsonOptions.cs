using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpNet.Core.Serialization
{
    public static class McpJsonOptions
    {
        public static readonly JsonSerializerOptions Default = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        public static T? Deserialize<T>(string json) =>
            JsonSerializer.Deserialize<T>(json, Default);

        public static string Serialize<T>(T value) =>
            JsonSerializer.Serialize(value, Default);

        public static T? Convert<T>(object? value)
        {
            if (value is null) return default;
            if (value is T t) return t;
            if (value is JsonElement element)
                return JsonSerializer.Deserialize<T>(element.GetRawText(), Default);
            var json = JsonSerializer.Serialize(value, Default);
            return JsonSerializer.Deserialize<T>(json, Default);
        }
    }
}
