using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using McpNet.Gateway.Aggregation;
using McpNet.Gateway.Models;
using McpNet.Gateway.Registry;
using McpNet.Gateway.Routing;
using Xunit;

namespace McpNet.Tests
{
    public class MetaToolHandlerTests
    {
        private static MetaToolHandler CreateHandler(out InMemoryServerRepository serverRepo, out InMemoryToolGroupRepository groupRepo)
        {
            serverRepo = new InMemoryServerRepository();
            groupRepo = new InMemoryToolGroupRepository();
            var registry = new ServerRegistry(serverRepo);
            var aggregator = new ToolAggregator(registry, serverRepo);
            return new MetaToolHandler(registry, aggregator, serverRepo, groupRepo);
        }

        [Fact]
        public void IsMetaTool_RecognizesPrefix()
        {
            var handler = CreateHandler(out _, out _);
            Assert.True(handler.IsMetaTool("mcpnet__list_servers"));
            Assert.False(handler.IsMetaTool("context7__resolve"));
            Assert.False(handler.IsMetaTool("list_servers"));
        }

        [Fact]
        public void GetToolDefinitions_IncludesCoreTools()
        {
            var handler = CreateHandler(out _, out _);
            var names = handler.GetToolDefinitions().Select(t => t.Name).ToList();

            Assert.Contains("mcpnet__list_servers", names);
            Assert.Contains("mcpnet__register_server", names);
            Assert.Contains("mcpnet__deregister_server", names);
            Assert.Contains("mcpnet__enable_tool", names);
            Assert.Contains("mcpnet__disable_tool", names);
            Assert.Contains("mcpnet__refresh", names);
        }

        [Fact]
        public void GetToolDefinitions_IncludesGroupTools_WhenGroupRepoPresent()
        {
            var handler = CreateHandler(out _, out _);
            var names = handler.GetToolDefinitions().Select(t => t.Name).ToList();
            Assert.Contains("mcpnet__create_group", names);
            Assert.Contains("mcpnet__list_groups", names);
        }

        [Fact]
        public void RegisterServer_Tool_RequiresName()
        {
            var handler = CreateHandler(out _, out _);
            var def = handler.GetToolDefinitions().First(t => t.Name == "mcpnet__register_server");
            Assert.NotNull(def.InputSchema.Required);
            Assert.Contains("name", def.InputSchema.Required!);
        }

        [Fact]
        public async Task RegisterServer_AddsServer()
        {
            var handler = CreateHandler(out var serverRepo, out _);

            var result = await handler.HandleAsync("mcpnet__register_server", new Dictionary<string, object?>
            {
                ["name"] = "context7",
                ["url"] = "https://mcp.context7.com/mcp",
                ["transport"] = "StreamableHttp"
            }, default);

            Assert.False(result.IsError);
            var servers = await serverRepo.GetAllAsync();
            Assert.Single(servers);
            Assert.Equal("context7", servers[0].Name);
        }

        [Fact]
        public async Task RegisterServer_MissingName_ReturnsError()
        {
            var handler = CreateHandler(out _, out _);
            var result = await handler.HandleAsync("mcpnet__register_server", new Dictionary<string, object?>(), default);
            Assert.True(result.IsError);
        }

        [Fact]
        public async Task ListServers_ReturnsRegistered()
        {
            var handler = CreateHandler(out var serverRepo, out _);
            await serverRepo.AddAsync(new RegisteredServer { Name = "srv1", Url = "http://a/mcp" });

            var result = await handler.HandleAsync("mcpnet__list_servers", null, default);

            Assert.False(result.IsError);
            Assert.Contains("srv1", result.Content[0].Text);
        }

        [Fact]
        public async Task DeregisterServer_RemovesServer()
        {
            var handler = CreateHandler(out var serverRepo, out _);
            await serverRepo.AddAsync(new RegisteredServer { Name = "todelete", Url = "http://a/mcp" });

            var result = await handler.HandleAsync("mcpnet__deregister_server", new Dictionary<string, object?>
            {
                ["name"] = "todelete"
            }, default);

            Assert.False(result.IsError);
            Assert.Empty(await serverRepo.GetAllAsync());
        }

        [Fact]
        public async Task DeregisterServer_NotFound_ReturnsError()
        {
            var handler = CreateHandler(out _, out _);
            var result = await handler.HandleAsync("mcpnet__deregister_server", new Dictionary<string, object?>
            {
                ["name"] = "ghost"
            }, default);
            Assert.True(result.IsError);
        }

        [Fact]
        public async Task CreateGroup_AddsGroup()
        {
            var handler = CreateHandler(out _, out var groupRepo);

            var result = await handler.HandleAsync("mcpnet__create_group", new Dictionary<string, object?>
            {
                ["name"] = "readonly",
                ["description"] = "read-only tools"
            }, default);

            Assert.False(result.IsError);
            var groups = await groupRepo.GetAllAsync();
            Assert.Single(groups);
            Assert.Equal("readonly", groups[0].Name);
        }

        [Fact]
        public async Task ListGroups_ReturnsGroups()
        {
            var handler = CreateHandler(out _, out var groupRepo);
            await groupRepo.AddAsync(new ToolGroup { Name = "grp-a" });

            var result = await handler.HandleAsync("mcpnet__list_groups", null, default);

            Assert.False(result.IsError);
            Assert.Contains("grp-a", result.Content[0].Text);
        }

        [Fact]
        public async Task UnknownMetaTool_ReturnsError()
        {
            var handler = CreateHandler(out _, out _);
            var result = await handler.HandleAsync("mcpnet__does_not_exist", null, default);
            Assert.True(result.IsError);
        }

        [Fact]
        public void NoGroupRepo_OmitsGroupTools()
        {
            var serverRepo = new InMemoryServerRepository();
            var registry = new ServerRegistry(serverRepo);
            var aggregator = new ToolAggregator(registry, serverRepo);
            var handler = new MetaToolHandler(registry, aggregator, serverRepo, groupRepo: null);

            var names = handler.GetToolDefinitions().Select(t => t.Name).ToList();
            Assert.DoesNotContain("mcpnet__create_group", names);
            Assert.DoesNotContain("mcpnet__list_groups", names);
        }
    }
}
