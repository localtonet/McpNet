using System;
using Microsoft.Extensions.DependencyInjection;
using McpNet.Gateway.Abstractions;
using McpNet.Gateway.Persistence.Json;
using McpNet.Gateway.Security;

namespace McpNet.Gateway.Extensions
{
    public static class JsonPersistenceServiceCollectionExtensions
    {
        /// <summary>
        /// Registers JSON file-based repository implementations.
        /// No database required - data is stored as JSON files in <paramref name="dataDirectory"/>.
        /// Secrets (BearerToken, OAuthConfig.ClientSecret, McpClient.BearerToken) are encrypted
        /// at rest using the registered <see cref="ISecretProtector"/> (defaults to
        /// <see cref="NullSecretProtector"/> if none is registered).
        /// </summary>
        public static IServiceCollection AddMcpJsonPersistence(
            this IServiceCollection services,
            string dataDirectory = "mcp-data")
        {
            if (string.IsNullOrWhiteSpace(dataDirectory))
                throw new ArgumentException("Data directory cannot be empty.", nameof(dataDirectory));

            // Use factory registrations so ISecretProtector is resolved from DI
            services.AddSingleton<IServerRepository>(sp =>
                new JsonServerRepository(dataDirectory,
                    sp.GetService<ISecretProtector>() ?? NullSecretProtector.Instance));

            services.AddSingleton<IToolGroupRepository>(
                new JsonToolGroupRepository(dataDirectory));

            services.AddSingleton<IClientRepository>(sp =>
                new JsonClientRepository(dataDirectory,
                    sp.GetService<ISecretProtector>() ?? NullSecretProtector.Instance));

            services.AddSingleton<IAuditLogRepository>(
                new JsonAuditLogRepository(dataDirectory));

            return services;
        }

        /// <summary>
        /// Registers JSON file-based repository implementations with custom options.
        /// </summary>
        public static IServiceCollection AddMcpJsonPersistence(
            this IServiceCollection services,
            Action<JsonPersistenceOptions> configure)
        {
            var opts = new JsonPersistenceOptions();
            configure(opts);

            services.AddSingleton<IServerRepository>(sp =>
                new JsonServerRepository(opts.DataDirectory,
                    sp.GetService<ISecretProtector>() ?? NullSecretProtector.Instance));

            services.AddSingleton<IToolGroupRepository>(
                new JsonToolGroupRepository(opts.DataDirectory));

            services.AddSingleton<IClientRepository>(sp =>
                new JsonClientRepository(opts.DataDirectory,
                    sp.GetService<ISecretProtector>() ?? NullSecretProtector.Instance));

            services.AddSingleton<IAuditLogRepository>(
                new JsonAuditLogRepository(opts.DataDirectory, opts.AuditLogReadLimit));

            services.AddSingleton<IToolStateStore>(
                new JsonToolStateStore(opts.DataDirectory));

            return services;
        }
    }
}

