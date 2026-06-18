namespace McpNet.Gateway.Security
{
    /// <summary>
    /// Encrypts and decrypts secret values before they are persisted to disk.
    /// Encrypted values are stored with the prefix <c>enc:</c> so the system
    /// can detect and auto-decrypt them on load, providing backward compatibility
    /// with existing plaintext JSON files.
    ///
    /// Default implementation: <see cref="NullSecretProtector"/> (passthrough).
    /// Production implementation: DataProtectionSecretProtector in McpNet.Host
    /// (uses ASP.NET Core Data Protection — DPAPI on Windows, file-based on Linux/macOS).
    /// </summary>
    public interface ISecretProtector
    {
        /// <summary>
        /// Encrypts <paramref name="plaintext"/> and returns an <c>enc:</c>-prefixed ciphertext.
        /// Returns <see langword="null"/> or empty unchanged.
        /// </summary>
        string? Protect(string? plaintext);

        /// <summary>
        /// Decrypts an <c>enc:</c>-prefixed value back to plaintext.
        /// Plaintext values (no prefix) are returned as-is for backward compatibility.
        /// Returns <see langword="null"/> or empty unchanged.
        /// </summary>
        string? Unprotect(string? value);
    }
}
