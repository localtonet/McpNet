using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpNet.Cli
{
    /// <summary>
    /// Persistent CLI configuration stored in <c>~/.mcpnet/config.json</c>.
    /// Holds the gateway URL and admin token so you don't have to pass
    /// --url / --token on every command.
    ///
    /// Priority chain (highest to lowest):
    ///   1. CLI flags:    --url / --token
    ///   2. Env vars:     MCPNET_URL  /  MCPNET_ADMIN_TOKEN
    ///   3. Config file:  ~/.mcpnet/config.json
    ///   4. Default:      http://localhost:5050  /  (none)
    /// </summary>
    internal sealed class CliConfig
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("token")]
        public string? Token { get; set; }

        // ── Config file path ─────────────────────────────────────────────────

        public static string ConfigPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mcpnet", "config.json");

        // ── Load ─────────────────────────────────────────────────────────────

        public static CliConfig Load()
        {
            if (!File.Exists(ConfigPath))
                return new CliConfig();

            try
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<CliConfig>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new CliConfig();
            }
            catch
            {
                // Corrupt config - return empty, don't crash
                return new CliConfig();
            }
        }

        // ── Save ─────────────────────────────────────────────────────────────

        public static void Save(CliConfig cfg)
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(dir);

            // chmod-equivalent: write file then restrict permissions on Unix
            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);

            // Restrict to owner-read-only on Unix/macOS (token is sensitive)
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch { /* best-effort */ }
            }
        }

        // ── Clear ─────────────────────────────────────────────────────────────

        public static void Clear()
        {
            if (File.Exists(ConfigPath))
                File.Delete(ConfigPath);
        }
    }
}
