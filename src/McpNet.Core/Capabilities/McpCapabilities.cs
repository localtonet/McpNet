using System.Collections.Generic;
using System.Text.Json.Serialization;
using McpNet.Core.Serialization;

namespace McpNet.Core.Capabilities
{
    public class McpTool
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        [JsonPropertyName("inputSchema")]
        public McpToolInputSchema InputSchema { get; set; } = new McpToolInputSchema();
    }

    public class McpToolInputSchema
    {
        [JsonPropertyName("type")]
        [JsonConverter(typeof(JsonSchemaTypeConverter))]
        public string Type { get; set; } = "object";

        [JsonPropertyName("properties")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, McpSchemaProperty>? Properties { get; set; }

        [JsonPropertyName("required")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Required { get; set; }
    }

    public class McpSchemaProperty
    {
        [JsonPropertyName("type")]
        [JsonConverter(typeof(JsonSchemaTypeConverter))]
        public string Type { get; set; } = "string";

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        [JsonPropertyName("enum")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Enum { get; set; }

        /// <summary>
        /// Element schema for <c>array</c> properties. Required by JSON Schema (and enforced by
        /// strict MCP clients such as VS Code) whenever <see cref="Type"/> is <c>"array"</c>.
        /// </summary>
        [JsonPropertyName("items")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public McpSchemaProperty? Items { get; set; }
    }

    public class McpPrompt
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        [JsonPropertyName("arguments")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<McpPromptArgument>? Arguments { get; set; }
    }

    public class McpPromptArgument
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        [JsonPropertyName("required")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Required { get; set; }
    }

    public class McpResource
    {
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        [JsonPropertyName("mimeType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MimeType { get; set; }
    }

    public class McpContent
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "text";

        [JsonPropertyName("text")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Text { get; set; }

        [JsonPropertyName("data")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Data { get; set; }

        [JsonPropertyName("mimeType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MimeType { get; set; }

        [JsonPropertyName("uri")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Uri { get; set; }

        public static McpContent FromText(string text) => new McpContent { Type = "text", Text = text };
        public static McpContent FromImage(string base64Data, string mimeType) => new McpContent { Type = "image", Data = base64Data, MimeType = mimeType };
        public static McpContent FromResource(string uri, string? mimeType = null) => new McpContent { Type = "resource", Uri = uri, MimeType = mimeType };
    }
}
