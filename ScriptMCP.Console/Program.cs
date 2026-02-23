using ScriptMCP.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

McpConstants.ResolveSavePath();

// ── CLI mode: --exec <functionName> [argsJson] ──────────────────────────────
// Executes a single dynamic function and exits without starting the MCP server.
var execIndex = Array.IndexOf(args, "--exec");
if (execIndex >= 0 && execIndex + 1 < args.Length)
{
    var functionName = args[execIndex + 1];
    var argsJson = (execIndex + 2 < args.Length) ? args[execIndex + 2] : "{}";

    try
    {
        var tools = new DynamicTools();
        var result = tools.CallDynamicFunction(functionName, argsJson);
        Console.Write(result);
    }
    catch (Exception ex)
    {
        Console.Error.Write(ex.ToString());
        Environment.ExitCode = 1;
    }
    return;
}

// ── MCP server mode (default) ────────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddSingleton<DynamicTools>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "scriptmcp-console", Version = "1.0.0" };
        options.ServerInstructions = McpConstants.Instructions;
    })
    .WithStdioServerTransport()
    .WithTools<DynamicTools>();

await builder.Build().RunAsync();
