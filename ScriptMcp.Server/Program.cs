using ModelContextProtocol.Server;
using ScriptMCP.Library;

// ── Dynamic-functions path ────────────────────────────────────────────────────
McpConstants.ResolveSavePath();

// ── ASP.NET Core host ─────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// Suppress framework noise
builder.Logging.ClearProviders();

// Listen on the designated address
builder.WebHost.UseUrls("http://localhost:8080");

// Register DynamicTools as a singleton so state is preserved across requests
builder.Services.AddSingleton<DynamicTools>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "scriptmcp-server", Version = "1.0.0" };
        options.ServerInstructions = McpConstants.Instructions;
    })
    .WithHttpTransport()
    .WithTools<DynamicTools>();

var app = builder.Build();

app.MapMcp("/");

Console.WriteLine("MCP Server running on http://localhost:8080/");
Console.WriteLine("Press Ctrl+C to stop.");

app.Run();
