using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Gateway.Abstractions;
using McpNet.Gateway.Models;
namespace McpNet.Gateway.Auth
{
    public enum GatewayMode { Dev, Enterprise }

    public class GatewayAuthOptions
    {
        public GatewayMode Mode { get; set; } = GatewayMode.Dev;
        public string AdminToken { get; set; } = string.Empty;
    }

    public class GatewayAuthenticator
    {
        private readonly GatewayAuthOptions _options;
        private readonly IClientRepository? _clientRepo;

        public GatewayAuthenticator(GatewayAuthOptions options, IClientRepository? clientRepo = null)
        {
            _options = options;
            _clientRepo = clientRepo;
        }

        public bool IsDevMode => _options.Mode == GatewayMode.Dev;

        public bool ValidateAdminToken(string? token)
        {
            if (IsDevMode) return true;
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(_options.AdminToken)) return false;
            // Constant-time comparison to avoid leaking the token via timing side-channels.
            var a = Encoding.UTF8.GetBytes(token);
            var b = Encoding.UTF8.GetBytes(_options.AdminToken);
            return CryptographicOperations.FixedTimeEquals(a, b);
        }

        public async Task<McpClient?> AuthenticateMcpClientAsync(string? bearerToken, CancellationToken ct = default)
        {
            if (IsDevMode) return null;
            if (string.IsNullOrEmpty(bearerToken) || _clientRepo == null) return null;
            return await _clientRepo.GetByTokenAsync(bearerToken, ct).ConfigureAwait(false);
        }

        public bool RequiresMcpAuth => !IsDevMode;
    }
}
