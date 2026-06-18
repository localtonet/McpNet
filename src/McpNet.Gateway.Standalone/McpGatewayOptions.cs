using McpNet.Gateway.Auth;

namespace McpNet.Gateway.Standalone
{
    /// <summary>Fluent configuration for <see cref="McpGatewayServer"/>.</summary>
    public sealed class McpGatewayOptions
    {
        /// <summary>TCP port the HttpListener binds to. Default: 5050.</summary>
        public int Port { get; set; } = 5050;

        /// <summary>
        /// HttpListener prefix. Default: <c>http://localhost:{Port}/</c>.
        /// Use <c>http://*:{Port}/</c> to bind all interfaces
        /// (requires <c>netsh http add urlacl</c> on Windows or root on Linux).
        /// </summary>
        public string? ListenPrefix { get; set; }

        /// <summary>Directory where JSON data files and encryption keys are stored.</summary>
        public string DataDirectory { get; set; } = "mcp-data";

        /// <summary>Authentication mode: <see cref="GatewayMode.Dev"/> (open) or
        /// <see cref="GatewayMode.Enterprise"/> (token-required).</summary>
        public GatewayMode Mode { get; set; } = GatewayMode.Dev;

        /// <summary>Admin token for the management API. Required in Enterprise mode.</summary>
        public string AdminToken { get; set; } = string.Empty;

        /// <summary>Enable gateway self-management meta-tools.</summary>
        public bool EnableMetaTools { get; set; } = false;
    }
}
