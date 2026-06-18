using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using McpNet.Gateway.Security;

namespace McpNet.Gateway.Standalone.Security
{
    public sealed class AesGcmSecretProtector : ISecretProtector
    {
        private const string Prefix    = "enc:";
        private const int    NonceSize = 12;   // GCM standard nonce
        private const int    TagSize   = 16;   // AES-GCM tag

        private readonly byte[] _key;          // 32 bytes → AES-256

        public AesGcmSecretProtector(string dataDirectory)
        {
            _key = LoadOrCreateKey(dataDirectory);
        }

        // ── ISecretProtector ─────────────────────────────────────────────────

        public string? Protect(string? plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return plaintext;
            if (plaintext.StartsWith(Prefix, StringComparison.Ordinal)) return plaintext; // already encrypted

            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var nonce          = new byte[NonceSize];
            var ciphertext     = new byte[plaintextBytes.Length];
            var tag            = new byte[TagSize];

            RandomNumberGenerator.Fill(nonce);

            using var aes = new AesGcm(_key, TagSize);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

            // Layout: nonce | ciphertext | tag
            var payload = new byte[NonceSize + ciphertext.Length + TagSize];
            Buffer.BlockCopy(nonce,       0, payload, 0,                                NonceSize);
            Buffer.BlockCopy(ciphertext,  0, payload, NonceSize,                        ciphertext.Length);
            Buffer.BlockCopy(tag,         0, payload, NonceSize + ciphertext.Length,    TagSize);

            return Prefix + Convert.ToBase64String(payload);
        }

        public string? Unprotect(string? value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return value; // plaintext — backward compat

            byte[] payload;
            try { payload = Convert.FromBase64String(value.Substring(Prefix.Length)); }
            catch { return value; } // corrupt — return as-is, let caller deal with it

            if (payload.Length < NonceSize + TagSize) return value;

            var nonce      = payload.AsSpan(0, NonceSize);
            var tag        = payload.AsSpan(payload.Length - TagSize, TagSize);
            var ciphertext = payload.AsSpan(NonceSize, payload.Length - NonceSize - TagSize);
            var plaintext  = new byte[ciphertext.Length];

            try
            {
                using var aes = new AesGcm(_key, TagSize);
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
                return Encoding.UTF8.GetString(plaintext);
            }
            catch
            {
                // Key mismatch / corruption — return encrypted value as-is rather than crashing.
                return value;
            }
        }

        // ── Key management ───────────────────────────────────────────────────

        private static byte[] LoadOrCreateKey(string dataDirectory)
        {
            var dir     = Path.Combine(dataDirectory, "dp-keys");
            var keyFile = Path.Combine(dir, "aes.key");

            if (File.Exists(keyFile))
            {
                var existing = File.ReadAllBytes(keyFile);
                if (existing.Length == 32) return existing;
            }

            // Generate new key
            var key = new byte[32];
            RandomNumberGenerator.Fill(key);

            Directory.CreateDirectory(dir);
            File.WriteAllBytes(keyFile, key);

            // Restrict permissions on Unix
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch { }
            }

            return key;
        }
    }
}
