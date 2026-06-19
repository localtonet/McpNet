namespace McpNet.Gateway.Security
{
    /// <summary>
    /// Passthrough implementation - no encryption.
    /// Used when Data Protection is not configured (Dev mode / tests).
    /// </summary>
    public sealed class NullSecretProtector : ISecretProtector
    {
        public static readonly NullSecretProtector Instance = new();

        public string? Protect(string? plaintext) => plaintext;
        public string? Unprotect(string? value) => value;
    }
}
