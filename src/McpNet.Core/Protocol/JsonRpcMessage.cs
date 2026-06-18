using System.Text.Json.Serialization;

namespace McpNet.Core.Protocol
{
    public abstract class JsonRpcMessageBase
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; } = "2.0";
    }

    public class JsonRpcRequest : JsonRpcMessageBase
    {
        [JsonPropertyName("id")]
        public object? Id { get; set; }

        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("params")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Params { get; set; }
    }

    public class JsonRpcResponse : JsonRpcMessageBase
    {
        [JsonPropertyName("id")]
        public object? Id { get; set; }

        [JsonPropertyName("result")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Result { get; set; }

        [JsonPropertyName("error")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonRpcError? Error { get; set; }

        /// <summary>Set by the router after initialize; transported via Mcp-Session-Id response header.</summary>
        [JsonIgnore]
        public string? SessionId { get; set; }
    }

    public class JsonRpcNotification : JsonRpcMessageBase
    {
        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("params")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Params { get; set; }
    }

    public class JsonRpcError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Data { get; set; }
    }

    public static class JsonRpcErrorCodes
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;
    }
}
