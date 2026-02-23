using ScriptMCP.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

McpConstants.ResolveSavePath();

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
