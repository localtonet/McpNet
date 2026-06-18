# ─────────────────────────────────────────────────────────────────────────────
# McpNet Gateway — multi-stage container image
# Build:  docker build -t mcpnet-gateway .
# Run:    docker run -p 5050:5050 mcpnet-gateway
# ─────────────────────────────────────────────────────────────────────────────

# ── Stage 1: build & publish ─────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution + project files first for layer-cached restore
COPY NETMCP.slnx ./
COPY src/ ./src/
COPY tests/ ./tests/

RUN dotnet restore src/McpNet.Host/McpNet.Host.csproj
RUN dotnet publish src/McpNet.Host/McpNet.Host.csproj \
    -c Release \
    -o /app/publish \
    --no-self-contained \
    /p:UseAppHost=false

# ── Stage 2: runtime ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Non-root user
RUN groupadd -r mcpnet && useradd -r -g mcpnet mcpnet
COPY --from=build /app/publish ./
RUN mkdir -p /app/mcp-data && chown -R mcpnet:mcpnet /app
USER mcpnet

ENV ASPNETCORE_URLS=http://+:5050 \
    McpGateway__Port=5050 \
    McpGateway__DataDirectory=/app/mcp-data

EXPOSE 5050

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["sh", "-c", "wget -qO- http://localhost:5050/health || exit 1"]

ENTRYPOINT ["dotnet", "mcpnet-gateway.dll"]
