using System;
using System.Collections.Generic;

namespace McpNet.Gateway.Models
{
    public enum UpstreamTransportType
    {
        StreamableHttp,
        Sse,
        Stdio,
        RestOpenApi
    }

    public class RegisteredServer
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string? Url { get; set; }
        public UpstreamTransportType TransportType { get; set; } = UpstreamTransportType.StreamableHttp;
        public bool Enabled { get; set; } = true;
        public string? BearerToken { get; set; }
        public Dictionary<string, string> CustomHeaders { get; set; } = new Dictionary<string, string>();
        public string? StdioCommand { get; set; }
        public List<string> StdioArgs { get; set; } = new List<string>();
        public string? StdioWorkingDirectory { get; set; }
        /// <summary>
        /// Environment variables injected into the stdio child process.
        /// Used to pass API keys (e.g. MINIMAX_API_KEY) without storing them in args.
        /// </summary>
        public Dictionary<string, string> StdioEnvVars { get; set; } = new Dictionary<string, string>();
        public OAuthConfig? OAuth { get; set; }
        /// <summary>
        /// REST / OpenAPI upstream configuration. Set when <see cref="TransportType"/> is
        /// <see cref="UpstreamTransportType.RestOpenApi"/>; the gateway parses the OpenAPI document
        /// and projects each operation as an MCP tool.
        /// </summary>
        public RestApiConfig? Rest { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Configuration for a REST / OpenAPI upstream. The document is supplied either by URL
    /// (<see cref="SpecUrl"/>) or inline (<see cref="InlineSpec"/>); operations can be filtered
    /// by HTTP method and by name before being projected as MCP tools.
    /// </summary>
    public class RestApiConfig
    {
        /// <summary>URL of the OpenAPI / Swagger document to fetch and parse.</summary>
        public string? SpecUrl { get; set; }

        /// <summary>Inline OpenAPI / Swagger document (JSON), used instead of <see cref="SpecUrl"/>.</summary>
        public string? InlineSpec { get; set; }

        /// <summary>Base URL for API calls, overriding any value derived from the document.</summary>
        public string? BaseUrl { get; set; }

        /// <summary>When non-empty, only these HTTP methods are projected (e.g. get, post).</summary>
        public List<string> IncludeMethods { get; set; } = new List<string>();

        /// <summary>When non-empty, only operations matching one of these tokens are projected.</summary>
        public List<string> IncludeOperations { get; set; } = new List<string>();

        /// <summary>Operations matching any of these tokens are excluded from projection.</summary>
        public List<string> ExcludeOperations { get; set; } = new List<string>();

        /// <summary>Maximum number of tools to generate (0 = unlimited).</summary>
        public int MaxTools { get; set; }

        /// <summary>Allow calls to private/loopback addresses (disables the SSRF guard).</summary>
        public bool AllowPrivateNetwork { get; set; }
    }

    /// <summary>
    /// OAuth 2.0 client-credentials configuration for authenticating to an upstream
    /// MCP server. When set, the gateway fetches and caches an access token, refreshing
    /// automatically before expiry, and sends it as a Bearer token on upstream requests.
    /// </summary>
    public class OAuthConfig
    {
        public bool Enabled { get; set; } = true;
        public string TokenUrl { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public List<string> Scopes { get; set; } = new List<string>();
    }

    public class ToolGroup
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public List<string> ToolNames { get; set; } = new List<string>();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class ServerRateLimit
    {
        public Guid ServerId { get; set; }
        public int LimitPerMinute { get; set; }
    }

    public class McpClient
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string BearerToken { get; set; } = string.Empty;
        public List<Guid> AllowedServerIds { get; set; } = new List<Guid>();
        public List<Guid> AllowedGroupIds { get; set; } = new List<Guid>();
        public bool Enabled { get; set; } = true;
        public int RateLimitPerMinute { get; set; } = 0;
        // Per-server overrides - takes precedence over RateLimitPerMinute for matching server.
        public List<ServerRateLimit> ServerRateLimits { get; set; } = new List<ServerRateLimit>();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class UserAccount
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Username { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string Role { get; set; } = "admin";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class AuditLog
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string? ClientId { get; set; }
        public string? ClientName { get; set; }
        public string Method { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public string? ServerName { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public long DurationMs { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class AggregatedTool
    {
        public string FullName { get; set; } = string.Empty;
        public string LocalName { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public Guid ServerId { get; set; }
        public bool Enabled { get; set; } = true;
        public McpNet.Core.Capabilities.McpTool Definition { get; set; } = new McpNet.Core.Capabilities.McpTool();
    }
}
