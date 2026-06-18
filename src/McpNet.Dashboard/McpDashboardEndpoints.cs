using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using McpNet.Gateway.Dashboard;

namespace McpNet.Dashboard
{
    public static class McpDashboardEndpoints
    {
        public static IEndpointConventionBuilder MapMcpDashboard(
            this IEndpointRouteBuilder endpoints,
            string pattern = "/dashboard")
        {
            endpoints.MapGet("/catalog.json",  async (HttpContext ctx) => await ServeAsync(ctx, "catalog.json",  "application/json; charset=utf-8"));
            endpoints.MapGet("/dashboard.css", async (HttpContext ctx) => await ServeAsync(ctx, "dashboard.css", "text/css; charset=utf-8"));
            endpoints.MapGet("/dashboard.js",  async (HttpContext ctx) => await ServeAsync(ctx, "dashboard.js",  "text/javascript; charset=utf-8"));

            return endpoints.MapGet(pattern,   async (HttpContext ctx) => await ServeAsync(ctx, "dashboard.html", "text/html; charset=utf-8"));
        }

        private static async Task ServeAsync(HttpContext ctx, string file, string contentType)
        {
            var bytes = await GatewayDashboardResources.GetAsync(file, ctx.RequestAborted);
            if (bytes is null) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.ContentType = contentType;
            await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
        }
    }
}


