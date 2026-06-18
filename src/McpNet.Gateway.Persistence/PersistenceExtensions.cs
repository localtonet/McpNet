using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using McpNet.Gateway.Abstractions;
using McpNet.Gateway.Persistence.Repositories;

namespace McpNet.Gateway.Persistence
{
    public static class PersistenceServiceCollectionExtensions
    {
        /// <summary>
        /// Registers GatewayDbContext with SQLite or PostgreSQL.
        /// </summary>
        public static IServiceCollection AddMcpGatewayDbContext(
            this IServiceCollection services,
            string connectionString,
            bool usePostgres = false)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString));

            services.AddDbContext<GatewayDbContext>(opts =>
            {
                if (usePostgres)
                    opts.UseNpgsql(connectionString);
                else
                    opts.UseSqlite(connectionString);
            });

            return services;
        }

        /// <summary>
        /// Registers EF Core repository implementations.
        /// Call after <see cref="AddMcpGatewayDbContext"/>.
        /// </summary>
        public static IServiceCollection AddMcpEfRepositories(this IServiceCollection services)
        {
            services.AddScoped<IServerRepository,    ServerRepository>();
            services.AddScoped<IToolGroupRepository, ToolGroupRepository>();
            services.AddScoped<IClientRepository,    ClientRepository>();
            services.AddScoped<IAuditLogRepository,  AuditLogRepository>();
            return services;
        }
    }
}
