using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using ModelContextProtocol.AspNetCore;

namespace dnSpy.Extension.MalwareMCP;

public sealed class McpServerHost
{
    private WebApplication? _app;
    private readonly DnSpyBridge _bridge;
    private readonly int _port;

    public McpServerHost(DnSpyBridge bridge, int port = 0)
    {
        _bridge = bridge;
        _port = port > 0 ? port
            : int.TryParse(Environment.GetEnvironmentVariable("DNSPY_MCP_PORT"), out var p) ? p
            : 8765;
    }

    public async Task StartAsync()
    {
        try
        {
            Diagnostics.Log($"[Host] StartAsync beginning (port={_port})");
            var builder = WebApplication.CreateBuilder();
            Diagnostics.Log("[Host] WebApplication.CreateBuilder OK");

            builder.Services.AddSingleton(_bridge);
            builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(_port));

            // Forward ASP.NET Core / MCP SDK logs to our file so we can diagnose request failures.
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new FileLoggerProvider());
            builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);

            builder.Services
                .AddMcpServer()
                .WithHttpTransport(o => o.Stateless = true)
                .WithToolsFromAssembly(typeof(McpServerHost).Assembly);
            Diagnostics.Log("[Host] MCP server services registered");

            _app = builder.Build();
            Diagnostics.Log("[Host] WebApplication built");

            // Diagnostic: simple endpoints to confirm Kestrel plumbing works inside dnSpy
            _app.MapGet("/ping", () => { Diagnostics.Log("[Host] /ping hit"); return "pong"; });
            _app.MapGet("/health", () =>
            {
                Diagnostics.Log("[Host] /health hit");
                return Microsoft.AspNetCore.Http.Results.Ok(new { status = "ok", tool_count = typeof(McpServerHost).Assembly.GetTypes().Length });
            });

            _app.MapMcp("/mcp");
            Diagnostics.Log("[Host] MapMcp done");

            await _app.StartAsync();
            Diagnostics.Log($"[Host] MCP server listening on 0.0.0.0:{_port}");
            _bridge.Log($"[MalwareMCP] MCP server listening on 0.0.0.0:{_port}");
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"[Host] FAILED: {ex}");
            _bridge.Log($"[MalwareMCP] Failed to start MCP server: {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        if (_app != null)
            await _app.StopAsync();
    }
}
