using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Core.Capabilities;
using McpNet.Core.Protocol;
using McpNet.Core.Serialization;
using McpNet.Gateway.Auth;
using McpNet.Gateway.Models;
using McpNet.Gateway.Sessions;
using Xunit;

namespace McpNet.Tests
{
    public class JsonRpcSerializationTests
    {
        [Fact]
        public void Request_RoundTrip()
        {
            var req = new JsonRpcRequest { Id = 1, Method = McpMethods.ToolsList };
            var json = McpJsonOptions.Serialize(req);
            var back = McpJsonOptions.Deserialize<JsonRpcRequest>(json);
            Assert.Equal("tools/list", back!.Method);
            Assert.Equal(1, ((JsonElement)back.Id!).GetInt32());
        }

        [Fact]
        public void Response_WithError_RoundTrip()
        {
            var resp = new JsonRpcResponse
            {
                Id = "abc",
                Error = new JsonRpcError { Code = JsonRpcErrorCodes.MethodNotFound, Message = "Method not found" }
            };
            var json = McpJsonOptions.Serialize(resp);
            var back = McpJsonOptions.Deserialize<JsonRpcResponse>(json);
            Assert.NotNull(back!.Error);
            Assert.Equal(-32601, back.Error!.Code);
        }

        [Fact]
        public void McpContent_FromText_Serializes()
        {
            var c = McpContent.FromText("hello");
            var json = McpJsonOptions.Serialize(c);
            Assert.Contains("\"text\"", json);
            Assert.Contains("hello", json);
        }

        [Fact]
        public void McpJsonOptions_Convert_ReturnsTyped()
        {
            var tool = new McpTool { Name = "echo", Description = "Echo tool" };
            var element = JsonSerializer.Deserialize<JsonElement>(McpJsonOptions.Serialize(tool));
            var converted = McpJsonOptions.Convert<McpTool>(element);
            Assert.Equal("echo", converted!.Name);
        }
    }

    public class SessionManagerTests
    {
        [Fact]
        public void CreateAndGet_Session_Succeeds()
        {
            var mgr = new GatewaySessionManager();
            var id = mgr.CreateSession();
            Assert.False(string.IsNullOrEmpty(id));
            var ok = mgr.TryGetSession(id, out var session);
            Assert.True(ok);
            Assert.NotNull(session);
        }

        [Fact]
        public void Get_NonExistent_ReturnsFalse()
        {
            var mgr = new GatewaySessionManager();
            var ok = mgr.TryGetSession("nonexistent", out _);
            Assert.False(ok);
        }

        [Fact]
        public void Terminate_Session_Removes()
        {
            var mgr = new GatewaySessionManager();
            var id = mgr.CreateSession();
            mgr.TerminateSession(id);
            Assert.False(mgr.TryGetSession(id, out _));
        }

        [Fact]
        public void SessionId_Is_CryptographicallyRandom()
        {
            var mgr = new GatewaySessionManager();
            var ids = new HashSet<string>();
            for (int i = 0; i < 100; i++)
                ids.Add(mgr.CreateSession());
            Assert.Equal(100, ids.Count); // all unique
        }
    }

    public class AuthTests
    {
        [Fact]
        public void DevMode_AdminToken_AlwaysValid()
        {
            var opts = new GatewayAuthOptions { Mode = GatewayMode.Dev };
            var auth = new GatewayAuthenticator(opts, null!);
            Assert.True(auth.IsDevMode);
            Assert.True(auth.ValidateAdminToken("any-token"));
            Assert.True(auth.ValidateAdminToken(""));
            Assert.True(auth.ValidateAdminToken(null!));
        }

        [Fact]
        public void EnterpriseMode_AdminToken_ValidatesCorrectly()
        {
            var opts = new GatewayAuthOptions { Mode = GatewayMode.Enterprise, AdminToken = "secret123" };
            var auth = new GatewayAuthenticator(opts, null!);
            Assert.False(auth.IsDevMode);
            Assert.True(auth.ValidateAdminToken("secret123"));
            Assert.False(auth.ValidateAdminToken("wrongtoken"));
            Assert.False(auth.ValidateAdminToken(""));
        }

        [Fact]
        public async Task DevMode_AuthenticateClient_ReturnsNull_NoRestriction()
        {
            var opts = new GatewayAuthOptions { Mode = GatewayMode.Dev };
            var auth = new GatewayAuthenticator(opts, null!);
            var client = await auth.AuthenticateMcpClientAsync("any-token");
            Assert.Null(client); // dev mode returns null = unrestricted
        }
    }

    public class GatewayModelsTests
    {
        [Fact]
        public void RegisteredServer_Defaults_AreValid()
        {
            var s = new RegisteredServer { Name = "test", Url = "http://localhost/mcp" };
            Assert.NotEqual(Guid.Empty, s.Id);
            Assert.True(s.Enabled);
            Assert.Equal(UpstreamTransportType.StreamableHttp, s.TransportType);
            Assert.NotNull(s.CustomHeaders);
            Assert.NotNull(s.StdioArgs);
        }

        [Fact]
        public void ToolGroup_Defaults_AreValid()
        {
            var g = new ToolGroup { Name = "mygroup" };
            Assert.NotEqual(Guid.Empty, g.Id);
            Assert.NotNull(g.ToolNames);
        }

        [Fact]
        public void McpClient_Defaults_AreValid()
        {
            var c = new McpClient { Name = "client1", BearerToken = "token" };
            Assert.NotEqual(Guid.Empty, c.Id);
            Assert.True(c.Enabled);
        }
    }

    public class ProtocolConstantsTests
    {
        [Fact]
        public void ProtocolVersion_Current_Is_2025_06_18()
        {
            Assert.Equal("2025-06-18", McpProtocolVersion.Current);
        }

        [Fact]
        public void McpMethods_Constants_AreCorrect()
        {
            Assert.Equal("initialize", McpMethods.Initialize);
            Assert.Equal("tools/list", McpMethods.ToolsList);
            Assert.Equal("tools/call", McpMethods.ToolsCall);
            Assert.Equal("notifications/initialized", McpMethods.Initialized);
        }

        [Fact]
        public void JsonRpcErrorCodes_AreStandard()
        {
            Assert.Equal(-32700, JsonRpcErrorCodes.ParseError);
            Assert.Equal(-32600, JsonRpcErrorCodes.InvalidRequest);
            Assert.Equal(-32601, JsonRpcErrorCodes.MethodNotFound);
            Assert.Equal(-32602, JsonRpcErrorCodes.InvalidParams);
            Assert.Equal(-32603, JsonRpcErrorCodes.InternalError);
        }
    }
}
