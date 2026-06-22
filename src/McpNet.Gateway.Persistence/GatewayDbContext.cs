using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using McpNet.Gateway.Models;

namespace McpNet.Gateway.Persistence
{
    public class GatewayDbContext : DbContext
    {
        public GatewayDbContext(DbContextOptions<GatewayDbContext> options) : base(options) { }

        public DbSet<RegisteredServer> Servers => Set<RegisteredServer>();
        public DbSet<ToolGroup> ToolGroups => Set<ToolGroup>();
        public DbSet<McpClient> Clients => Set<McpClient>();
        public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RegisteredServer>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Name).IsRequired().HasMaxLength(200);
                e.HasIndex(x => x.Name).IsUnique();
                e.Property(x => x.CustomHeaders)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(v, (JsonSerializerOptions?)null)!);
                e.Property(x => x.StdioArgs)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Deserialize<System.Collections.Generic.List<string>>(v, (JsonSerializerOptions?)null)!);
                e.Property(x => x.StdioEnvVars)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(v, (JsonSerializerOptions?)null)!);
                e.Property(x => x.OAuth)
                    .HasConversion(
                        v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                        v => string.IsNullOrEmpty(v) ? null : JsonSerializer.Deserialize<OAuthConfig>(v, (JsonSerializerOptions?)null));
                e.Property(x => x.ToolAliases)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(v, (JsonSerializerOptions?)null)!);
                // CacheTtlSeconds and AutoRestart are primitive columns - no converter needed.
            });

            modelBuilder.Entity<ToolGroup>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Name).IsRequired().HasMaxLength(200);
                e.Property(x => x.ToolNames)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Deserialize<System.Collections.Generic.List<string>>(v, (JsonSerializerOptions?)null)!);
            });

            modelBuilder.Entity<McpClient>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Name).IsRequired().HasMaxLength(200);
                e.HasIndex(x => x.BearerToken).IsUnique();
                e.Property(x => x.AllowedServerIds)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Deserialize<System.Collections.Generic.List<Guid>>(v, (JsonSerializerOptions?)null)!);
                e.Property(x => x.AllowedGroupIds)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Deserialize<System.Collections.Generic.List<Guid>>(v, (JsonSerializerOptions?)null)!);
                e.Property(x => x.ServerRateLimits)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Deserialize<System.Collections.Generic.List<ServerRateLimit>>(v, (JsonSerializerOptions?)null)!);
            });

            modelBuilder.Entity<UserAccount>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.Username).IsUnique();
            });

            modelBuilder.Entity<AuditLog>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.Timestamp);
            });
        }
    }
}
