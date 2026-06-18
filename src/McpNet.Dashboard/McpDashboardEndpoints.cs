using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace McpNet.Dashboard
{
    public static class McpDashboardEndpoints
    {
        private static readonly ConcurrentDictionary<string, string> _cache = new();

        private static async Task ServeResourceAsync(HttpContext ctx, string resourceName, string contentType)
        {
            if (!_cache.TryGetValue(resourceName, out var content))
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream is null) { ctx.Response.StatusCode = 404; return; }
                using var reader = new StreamReader(stream);
                content = await reader.ReadToEndAsync();
                _cache[resourceName] = content;
            }
            ctx.Response.ContentType = contentType;
            await ctx.Response.WriteAsync(content);
        }

        public static IEndpointConventionBuilder MapMcpDashboard(
            this IEndpointRouteBuilder endpoints,
            string pattern = "/dashboard")
        {
            endpoints.MapGet("/catalog.json", async (HttpContext ctx) =>
                await ServeResourceAsync(ctx, "McpNet.Dashboard.wwwroot.catalog.json", "application/json; charset=utf-8"));

            endpoints.MapGet("/dashboard.css", async (HttpContext ctx) =>
                await ServeResourceAsync(ctx, "McpNet.Dashboard.wwwroot.dashboard.css", "text/css; charset=utf-8"));

            endpoints.MapGet("/dashboard.js", async (HttpContext ctx) =>
                await ServeResourceAsync(ctx, "McpNet.Dashboard.wwwroot.dashboard.js", "text/javascript; charset=utf-8"));

            return endpoints.MapGet(pattern, async (HttpContext ctx) =>
                await ServeResourceAsync(ctx, "McpNet.Dashboard.wwwroot.dashboard.html", "text/html; charset=utf-8"));
        }
    }
}


