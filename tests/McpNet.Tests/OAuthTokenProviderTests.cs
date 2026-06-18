using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Gateway.Models;
using McpNet.Gateway.Upstream;
using Xunit;

namespace McpNet.Tests
{
    public class OAuthTokenProviderTests
    {
        private static OAuthConfig Config() => new()
        {
            Enabled = true,
            TokenUrl = "https://auth.example.com/token",
            ClientId = "cid",
            ClientSecret = "secret",
            Scopes = { "mcp.read" }
        };

        private static HttpResponseMessage TokenResponse(string token, int expiresIn = 3600)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"access_token\":\"{token}\",\"token_type\":\"Bearer\",\"expires_in\":{expiresIn}}}",
                    Encoding.UTF8, "application/json")
            };

        [Fact]
        public async Task GetAccessToken_FetchesToken()
        {
            var stub = new StubHttpMessageHandler(_ => TokenResponse("tok-1"));
            var provider = new OAuthTokenProvider(Config(), new HttpClient(stub));

            var token = await provider.GetAccessTokenAsync();

            Assert.Equal("tok-1", token);
            Assert.Equal(1, stub.CallCount);
        }

        [Fact]
        public async Task GetAccessToken_CachesUntilExpiry()
        {
            var stub = new StubHttpMessageHandler(_ => TokenResponse("tok-cached", expiresIn: 3600));
            var provider = new OAuthTokenProvider(Config(), new HttpClient(stub));

            var t1 = await provider.GetAccessTokenAsync();
            var t2 = await provider.GetAccessTokenAsync();

            Assert.Equal(t1, t2);
            Assert.Equal(1, stub.CallCount); // second call served from cache
        }

        [Fact]
        public async Task Invalidate_ForcesRefetch()
        {
            var counter = 0;
            var stub = new StubHttpMessageHandler(_ => TokenResponse($"tok-{++counter}"));
            var provider = new OAuthTokenProvider(Config(), new HttpClient(stub));

            var t1 = await provider.GetAccessTokenAsync();
            provider.Invalidate();
            var t2 = await provider.GetAccessTokenAsync();

            Assert.NotEqual(t1, t2);
            Assert.Equal(2, stub.CallCount);
        }

        [Fact]
        public async Task NearExpiry_RefreshesEarly()
        {
            // expires_in below the 30s refresh skew → never considered valid, always refetch
            var counter = 0;
            var stub = new StubHttpMessageHandler(_ => TokenResponse($"tok-{++counter}", expiresIn: 10));
            var provider = new OAuthTokenProvider(Config(), new HttpClient(stub));

            await provider.GetAccessTokenAsync();
            await provider.GetAccessTokenAsync();

            Assert.Equal(2, stub.CallCount);
        }

        [Fact]
        public async Task FailedTokenRequest_Throws()
        {
            var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("invalid_client")
            });
            var provider = new OAuthTokenProvider(Config(), new HttpClient(stub));

            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccessTokenAsync());
        }

        [Fact]
        public async Task MissingAccessToken_Throws()
        {
            var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token_type\":\"Bearer\"}", Encoding.UTF8, "application/json")
            });
            var provider = new OAuthTokenProvider(Config(), new HttpClient(stub));

            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccessTokenAsync());
        }

        [Fact]
        public async Task SendsClientCredentialsGrant()
        {
            string? capturedBody = null;
            var stub = new StubHttpMessageHandler(req =>
            {
#pragma warning disable xUnit1031 // synchronous read required inside non-async handler delegate
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                return TokenResponse("tok");
            });
            var provider = new OAuthTokenProvider(Config(), new HttpClient(stub));

            await provider.GetAccessTokenAsync();

            Assert.NotNull(capturedBody);
            Assert.Contains("grant_type=client_credentials", capturedBody);
            Assert.Contains("client_id=cid", capturedBody);
            Assert.Contains("scope=mcp.read", capturedBody);
        }
    }
}
