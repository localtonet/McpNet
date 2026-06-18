namespace McpNet.Core.Protocol
{
    public static class McpMethods
    {
        public const string Initialize = "initialize";
        public const string Initialized = "notifications/initialized";
        public const string Ping = "ping";
        public const string ToolsList = "tools/list";
        public const string ToolsCall = "tools/call";
        public const string PromptsList = "prompts/list";
        public const string PromptsGet = "prompts/get";
        public const string ResourcesList = "resources/list";
        public const string ResourcesRead = "resources/read";
        public const string ResourcesSubscribe = "resources/subscribe";
        public const string ResourcesUnsubscribe = "resources/unsubscribe";
        public const string LoggingSetLevel = "logging/setLevel";
        public const string Cancel = "notifications/cancelled";
        public const string Progress = "notifications/progress";
        public const string ToolsListChanged = "notifications/tools/list_changed";
        public const string PromptsListChanged = "notifications/prompts/list_changed";
        public const string ResourcesListChanged = "notifications/resources/list_changed";
    }

    public static class McpProtocolVersion
    {
        public const string Current = "2025-06-18";
        public const string Legacy = "2025-03-26";
    }

    public static class McpHeaders
    {
        public const string SessionId = "Mcp-Session-Id";
        public const string ProtocolVersion = "MCP-Protocol-Version";
        public const string AdminToken = "X-Admin-Token";
    }
}
