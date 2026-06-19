using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using McpNet.Core.Serialization;

namespace McpNet.Cli
{
    internal static class Program
    {
        private const string DefaultBaseUrl = "http://localhost:5050";

        public static async Task<int> Main(string[] args)
        {
            var parsed = ArgParser.Parse(args);

            if (parsed.Command is null || parsed.Flags.ContainsKey("help") || parsed.Flags.ContainsKey("h"))
            {
                PrintHelp();
                return 0;
            }

            // ── configure is the only command that doesn't need api ────────
            if (parsed.Command == "configure")
                return ConfigureCmd(parsed);

            // Resolve gateway URL + admin token:
            // priority: flag > env var > config file > default
            var fileCfg   = CliConfig.Load();
            var baseUrl   = parsed.Get("url")
                ?? Environment.GetEnvironmentVariable("MCPNET_URL")
                ?? fileCfg.Url
                ?? DefaultBaseUrl;
            var token     = parsed.Get("token")
                ?? Environment.GetEnvironmentVariable("MCPNET_ADMIN_TOKEN")
                ?? fileCfg.Token;

            using var api = new ApiClient(baseUrl, token);

            try
            {
                return parsed.Command switch
                {
                    "register"   => await RegisterServer(api, parsed),
                    "deregister" => await DeregisterServer(api, parsed),
                    "servers"    => await ListServers(api),
                    "tools"      => await ListTools(api, parsed),
                    "enable"     => await ToggleTool(api, parsed, true),
                    "disable"    => await ToggleTool(api, parsed, false),
                    "refresh"    => await RefreshTools(api),
                    "groups"     => await ListGroups(api),
                    "group"      => await GroupCmd(api, parsed),
                    "clients"    => await ListClients(api),
                    "client"     => await ClientCmd(api, parsed),
                    "audit"      => await ShowAudit(api),
                    "version"    => PrintVersion(),
                    "whoami"     => Whoami(baseUrl, fileCfg),
                    _ => Unknown(parsed.Command)
                };
            }
            catch (CliException ex)
            {
                Error(ex.Message);
                return 1;
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                Error($"Cannot reach gateway at {baseUrl} - {ex.Message}");
                return 2;
            }
        }

        // ── configure ─────────────────────────────────────────────────────────
        private static int ConfigureCmd(ParsedArgs a)
        {
            // mcpnet configure --url http://... --token abc123
            // mcpnet configure --show
            // mcpnet configure --clear
            if (a.Flags.ContainsKey("clear"))
            {
                CliConfig.Clear();
                Success($"Config cleared ({CliConfig.ConfigPath})");
                return 0;
            }
            if (a.Flags.ContainsKey("show"))
            {
                var c = CliConfig.Load();
                Console.WriteLine($"Config file : {CliConfig.ConfigPath}");
                Console.WriteLine($"url         : {c.Url ?? "(not set)"}");
                Console.WriteLine($"token       : {(c.Token != null ? new string('*', Math.Min(c.Token.Length, 6)) + "..." : "(not set)")}");
                return 0;
            }

            var cfg  = CliConfig.Load();
            var url  = a.Get("url");
            var tok  = a.Get("token");
            if (url == null && tok == null)
            {
                Error("usage: mcpnet configure --url <url> --token <token>  (or --show / --clear)");
                return 1;
            }
            if (url  != null) cfg.Url   = url;
            if (tok  != null) cfg.Token = tok;
            CliConfig.Save(cfg);
            Success($"Config saved to {CliConfig.ConfigPath}");
            return 0;
        }

        private static int Whoami(string baseUrl, CliConfig cfg)
        {
            Console.WriteLine($"Gateway : {baseUrl}");
            Console.WriteLine($"Token   : {(cfg.Token != null ? new string('*', Math.Min(cfg.Token.Length, 6)) + "..." : "(none - Dev mode or env var)")}" );
            Console.WriteLine($"Source  : {CliConfig.ConfigPath}");
            return 0;
        }

        // ── register ──────────────────────────────────────────────────────────
        private static async Task<int> RegisterServer(ApiClient api, ParsedArgs a)
        {
            var name = a.Get("name") ?? a.Positional.ElementAtOrDefault(0);
            if (string.IsNullOrWhiteSpace(name)) { Error("--name is required"); return 1; }

            var transport = a.Get("transport") ?? "StreamableHttp";
            // --arg can be repeated: --arg "-y" --arg "@mcp/server" --arg "/path/with spaces"
            // Fallback: --args "arg1 arg2" (split by space, broken for paths with spaces)
            var argList = a.GetAll("arg");
            if (argList.Count == 0 && a.Get("args") is { } argsFlat)
                argList = new System.Collections.Generic.List<string>(
                    argsFlat.Split(' ', StringSplitOptions.RemoveEmptyEntries));

            // --env KEY=VALUE can be repeated
            var envVars = new Dictionary<string, string>();
            foreach (var env in a.GetAll("env"))
            {
                var idx = env.IndexOf('=');
                if (idx > 0) envVars[env.Substring(0, idx)] = env.Substring(idx + 1);
            }

            var body = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["transportType"] = transport,
                ["url"] = a.Get("server-url") ?? a.Get("url-upstream"),
                ["bearerToken"] = a.Get("bearer") ?? a.Get("auth-token"),
                ["stdioCommand"] = a.Get("command"),
                ["stdioArgs"] = argList,
                ["stdioEnvVars"] = envVars
            };

            var result = await api.PostAsync("/api/servers", body);
            Success($"Registered server '{name}'");
            PrintJson(result);
            return 0;
        }

        // ── deregister ────────────────────────────────────────────────────────
        private static async Task<int> DeregisterServer(ApiClient api, ParsedArgs a)
        {
            var name = a.Get("name") ?? a.Positional.ElementAtOrDefault(0);
            if (string.IsNullOrWhiteSpace(name)) { Error("server name is required"); return 1; }

            var id = await ResolveServerId(api, name);
            if (id is null) { Error($"Server '{name}' not found"); return 1; }

            await api.DeleteAsync($"/api/servers/{id}");
            Success($"Deregistered '{name}'");
            return 0;
        }

        // ── servers ───────────────────────────────────────────────────────────
        private static async Task<int> ListServers(ApiClient api)
        {
            var json = await api.GetAsync("/api/servers");
            using var doc = JsonDocument.Parse(json);
            var rows = new List<string[]>();
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                rows.Add(new[]
                {
                    s.GetPropertyOrEmpty("name"),
                    s.GetPropertyOrEmpty("transportType"),
                    s.GetPropertyOrEmpty("url"),
                    s.TryGetProperty("enabled", out var en) && en.GetBoolean() ? "✓" : "✗"
                });
            }
            Table(new[] { "NAME", "TRANSPORT", "URL", "ON" }, rows);
            return 0;
        }

        // ── tools ─────────────────────────────────────────────────────────────
        private static async Task<int> ListTools(ApiClient api, ParsedArgs a)
        {
            var json = await api.GetAsync("/api/tools");
            using var doc = JsonDocument.Parse(json);
            var filter = a.Get("server");
            var rows = new List<string[]>();
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                var serverName = t.GetPropertyOrEmpty("serverName");
                if (filter != null && !serverName.Equals(filter, StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(new[]
                {
                    t.GetPropertyOrEmpty("fullName"),
                    serverName,
                    t.TryGetProperty("enabled", out var en) && en.GetBoolean() ? "✓" : "✗"
                });
            }
            Table(new[] { "TOOL", "SERVER", "ON" }, rows);
            return 0;
        }

        private static async Task<int> ToggleTool(ApiClient api, ParsedArgs a, bool enable)
        {
            var fullName = a.Positional.ElementAtOrDefault(0) ?? a.Get("tool");
            if (string.IsNullOrWhiteSpace(fullName)) { Error("tool full name is required (server__tool)"); return 1; }

            var serverName = fullName.Split("__")[0];
            var id = await ResolveServerId(api, serverName);
            if (id is null) { Error($"Server '{serverName}' not found"); return 1; }

            await api.PatchAsync($"/api/servers/{id}/tools/{Uri.EscapeDataString(fullName)}/toggle?enabled={enable.ToString().ToLowerInvariant()}");
            Success($"{(enable ? "Enabled" : "Disabled")} '{fullName}'");
            return 0;
        }

        private static async Task<int> RefreshTools(ApiClient api)
        {
            await api.PostAsync("/api/tools/refresh", null);
            Success("Tool cache refreshed");
            return 0;
        }

        // ── groups ────────────────────────────────────────────────────────────
        private static async Task<int> ListGroups(ApiClient api)
        {
            var json = await api.GetAsync("/api/groups");
            using var doc = JsonDocument.Parse(json);
            var rows = new List<string[]>();
            foreach (var g in doc.RootElement.EnumerateArray())
            {
                var count = g.TryGetProperty("toolNames", out var tn) && tn.ValueKind == JsonValueKind.Array
                    ? tn.GetArrayLength() : 0;
                rows.Add(new[]
                {
                    g.GetPropertyOrEmpty("name"),
                    g.GetPropertyOrEmpty("description"),
                    count.ToString()
                });
            }
            Table(new[] { "GROUP", "DESCRIPTION", "TOOLS" }, rows);
            return 0;
        }

        private static async Task<int> GroupCmd(ApiClient api, ParsedArgs a)
        {
            var sub = a.Positional.ElementAtOrDefault(0);
            switch (sub)
            {
                case "create":
                {
                    var name = a.Get("name") ?? a.Positional.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(name)) { Error("--name is required"); return 1; }
                    await api.PostAsync("/api/groups", new { name, description = a.Get("description") });
                    Success($"Created group '{name}'");
                    return 0;
                }
                case "delete":
                {
                    var name = a.Get("name") ?? a.Positional.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(name)) { Error("group name is required"); return 1; }
                    var id = await ResolveGroupId(api, name);
                    if (id is null) { Error($"Group '{name}' not found"); return 1; }
                    await api.DeleteAsync($"/api/groups/{id}");
                    Success($"Deleted group '{name}'");
                    return 0;
                }
                case "add-tool":
                {
                    var groupName = a.Get("group") ?? a.Positional.ElementAtOrDefault(1);
                    var toolName  = a.Get("tool")  ?? a.Positional.ElementAtOrDefault(2);
                    if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(toolName))
                    { Error("usage: mcpnet group add-tool <groupName> <toolName>"); return 1; }
                    var id = await ResolveGroupId(api, groupName);
                    if (id is null) { Error($"Group '{groupName}' not found"); return 1; }
                    await api.PostAsync($"/api/groups/{id}/tools", new { toolName });
                    Success($"Added '{toolName}' to group '{groupName}'");
                    return 0;
                }
                case "remove-tool":
                {
                    var groupName = a.Get("group") ?? a.Positional.ElementAtOrDefault(1);
                    var toolName  = a.Get("tool")  ?? a.Positional.ElementAtOrDefault(2);
                    if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(toolName))
                    { Error("usage: mcpnet group remove-tool <groupName> <toolName>"); return 1; }
                    var id = await ResolveGroupId(api, groupName);
                    if (id is null) { Error($"Group '{groupName}' not found"); return 1; }
                    await api.DeleteAsync($"/api/groups/{id}/tools/{Uri.EscapeDataString(toolName)}");
                    Success($"Removed '{toolName}' from group '{groupName}'");
                    return 0;
                }
                default:
                    Error("usage: mcpnet group <create|delete|add-tool|remove-tool> ...");
                    return 1;
            }
        }

        // ── clients ───────────────────────────────────────────────────────────
        private static async Task<int> ListClients(ApiClient api)
        {
            var json = await api.GetAsync("/api/clients");
            using var doc = JsonDocument.Parse(json);
            var rows = new List<string[]>();
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                rows.Add(new[]
                {
                    c.GetPropertyOrEmpty("name"),
                    c.TryGetProperty("enabled", out var en) && en.GetBoolean() ? "✓" : "✗",
                    c.GetPropertyOrEmpty("allowedServers"),
                    c.GetPropertyOrEmpty("allowedGroups")
                });
            }
            Table(new[] { "CLIENT", "ON", "SERVERS", "GROUPS" }, rows);
            return 0;
        }

        private static async Task<int> ClientCmd(ApiClient api, ParsedArgs a)
        {
            var sub = a.Positional.ElementAtOrDefault(0);
            switch (sub)
            {
                case "create":
                {
                    var name = a.Get("name") ?? a.Positional.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(name)) { Error("--name is required"); return 1; }
                    var result = await api.PostAsync("/api/clients", new { name });
                    Success($"Created client '{name}'");
                    PrintJson(result);
                    Console.WriteLine("\n⚠  Save the bearerToken now - it won't be shown again.");
                    return 0;
                }
                case "delete":
                {
                    var name = a.Get("name") ?? a.Positional.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(name)) { Error("client name is required"); return 1; }
                    var id = await ResolveClientId(api, name);
                    if (id is null) { Error($"Client '{name}' not found"); return 1; }
                    await api.DeleteAsync($"/api/clients/{id}");
                    Success($"Deleted client '{name}'");
                    return 0;
                }
                case "regenerate":
                {
                    var name = a.Get("name") ?? a.Positional.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(name)) { Error("client name is required"); return 1; }
                    var id = await ResolveClientId(api, name);
                    if (id is null) { Error($"Client '{name}' not found"); return 1; }
                    var result = await api.PostAsync($"/api/clients/{id}/regenerate", null);
                    Success($"Token regenerated for '{name}'");
                    PrintJson(result);
                    Console.WriteLine("\n⚠  Save the new bearerToken now - it won't be shown again.");
                    return 0;
                }
                case "update":
                {
                    var name = a.Get("name") ?? a.Positional.ElementAtOrDefault(1);
                    if (string.IsNullOrWhiteSpace(name)) { Error("client name is required"); return 1; }
                    var id = await ResolveClientId(api, name);
                    if (id is null) { Error($"Client '{name}' not found"); return 1; }
                    var patch = new Dictionary<string, object?>();
                    if (a.Get("rate-limit") is { } rl && int.TryParse(rl, out var rli))
                        patch["rateLimitPerMinute"] = rli;
                    if (a.Get("enabled") is { } en)
                        patch["enabled"] = bool.Parse(en);
                    await api.PutAsync($"/api/clients/{id}", patch);
                    Success($"Updated client '{name}'");
                    return 0;
                }
                default:
                {
                    // mcpnet client <name>  →  show client detail
                    if (sub != null)
                    {
                        var id = await ResolveClientId(api, sub);
                        if (id is null) { Error($"Client '{sub}' not found"); return 1; }
                        var detail = await api.GetAsync($"/api/clients/{id}");
                        PrintJson(detail);
                        return 0;
                    }
                    Error("usage: mcpnet client <create|delete|regenerate|update|<name>>");
                    return 1;
                }
            }
        }

        // ── audit ─────────────────────────────────────────────────────────────
        private static async Task<int> ShowAudit(ApiClient api)
        {
            var json = await api.GetAsync("/api/audit");
            using var doc = JsonDocument.Parse(json);
            var rows = new List<string[]>();
            foreach (var e in doc.RootElement.EnumerateArray().Reverse().Take(30))
            {
                rows.Add(new[]
                {
                    e.GetPropertyOrEmpty("timestamp"),
                    e.GetPropertyOrEmpty("toolName"),
                    e.GetPropertyOrEmpty("serverName"),
                    e.TryGetProperty("success", out var ok) && ok.GetBoolean() ? "OK" : "ERR",
                    e.GetPropertyOrEmpty("durationMs") + "ms"
                });
            }
            Table(new[] { "TIME", "TOOL", "SERVER", "STATUS", "DUR" }, rows);
            return 0;
        }

        // ── helpers ───────────────────────────────────────────────────────────
        private static async Task<string?> ResolveServerId(ApiClient api, string name)
        {
            var json = await api.GetAsync("/api/servers");
            using var doc = JsonDocument.Parse(json);
            foreach (var s in doc.RootElement.EnumerateArray())
                if (s.GetPropertyOrEmpty("name").Equals(name, StringComparison.OrdinalIgnoreCase))
                    return s.GetPropertyOrEmpty("id");
            return null;
        }

        private static async Task<string?> ResolveGroupId(ApiClient api, string name)
        {
            var json = await api.GetAsync("/api/groups");
            using var doc = JsonDocument.Parse(json);
            foreach (var g in doc.RootElement.EnumerateArray())
                if (g.GetPropertyOrEmpty("name").Equals(name, StringComparison.OrdinalIgnoreCase))
                    return g.GetPropertyOrEmpty("id");
            return null;
        }

        private static async Task<string?> ResolveClientId(ApiClient api, string name)
        {
            var json = await api.GetAsync("/api/clients");
            using var doc = JsonDocument.Parse(json);
            foreach (var c in doc.RootElement.EnumerateArray())
                if (c.GetPropertyOrEmpty("name").Equals(name, StringComparison.OrdinalIgnoreCase))
                    return c.GetPropertyOrEmpty("id");
            return null;
        }

        private static int PrintVersion()
        {
            Console.WriteLine("mcpnet " + (typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev"));
            return 0;
        }

        private static int Unknown(string cmd)
        {
            Error($"Unknown command '{cmd}'. Run 'mcpnet --help'.");
            return 1;
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"mcpnet - McpNet Gateway CLI

USAGE
  mcpnet <command> [options]

GLOBAL OPTIONS
  --url <url>        Gateway base URL (default http://localhost:5050, env MCPNET_URL)
  --token <token>    Admin token for enterprise mode (env MCPNET_ADMIN_TOKEN)

COMMANDS
  configure --url <url> --token <t>   Save gateway URL and admin token to config file
  configure --show                    Print current config file values
  configure --clear                   Delete config file
  whoami                              Show resolved gateway URL and token source
  register --name <n> --server-url <url> [--bearer <t>] [--transport <type>]
                     Register an upstream MCP server
  register --name <n> --transport Stdio --command <cmd>
           [--arg <a1> --arg <a2> ...]   Stdio args (repeat --arg for each)
           [--env KEY=VALUE ...]         Env vars for stdio child process
                     Register a stdio (subprocess) MCP server
  deregister <name>  Remove a registered server
  servers            List registered servers
  tools [--server <n>]   List aggregated tools
  enable <server__tool>  Enable a tool
  disable <server__tool> Disable a tool
  refresh            Refresh the tool cache from upstream servers
  groups             List tool groups
  group create --name <n> [--description <d>]
  group delete <name>
  group add-tool <groupName> <toolName>
  group remove-tool <groupName> <toolName>
  clients            List MCP clients (enterprise)
  client <name>                          Show client detail
  client create --name <n>
  client delete <name>
  client regenerate <name>               Regenerate bearer token
  client update <name> [--rate-limit <n>] [--enabled true|false]
  audit              Show recent audit log entries
  version            Print version

EXAMPLES
  mcpnet configure --url http://localhost:5050 --token 111111111111111111
  mcpnet configure --show
  mcpnet register --name context7 --server-url https://mcp.context7.com/mcp
  mcpnet register --name fs --transport Stdio --command npx --arg -y --arg @modelcontextprotocol/server-filesystem --arg ~/docs
  mcpnet servers
  mcpnet tools --server context7
  mcpnet disable context7__resolve-library-id
  mcpnet client create --name myagent
  mcpnet client regenerate myagent
  mcpnet group create --name search-tools
  mcpnet group add-tool search-tools context7__resolve-library-id");
        }

        // ── output formatting ──────────────────────────────────────────────────
        private static void Table(string[] headers, List<string[]> rows)
        {
            if (rows.Count == 0) { Console.WriteLine("(no results)"); return; }
            var widths = new int[headers.Length];
            for (int i = 0; i < headers.Length; i++) widths[i] = headers[i].Length;
            foreach (var r in rows)
                for (int i = 0; i < r.Length && i < widths.Length; i++)
                    widths[i] = Math.Max(widths[i], (r[i] ?? "").Length);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))));
            Console.ResetColor();
            foreach (var r in rows)
                Console.WriteLine(string.Join("  ", r.Select((c, i) => (c ?? "").PadRight(widths[i]))));
        }

        private static void PrintJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { Console.WriteLine(json); }
        }

        private static void Success(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ " + msg);
            Console.ResetColor();
        }

        private static void Error(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("✗ " + msg);
            Console.ResetColor();
        }
    }

    internal static class JsonExtensions
    {
        public static string GetPropertyOrEmpty(this JsonElement el, string name)
        {
            if (el.TryGetProperty(name, out var p))
            {
                return p.ValueKind switch
                {
                    JsonValueKind.String => p.GetString() ?? "",
                    JsonValueKind.Number => p.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "",
                    _ => p.GetRawText()
                };
            }
            return "";
        }
    }
}
