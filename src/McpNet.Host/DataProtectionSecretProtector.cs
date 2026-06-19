using System;
using Microsoft.AspNetCore.DataProtection;
using McpNet.Gateway.Security;

namespace McpNet.Host
{
    /// <summary>
    /// <see cref="ISecretProtector"/> implementation backed by ASP.NET Core Data Protection.
    ///
    /// On Windows this uses DPAPI (tied to the machine/user account).
    /// On Linux/macOS it uses AES-256-CBC with keys stored in the configured key ring
    /// (default: ~/.aspnet/DataProtection-Keys or the path set via PersistKeysToFileSystem).
    ///
    /// Encrypted values are stored as <c>enc:&lt;base64&gt;</c> so plaintext values
    /// (from existing unencrypted JSON files) are detected and returned as-is
    /// (backward-compatible migration path).
    /// </summary>
    internal sealed class DataProtectionSecretProtector : ISecretProtector
    {
        private const string Prefix = "enc:";
        private const string Purpose = "McpNet.Gateway.Secrets.v1";

        private readonly IDataProtector _dp;

        public DataProtectionSecretProtector(IDataProtectionProvider provider)
        {
            _dp = provider.CreateProtector(Purpose);
        }

        /// <inheritdoc/>
        public string? Protect(string? plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return plaintext;

            // Already encrypted - don't double-encrypt
            if (plaintext.StartsWith(Prefix, StringComparison.Ordinal)) return plaintext;

            return Prefix + _dp.Protect(plaintext);
        }

        /// <inheritdoc/>
        public string? Unprotect(string? value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            // Plaintext (legacy / never encrypted) - return as-is
            if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return value;

            try
            {
                return _dp.Unprotect(value.Substring(Prefix.Length));
            }
            catch
            {
                // Key rotation / corruption: return as-is and let the caller handle the error
                // rather than crashing the whole gateway on startup.
                return value;
            }
        }
    }
}
