using System.Collections.Generic;
using System.Text.Json.Serialization;
using McpNet.Core.Protocol;

namespace McpNet.Core.Capabilities
{
    public class InitializeParams
    {
        [JsonPropertyName("protocolVersion")]
        public string ProtocolVersion { get; set; } = string.Empty;

        [JsonPropertyName("capabilities")]
        public ClientCapabilities Capabilities { get; set; } = new ClientCapabilities();

        [JsonPropertyName("clientInfo")]
        public McpImplementation ClientInfo { get; set; } = new McpImplementation();
    }

    public class InitializeResult
    {
        [JsonPropertyName("protocolVersion")]
        public string ProtocolVersion { get; set; } = McpProtocolVersion.Current;

        [JsonPropertyName("capabilities")]
        public ServerCapabilities Capabilities { get; set; } = new ServerCapabilities();

        [JsonPropertyName("serverInfo")]
        public McpImplementation ServerInfo { get; set; } = new McpImplementation();

        [JsonPropertyName("instructions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Instructions { get; set; }
    }

    public class McpImplementation
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;
    }

    public class ClientCapabilities
    {
        [JsonPropertyName("roots")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RootsCapability? Roots { get; set; }

        [JsonPropertyName("sampling")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Sampling { get; set; }
    }

    public class RootsCapability
    {
        [JsonPropertyName("listChanged")]
        public bool ListChanged { get; set; }
    }

    public class ServerCapabilities
    {
        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ToolsCapability? Tools { get; set; }

        [JsonPropertyName("prompts")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PromptsCapability? Prompts { get; set; }

        [JsonPropertyName("resources")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ResourcesCapability? Resources { get; set; }

        [JsonPropertyName("logging")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Logging { get; set; }
    }

    public class ToolsCapability
    {
        [JsonPropertyName("listChanged")]
        public bool ListChanged { get; set; }
    }

    public class PromptsCapability
    {
        [JsonPropertyName("listChanged")]
        public bool ListChanged { get; set; }
    }

    public class ResourcesCapability
    {
        [JsonPropertyName("subscribe")]
        public bool Subscribe { get; set; }

        [JsonPropertyName("listChanged")]
        public bool ListChanged { get; set; }
    }

    public class ToolCallParams
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("arguments")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object?>? Arguments { get; set; }
    }

    public class ToolCallResult
    {
        [JsonPropertyName("content")]
        public List<McpContent> Content { get; set; } = new List<McpContent>();

        [JsonPropertyName("isError")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsError { get; set; }
    }

    public class PromptGetParams
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("arguments")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? Arguments { get; set; }
    }

    public class PromptGetResult
    {
        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        [JsonPropertyName("messages")]
        public List<PromptMessage> Messages { get; set; } = new List<PromptMessage>();
    }

    public class PromptMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        public McpContent Content { get; set; } = new McpContent();
    }

    public class ResourceReadParams
    {
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;
    }

    public class ResourceReadResult
    {
        [JsonPropertyName("contents")]
        public List<ResourceContent> Contents { get; set; } = new List<ResourceContent>();
    }

    public class ResourceContent
    {
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;

        [JsonPropertyName("mimeType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MimeType { get; set; }

        [JsonPropertyName("text")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Text { get; set; }

        [JsonPropertyName("blob")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Blob { get; set; }
    }

    public class PaginatedRequest
    {
        [JsonPropertyName("cursor")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Cursor { get; set; }
    }

    public class PaginatedResult<T>
    {
        [JsonPropertyName("nextCursor")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NextCursor { get; set; }

        [JsonPropertyName("items")]
        public List<T> Items { get; set; } = new List<T>();
    }
}
