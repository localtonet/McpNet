using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace McpNet.Gateway.Upstream
{
    /// <summary>
    /// Utilities for resolving stdio command paths and detecting runtimes (Node.js, Python, etc.)
    /// used by stdio-based MCP upstream servers.
    /// </summary>
    public static class StdioCommandHelper
    {
        /// <summary>
        /// Resolves a bare command (e.g. "npx") to its full path on Windows (.cmd priority)
        /// or returns it unchanged when already absolute.
        /// </summary>
        public static string ResolveCommandPath(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return command;

            if (Path.IsPathRooted(command)
                || command.Contains(Path.DirectorySeparatorChar)
                || command.Contains(Path.AltDirectorySeparatorChar))
                return command;

            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var dirs    = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // On Windows prefer .cmd then .exe over the bare shim (which is not a native executable)
            var candidates = new List<string>();
            if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(Path.GetExtension(command)))
            {
                candidates.Add(command + ".cmd");
                candidates.Add(command + ".exe");
                candidates.Add(command + ".bat");
                candidates.Add(command + ".com");
            }
            candidates.Add(command);

            foreach (var dir in dirs)
                foreach (var c in candidates)
                {
                    var full = Path.Combine(dir, c);
                    if (File.Exists(full)) return full;
                }

            return command;
        }

        /// <summary>
        /// Detects the runtime environment for the given stdio command and returns
        /// a human-readable summary (e.g. "node v22.1.0 / npx v10.5.0").
        /// Returns null when the runtime cannot be detected.
        /// </summary>
        public static async Task<string?> GetRuntimeInfoAsync(string resolvedCommand, CancellationToken ct)
        {
            var lower = resolvedCommand.ToLowerInvariant();

            if (lower.Contains("npx") || lower.Contains("node"))
                return await GetNodeInfoAsync(ct).ConfigureAwait(false);

            if (lower.Contains("python") || lower.Contains("uvx") || lower.Contains("uv"))
                return await GetPythonInfoAsync(ct).ConfigureAwait(false);

            return null;
        }

        // ── Node.js ───────────────────────────────────────────────────────────

        private static async Task<string?> GetNodeInfoAsync(CancellationToken ct)
        {
            var nodeVer = await RunVersionAsync("node", "--version", ct).ConfigureAwait(false);
            if (nodeVer is null) return null;
            var npmVer  = await RunVersionAsync("npm",  "--version", ct).ConfigureAwait(false);
            return npmVer is null ? $"node {nodeVer}" : $"node {nodeVer} / npm {npmVer}";
        }

        // ── Python ────────────────────────────────────────────────────────────

        private static async Task<string?> GetPythonInfoAsync(CancellationToken ct)
        {
            var ver = await RunVersionAsync("python3", "--version", ct).ConfigureAwait(false)
                   ?? await RunVersionAsync("python", "--version", ct).ConfigureAwait(false);
            return ver is null ? null : $"python {ver}";
        }

        // ── Helper ────────────────────────────────────────────────────────────

        private static async Task<string?> RunVersionAsync(string command, string args, CancellationToken ct)
        {
            try
            {
                var resolvedCmd = ResolveCommandPath(command);
                var cmdExt      = Path.GetExtension(resolvedCmd).ToLowerInvariant();
                var runViaCmd   = OperatingSystem.IsWindows()
                                  && (cmdExt == ".cmd" || cmdExt == ".bat");

                var psi = new ProcessStartInfo
                {
                    FileName               = runViaCmd ? "cmd.exe" : resolvedCmd,
                    Arguments              = runViaCmd ? $"/c {EscapeArg(resolvedCmd)} {args}" : args,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                };

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                using var proc = Process.Start(psi);
                if (proc is null) return null;

                var stdout = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
                var stderr = await proc.StandardError.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

                var raw = (stdout + stderr).Trim();
                // Strip "node vX.X.X" → "vX.X.X"
                foreach (var prefix in new[] { "Python ", "python ", "node " })
                    if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return raw.Substring(prefix.Length).Trim();
                return raw.Length > 0 ? raw.Split('\n')[0].Trim() : null;
            }
            catch { return null; }
        }

        private static string EscapeArg(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return value.Contains(' ') || value.Contains('"')
                ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
                : value;
        }
    }
}
