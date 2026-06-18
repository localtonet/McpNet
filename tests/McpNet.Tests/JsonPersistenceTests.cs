using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using McpNet.Gateway.Models;
using McpNet.Gateway.Persistence.Json;
using Xunit;

namespace McpNet.Tests
{
    public class JsonPersistenceTests : IDisposable
    {
        private readonly string _dir;

        public JsonPersistenceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "mcpnet-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        // ─── Server repository ───────────────────────────────────────────────
        [Fact]
        public async Task Server_Add_Then_GetAll_Persists()
        {
            var repo = new JsonServerRepository(_dir);
            var server = new RegisteredServer { Name = "s1", Url = "http://a/mcp" };
            await repo.AddAsync(server);

            // Fresh instance reads from disk
            var repo2 = new JsonServerRepository(_dir);
            var all = await repo2.GetAllAsync();

            Assert.Single(all);
            Assert.Equal("s1", all[0].Name);
        }

        [Fact]
        public async Task Server_GetByName_Works()
        {
            var repo = new JsonServerRepository(_dir);
            await repo.AddAsync(new RegisteredServer { Name = "findme", Url = "http://a/mcp" });

            var found = await repo.GetByNameAsync("findme");
            Assert.NotNull(found);
            Assert.Equal("findme", found!.Name);
        }

        [Fact]
        public async Task Server_Update_Persists()
        {
            var repo = new JsonServerRepository(_dir);
            var s = new RegisteredServer { Name = "old", Url = "http://a/mcp" };
            await repo.AddAsync(s);

            s.Name = "new";
            await repo.UpdateAsync(s);

            var found = await repo.GetByIdAsync(s.Id);
            Assert.Equal("new", found!.Name);
        }

        [Fact]
        public async Task Server_Delete_Removes()
        {
            var repo = new JsonServerRepository(_dir);
            var s = new RegisteredServer { Name = "temp", Url = "http://a/mcp" };
            await repo.AddAsync(s);
            await repo.DeleteAsync(s.Id);

            Assert.Empty(await repo.GetAllAsync());
        }

        [Fact]
        public async Task Server_OAuthConfig_RoundTrips()
        {
            var repo = new JsonServerRepository(_dir);
            var s = new RegisteredServer
            {
                Name = "oauth-srv",
                Url = "http://a/mcp",
                OAuth = new OAuthConfig
                {
                    Enabled = true,
                    TokenUrl = "https://auth/token",
                    ClientId = "cid",
                    ClientSecret = "secret",
                    Scopes = { "a", "b" }
                }
            };
            await repo.AddAsync(s);

            var repo2 = new JsonServerRepository(_dir);
            var found = await repo2.GetByIdAsync(s.Id);

            Assert.NotNull(found!.OAuth);
            Assert.Equal("https://auth/token", found.OAuth!.TokenUrl);
            Assert.Equal(2, found.OAuth.Scopes.Count);
        }

        // ─── Group repository ────────────────────────────────────────────────
        [Fact]
        public async Task Group_CRUD_Works()
        {
            var repo = new JsonToolGroupRepository(_dir);
            var g = new ToolGroup { Name = "grp" };
            await repo.AddAsync(g);

            g.ToolNames.Add("srv__tool");
            await repo.UpdateAsync(g);

            var found = await repo.GetByIdAsync(g.Id);
            Assert.Contains("srv__tool", found!.ToolNames);

            await repo.DeleteAsync(g.Id);
            Assert.Empty(await repo.GetAllAsync());
        }

        // ─── Client repository ───────────────────────────────────────────────
        [Fact]
        public async Task Client_GetByToken_Works()
        {
            var repo = new JsonClientRepository(_dir);
            await repo.AddAsync(new McpClient { Name = "c1", BearerToken = "tok-abc" });

            var found = await repo.GetByTokenAsync("tok-abc");
            Assert.NotNull(found);
            Assert.Equal("c1", found!.Name);
        }

        // ─── Audit log repository (NDJSON) ───────────────────────────────────
        [Fact]
        public async Task Audit_Append_And_ReadRecent()
        {
            var repo = new JsonAuditLogRepository(_dir);
            for (int i = 0; i < 5; i++)
                await repo.AddAsync(new AuditLog { ToolName = "tool" + i, Success = true });

            var recent = await repo.GetRecentAsync(3);
            Assert.Equal(3, recent.Count);
            // Returns chronological order, last 3 entries
            Assert.Equal("tool2", recent[0].ToolName);
            Assert.Equal("tool4", recent[2].ToolName);
        }

        [Fact]
        public async Task Audit_EmptyFile_ReturnsEmpty()
        {
            var repo = new JsonAuditLogRepository(_dir);
            Assert.Empty(await repo.GetRecentAsync());
        }

        [Fact]
        public async Task Server_AtomicWrite_NoTempLeftover()
        {
            var repo = new JsonServerRepository(_dir);
            await repo.AddAsync(new RegisteredServer { Name = "s", Url = "http://a/mcp" });

            Assert.False(File.Exists(Path.Combine(_dir, "servers.json.tmp")));
            Assert.True(File.Exists(Path.Combine(_dir, "servers.json")));
        }
    }
}
