using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Gateway.Auth;
using McpNet.Gateway.Standalone;

namespace McpNet.Standalone.TestApp
{
    internal static class Program
    {
        private const int Port = 5050;
        private static readonly string Base = $"http://localhost:{Port}";
        private static readonly HttpClient Http = new HttpClient();

        public static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=================================================");
            Console.WriteLine("  McpNet.Gateway.Standalone  -  Manual Test App  ");
            Console.WriteLine("=================================================");
            Console.ResetColor();

            // ── Start gateway ───────────────────────────────────────────────
            var gateway = McpGatewayBuilder.Create()
                .ListenOn(Port)
                .WithDataDirectory("mcp-test-data")
                .WithMode(GatewayMode.Dev)           // open, no token needed
                .Build();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            await gateway.StartAsync();

            Ok($"Gateway started on port {Port}");
            Info($"Dashboard  → {Base}/dashboard");
            Info($"Health     → {Base}/health");
            Info($"MCP        → {Base}/mcp");
            Info($"API        → {Base}/api/servers");
            Console.WriteLine();
            Info("Press Ctrl+C to stop. Type a command or press Enter to run all quick tests.");
            Console.WriteLine();

            // ── Interactive loop ────────────────────────────────────────────
            while (!cts.Token.IsCancellationRequested)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("test> ");
                Console.ResetColor();

                string? line;
                try   { line = Console.ReadLine(); }
                catch { break; }

                if (line is null || cts.IsCancellationRequested) break;

                var cmd = line.Trim().ToLowerInvariant();

                if (cmd == "" || cmd == "all")
                {
                    await RunAllAsync();
                }
                else
                {
                    switch (cmd)
                    {
                        case "health":   await TestHealthAsync();   break;
                        case "info":     await TestInfoAsync();     break;
                        case "servers":  await TestServersAsync();  break;
                        case "tools":    await TestToolsAsync();    break;
                        case "catalog":  await TestCatalogAsync();  break;
                        case "dashboard":await TestDashboardAsync();break;
                        case "help":     PrintHelp();               break;
                        case "exit":
                        case "quit":
                        case "q":        cts.Cancel();              break;
                        default:
                            Warn($"Unknown command '{cmd}'. Type 'help' for list.");
                            break;
                    }
                }

                Console.WriteLine();
            }

            // ── Shutdown ────────────────────────────────────────────────────
            Console.WriteLine();
            Info("Stopping gateway...");
            await gateway.StopAsync();
            await gateway.DisposeAsync();
            Ok("Gateway stopped. Bye!");
        }

        // ── Test runners ────────────────────────────────────────────────────

        private static async Task RunAllAsync()
        {
            await TestHealthAsync();
            await TestInfoAsync();
            await TestServersAsync();
            await TestToolsAsync();
            await TestCatalogAsync();
            await TestDashboardAsync();
        }

        private static async Task TestHealthAsync()
        {
            Header("GET /health");
            await GetAsync("/health");
        }

        private static async Task TestInfoAsync()
        {
            Header("GET /api/info");
            await GetAsync("/api/info");
        }

        private static async Task TestServersAsync()
        {
            Header("GET /api/servers");
            await GetAsync("/api/servers");

            Header("POST /api/servers  (register test server)");
            var body = """
                {
                  "name": "test-server",
                  "url": "http://localhost:9999/mcp",
                  "transportType": "StreamableHttp"
                }
                """;
            var resp = await PostAsync("/api/servers", body);
            if (resp != null)
            {
                Header("GET /api/servers  (after register)");
                await GetAsync("/api/servers");

                // Try to extract id and delete
                try
                {
                    using var doc = JsonDocument.Parse(resp);
                    if (doc.RootElement.TryGetProperty("id", out var idEl))
                    {
                        var id = idEl.GetString();
                        Header($"DELETE /api/servers/{id}");
                        await DeleteAsync($"/api/servers/{id}");
                    }
                }
                catch { }
            }
        }

        private static async Task TestToolsAsync()
        {
            Header("GET /api/tools");
            await GetAsync("/api/tools");

            Header("GET /api/tools/status");
            await GetAsync("/api/tools/status");
        }

        private static async Task TestCatalogAsync()
        {
            Header("GET /api/catalog");
            var resp = await GetAsync("/api/catalog");
            if (resp != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(resp);
                    if (doc.RootElement.TryGetProperty("servers", out var svrs))
                        Info($"  → {svrs.GetArrayLength()} catalog entries");
                }
                catch { }
            }

            Header("GET /api/catalog/search?q=github");
            await GetAsync("/api/catalog/search?q=github");

            Header("POST /api/catalog/custom  (add custom entry)");
            var entry = """
                {
                  "title": "My Test Tool",
                  "description": "A custom catalog entry added by the test app",
                  "category": "Testing",
                  "command": "npx",
                  "args": ["-y", "my-test-mcp"]
                }
                """;
            var addResp = await PostAsync("/api/catalog/custom", entry);

            if (addResp != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(addResp);
                    if (doc.RootElement.TryGetProperty("name", out var nameEl))
                    {
                        var name = nameEl.GetString();
                        Header($"DELETE /api/catalog/custom/{name}");
                        await DeleteAsync($"/api/catalog/custom/{name}");
                    }
                }
                catch { }
            }
        }

        private static async Task TestDashboardAsync()
        {
            Header("GET /dashboard  (HTML)");
            try
            {
                var resp = await Http.GetAsync($"{Base}/dashboard");
                var status = (int)resp.StatusCode;
                var len = resp.Content.Headers.ContentLength ?? -1;
                var ct = resp.Content.Headers.ContentType?.MediaType ?? "?";
                if (resp.IsSuccessStatusCode)
                    Ok($"  {status}  {ct}  ({len} bytes)");
                else
                    Fail($"  {status}");
            }
            catch (Exception ex) { Fail($"  ERROR: {ex.Message}"); }

            Header("GET /dashboard.js");
            await HeadAsync("/dashboard.js");

            Header("GET /dashboard.css");
            await HeadAsync("/dashboard.css");
        }

        // ── HTTP helpers ────────────────────────────────────────────────────

        private static async Task<string?> GetAsync(string path)
        {
            try
            {
                var resp = await Http.GetAsync($"{Base}{path}");
                var body = await resp.Content.ReadAsStringAsync();
                var status = (int)resp.StatusCode;
                if (resp.IsSuccessStatusCode)
                {
                    Ok($"  {status}");
                    PrintJson(body);
                    return body;
                }
                else
                {
                    Fail($"  {status}  {body}");
                    return null;
                }
            }
            catch (Exception ex) { Fail($"  ERROR: {ex.Message}"); return null; }
        }

        private static async Task<string?> PostAsync(string path, string json)
        {
            try
            {
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await Http.PostAsync($"{Base}{path}", content);
                var body = await resp.Content.ReadAsStringAsync();
                var status = (int)resp.StatusCode;
                if (resp.IsSuccessStatusCode)
                {
                    Ok($"  {status}");
                    PrintJson(body);
                    return body;
                }
                else
                {
                    Fail($"  {status}  {body}");
                    return null;
                }
            }
            catch (Exception ex) { Fail($"  ERROR: {ex.Message}"); return null; }
        }

        private static async Task DeleteAsync(string path)
        {
            try
            {
                var resp = await Http.DeleteAsync($"{Base}{path}");
                var status = (int)resp.StatusCode;
                if (resp.IsSuccessStatusCode) Ok($"  {status}  (deleted)");
                else Fail($"  {status}");
            }
            catch (Exception ex) { Fail($"  ERROR: {ex.Message}"); }
        }

        private static async Task HeadAsync(string path)
        {
            try
            {
                var resp = await Http.GetAsync($"{Base}{path}");
                var status = (int)resp.StatusCode;
                var ct = resp.Content.Headers.ContentType?.MediaType ?? "?";
                var len = resp.Content.Headers.ContentLength ?? (await resp.Content.ReadAsByteArrayAsync()).Length;
                if (resp.IsSuccessStatusCode) Ok($"  {status}  {ct}  ({len} bytes)");
                else Fail($"  {status}");
            }
            catch (Exception ex) { Fail($"  ERROR: {ex.Message}"); }
        }

        // ── Output helpers ──────────────────────────────────────────────────

        private static void PrintHelp()
        {
            Console.WriteLine("Commands:");
            Console.WriteLine("  all (or Enter)  Run all tests");
            Console.WriteLine("  health          GET /health");
            Console.WriteLine("  info            GET /api/info");
            Console.WriteLine("  servers         Register + list + delete a server");
            Console.WriteLine("  tools           GET /api/tools + /api/tools/status");
            Console.WriteLine("  catalog         Catalog read + custom add/delete");
            Console.WriteLine("  dashboard       Fetch dashboard HTML/JS/CSS");
            Console.WriteLine("  help            Show this list");
            Console.WriteLine("  q / quit        Stop and exit");
        }

        private static void PrintJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var pretty = JsonSerializer.Serialize(doc.RootElement,
                    new JsonSerializerOptions { WriteIndented = true });
                // Truncate long responses
                var lines = pretty.Split('\n');
                var limit = Math.Min(lines.Length, 30);
                for (var i = 0; i < limit; i++)
                    Console.WriteLine("    " + lines[i]);
                if (lines.Length > 30)
                    Console.WriteLine($"    ... ({lines.Length - 30} more lines)");
            }
            catch
            {
                Console.WriteLine("    " + json[..Math.Min(json.Length, 200)]);
            }
        }

        private static void Header(string text)
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"  ── {text}");
            Console.ResetColor();
        }

        private static void Ok(string text)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(text);
            Console.ResetColor();
        }

        private static void Fail(string text)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(text);
            Console.ResetColor();
        }

        private static void Warn(string text)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(text);
            Console.ResetColor();
        }

        private static void Info(string text)
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(text);
            Console.ResetColor();
        }
    }
}
