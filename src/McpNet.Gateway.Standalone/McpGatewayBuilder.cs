using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using McpNet.Gateway.Abstractions;
using McpNet.Gateway.Aggregation;
using McpNet.Gateway.Auth;
using McpNet.Gateway.Dashboard;
using McpNet.Gateway.Extensions;
using McpNet.Gateway.Groups;
using McpNet.Gateway.Registry;
using McpNet.Gateway.Routing;
using McpNet.Gateway.Security;
using McpNet.Gateway.Sessions;
using McpNet.Gateway.Standalone.Dashboard;
using McpNet.Gateway.Standalone.Management;
using McpNet.Gateway.Standalone.Security;
using McpNet.Transport.Http;

namespace McpNet.Gateway.Standalone
{

    public sealed class McpGatewayBuilder
    {
        private readonly McpGatewayOptions _opts = new McpGatewayOptions();

        private McpGatewayBuilder() { }

        public static McpGatewayBuilder Create() => new McpGatewayBuilder();

        public McpGatewayBuilder ListenOn(int port)                { _opts.Port            = port;   return this; }
        public McpGatewayBuilder WithPrefix(string prefix)         { _opts.ListenPrefix    = prefix; return this; }
        public McpGatewayBuilder WithDataDirectory(string dir)     { _opts.DataDirectory   = dir;    return this; }
        public McpGatewayBuilder WithMode(GatewayMode mode)        { _opts.Mode            = mode;   return this; }
        public McpGatewayBuilder WithAdminToken(string token)      { _opts.AdminToken      = token;  return this; }
        public McpGatewayBuilder WithMetaTools(bool enable = true) { _opts.EnableMetaTools = enable; return this; }

        public McpGatewayServer Build()
        {
            var dataDir = Path.GetFullPath(_opts.DataDirectory);
            Directory.CreateDirectory(dataDir);

            var services = new ServiceCollection();

            // Secret encryption (AES-256-GCM, no external dependencies)
            services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(dataDir));

            // JSON persistence
            services.AddMcpJsonPersistence(dataDir);

            // Auth
            var authOpts = new GatewayAuthOptions { Mode = _opts.Mode, AdminToken = _opts.AdminToken };
            services.AddSingleton(authOpts);
            services.AddSingleton<GatewayAuthenticator>(sp =>
                new GatewayAuthenticator(authOpts, sp.GetService<IClientRepository>()));

            // Session manager
            services.AddSingleton<GatewaySessionManager>();

            // Core services
            services.AddSingleton<ServerRegistry>();
            services.AddSingleton<ToolAggregator>();
            services.AddSingleton<ToolGroupManager>();
            services.AddSingleton<GatewayRequestRouter>();

            // Shared catalog service (no ASP.NET Core)
            services.AddSingleton(new GatewayCatalogService(dataDir));

            // MCP transport (McpNet.Transport.Http - no ASP.NET Core)
            services.AddSingleton(new HttpListenerMcpOptions { Port = _opts.Port, McpPath = "/mcp" });
            services.AddSingleton<HttpListenerMcpTransport>();

            var sp = services.BuildServiceProvider();
            return new McpGatewayServer(sp, _opts);
        }
    }
}
