using System.Net;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Gateway.Dashboard;

namespace McpNet.Gateway.Standalone.Dashboard
{
    internal sealed class DashboardHandler
    {
        public async Task ServeAsync(HttpListenerContext ctx, string file, CancellationToken ct)
        {
            var bytes = await GatewayDashboardResources.GetAsync(file, ct).ConfigureAwait(false);
            if (bytes is null)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            ctx.Response.ContentType     = GetContentType(file);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            ctx.Response.OutputStream.Close();
        }

        private static string GetContentType(string file)
        {
            if (file.EndsWith(".html", System.StringComparison.OrdinalIgnoreCase)) return "text/html; charset=utf-8";
            if (file.EndsWith(".js",   System.StringComparison.OrdinalIgnoreCase)) return "text/javascript; charset=utf-8";
            if (file.EndsWith(".css",  System.StringComparison.OrdinalIgnoreCase)) return "text/css; charset=utf-8";
            if (file.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase)) return "application/json; charset=utf-8";
            return "application/octet-stream";
        }
    }
}
