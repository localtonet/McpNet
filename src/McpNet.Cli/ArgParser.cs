using System;
using System.Collections.Generic;

namespace McpNet.Cli
{
    /// <summary>
    /// Minimal POSIX-style argument parser. Supports:
    ///   command [subcommand/positional...] --flag value --bool
    ///   Repeated flags (e.g. --arg "-y" --arg "@mcp/server") accumulate into GetAll().
    /// </summary>
    internal sealed class ParsedArgs
    {
        public string? Command { get; init; }
        public List<string> Positional { get; } = new();

        // Last value for each flag (backward-compatible single-value access)
        public Dictionary<string, string?> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

        // All values for each flag that appears more than once
        private readonly Dictionary<string, List<string>> _multi =
            new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string flag) => Flags.TryGetValue(flag, out var v) ? v : null;

        /// <summary>Returns every value supplied for <paramref name="flag"/>.
        /// E.g. --arg "-y" --arg "@mcp/server" → ["–y", "@mcp/server"]</summary>
        public List<string> GetAll(string flag) =>
            _multi.TryGetValue(flag, out var list) ? list : new List<string>();

        internal void SetFlag(string key, string? value)
        {
            Flags[key] = value;
            if (value != null)
            {
                if (!_multi.TryGetValue(key, out var list))
                    _multi[key] = list = new List<string>();
                list.Add(value);
            }
        }
    }

    internal static class ArgParser
    {
        public static ParsedArgs Parse(string[] args)
        {
            string? command = null;
            var positional = new List<string>();
            var flags = new List<(string key, string? value)>();

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--", StringComparison.Ordinal))
                {
                    var key = a.Substring(2);
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                        flags.Add((key, args[++i]));
                    else
                        flags.Add((key, null)); // boolean flag
                }
                else if (a.StartsWith("-", StringComparison.Ordinal) && a.Length > 1)
                {
                    flags.Add((a.Substring(1), null));
                }
                else if (command == null)
                {
                    command = a;
                }
                else
                {
                    positional.Add(a);
                }
            }

            var parsed = new ParsedArgs { Command = command };
            parsed.Positional.AddRange(positional);
            foreach (var (key, value) in flags)
                parsed.SetFlag(key, value);
            return parsed;
        }
    }
}

