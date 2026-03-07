using System.Text;
using ScriptMCP.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

McpConstants.ResolveSavePath();
Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
var fileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// ── Helper: strip [Output Instructions] suffix ──────────────────────────────
static string StripOutputInstructions(string output)
{
    var idx = output.IndexOf("[Output Instructions]:", StringComparison.Ordinal);
    return idx >= 0 ? output[..idx].TrimEnd() : output;
}

// ── Helper: write scheduled-task output to either a new file or an append file
void WriteScheduledTaskOutput(string functionName, string result, bool append)
{
    if (append)
    {
        var appendPath = DynamicTools.GetScheduledTaskAppendOutputPath(functionName);
        var text = result + Environment.NewLine;
        File.AppendAllText(appendPath, text, fileEncoding);
        return;
    }

    var timestampedPath = DynamicTools.GetScheduledTaskOutputPath(functionName);
    File.WriteAllText(timestampedPath, result, fileEncoding);
}

// ── CLI mode: --exec <functionName> [argsJson] ──────────────────────────────
// Executes a single dynamic function and exits without starting the MCP server.
var execIndex = Array.IndexOf(args, "--exec");
var execOutIndex = Array.IndexOf(args, "--exec-out");
if (execOutIndex < 0)
    execOutIndex = Array.IndexOf(args, "--exec_out"); // backward compatibility
var appendIndex = Array.IndexOf(args, "--append");

if (execOutIndex >= 0 && execOutIndex + 1 < args.Length)
{
    // --exec-out: execute function, write to stdout and persist scheduled-task output
    var functionName = args[execOutIndex + 1];
    var argsJson = (execOutIndex + 2 < args.Length) ? args[execOutIndex + 2] : "{}";
    var append = appendIndex >= 0;

    try
    {
        var tools = new DynamicTools();
        var result = tools.CallDynamicFunction(functionName, argsJson);
        Console.Write(result);

        var cleanResult = StripOutputInstructions(result);
        WriteScheduledTaskOutput(functionName, cleanResult, append);
    }
    catch (Exception ex)
    {
        Console.Error.Write(ex.ToString());
        Environment.ExitCode = 1;
    }
    return;
}

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
    .WithTools<DynamicTools>()
    .WithResources<DynamicResources>();

await builder.Build().RunAsync();
