using System.Globalization;
using System.Text;
using System.Text.Json;
using ScriptMCP.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var supportedOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "--db",
    "--exec",
    "--exec-stream",
    "--exec-out",
    "--exec-out-append",
    "--exec-out-rewrite",
    "--path",
    "--telegram"
};

for (int i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (!arg.StartsWith("--", StringComparison.Ordinal))
        continue;

    var optionName = arg;
    if (arg.StartsWith("--db=", StringComparison.OrdinalIgnoreCase))
        optionName = "--db";
    else if (arg.StartsWith("--path=", StringComparison.OrdinalIgnoreCase))
        optionName = "--path";
    else if (arg.StartsWith("--telegram=", StringComparison.OrdinalIgnoreCase))
        optionName = "--telegram";
    else if (!supportedOptions.Contains(arg))
    {
        Console.Error.WriteLine($"Error: unsupported argument '{arg}'. Supported arguments: --db, --exec, --exec-stream, --exec-out, --exec-out-append, --exec-out-rewrite, --path, --telegram.");
        Environment.ExitCode = 1;
        return;
    }

    // Options that take a value via separate arg (--db <val>, --path <val>)
    // --telegram is special: value is optional (next arg may be a path or absent)
    if (string.Equals(optionName, "--db", StringComparison.OrdinalIgnoreCase) &&
        !arg.StartsWith("--db=", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Error: --db requires a path value.");
            Environment.ExitCode = 1;
            return;
        }
        i++;
    }
    else if (string.Equals(optionName, "--path", StringComparison.OrdinalIgnoreCase) &&
             !arg.StartsWith("--path=", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Error: --path requires a path value.");
            Environment.ExitCode = 1;
            return;
        }
        i++;
    }
    else if (string.Equals(optionName, "--telegram", StringComparison.OrdinalIgnoreCase) &&
             !arg.StartsWith("--telegram=", StringComparison.OrdinalIgnoreCase))
    {
        // Optional value: skip next arg if it looks like a path (not a --flag)
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            i++;
    }
}

McpConstants.ResolveSavePath(args);
Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
var fileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// ── Helper: strip [Output Instructions] suffix ──────────────────────────────
static string StripOutputInstructions(string output)
{
    var idx = output.IndexOf("[Output Instructions]:", StringComparison.Ordinal);
    return idx >= 0 ? output[..idx].TrimEnd() : output;
}

// ── Helper: resolve a --key value from args (supports --key val and --key=val)
static string? ResolveOptionalArg(string[] args, string key)
{
    for (int i = 0; i < args.Length; i++)
    {
        var prefix = key + "=";
        if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return args[i][prefix.Length..];

        if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase) &&
            i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            return args[i + 1];
    }
    return null;
}

// ── Helper: check if a flag is present (without value) ──────────────────────
static bool HasFlag(string[] args, string flag)
{
    for (int i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            return true;
        if (args[i].StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
            return true;
    }
    return false;
}

// ── Helper: resolve output file path based on mode and optional --path ──────
static string ResolveOutputPath(string functionName, string mode, string? customPath)
{
    if (customPath != null)
    {
        var dir = Path.GetDirectoryName(customPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (mode == "--exec-out")
        {
            var nameWithoutExt = Path.GetFileNameWithoutExtension(customPath);
            var ext = Path.GetExtension(customPath);
            var timestamp = DateTime.UtcNow.ToString("yyMMdd_HHmmss", CultureInfo.InvariantCulture);
            return Path.Combine(dir ?? ".", $"{nameWithoutExt}_{timestamp}{ext}");
        }

        return customPath;
    }

    if (mode == "--exec-out")
        return ScriptTools.GetScheduledTaskOutputPath(functionName);

    return ScriptTools.GetScheduledTaskAppendOutputPath(functionName);
}

// ── Helper: send message to Telegram ────────────────────────────────────────
static void SendTelegram(string text, string[] args)
{
    string? configPath = ResolveOptionalArg(args, "--telegram");

    // If --telegram was given with no value, use default path beside the database
    if (configPath == null)
        configPath = Path.Combine(Path.GetDirectoryName(ScriptTools.SavePath) ?? ".", "telegram.json");

    if (!File.Exists(configPath))
    {
        Console.Error.WriteLine($"Telegram: config not found at {configPath}");
        return;
    }

    try
    {
        var json = File.ReadAllText(configPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var botToken = root.TryGetProperty("botToken", out var bt) ? bt.GetString() : null;
        var chatId = root.TryGetProperty("chatId", out var ci) ? ci.GetString() : null;

        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
        {
            Console.Error.WriteLine("Telegram: telegram.json must contain botToken and chatId.");
            return;
        }

        var url = $"https://api.telegram.org/bot{botToken}/sendMessage";
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        // Telegram messages have a 4096 character limit; split if needed
        var chunks = SplitMessage(text, 4096);
        foreach (var chunk in chunks)
        {
            var payload = JsonSerializer.Serialize(new { chat_id = chatId, text = chunk });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = client.PostAsync(url, content).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Console.Error.WriteLine($"Telegram: API returned {(int)response.StatusCode} — {body}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Telegram: {ex.Message}");
    }
}

static List<string> SplitMessage(string text, int maxLength)
{
    var chunks = new List<string>();
    for (int i = 0; i < text.Length; i += maxLength)
        chunks.Add(text.Substring(i, Math.Min(maxLength, text.Length - i)));
    if (chunks.Count == 0) chunks.Add(text);
    return chunks;
}

// ── Validate --path usage ───────────────────────────────────────────────────
var customPath = ResolveOptionalArg(args, "--path");
bool hasExecOut = HasFlag(args, "--exec-out") || HasFlag(args, "--exec-out-append") || HasFlag(args, "--exec-out-rewrite");
bool hasAnyExec = HasFlag(args, "--exec") || hasExecOut;
bool hasTelegram = HasFlag(args, "--telegram");

if (customPath != null && !hasExecOut)
{
    Console.Error.WriteLine("Error: --path requires one of --exec-out, --exec-out-append, or --exec-out-rewrite.");
    Environment.ExitCode = 1;
    return;
}

if (hasTelegram && !hasAnyExec)
{
    Console.Error.WriteLine("Error: --telegram requires one of --exec, --exec-out, --exec-out-append, or --exec-out-rewrite.");
    Environment.ExitCode = 1;
    return;
}

// ── CLI mode: --exec-stream <functionName> [argsJson] ───────────────────────
// Executes a script with output streamed directly to stdout (no buffering).
var execStreamIndex = Array.FindIndex(args, a => string.Equals(a, "--exec-stream", StringComparison.OrdinalIgnoreCase));
if (execStreamIndex >= 0 && execStreamIndex + 1 < args.Length)
{
    var functionName = args[execStreamIndex + 1];
    var argsJson = (execStreamIndex + 2 < args.Length && !args[execStreamIndex + 2].StartsWith("--")) ? args[execStreamIndex + 2] : "{}";
    var tools = new ScriptTools();
    tools.CallScriptStreaming(functionName, argsJson);
    return;
}

// ── CLI mode: --exec <functionName> [argsJson] ──────────────────────────────
// Executes a single script and exits without starting the MCP server.
var execIndex = Array.IndexOf(args, "--exec");
var execOutIndex = Array.IndexOf(args, "--exec-out");
var execOutAppendIndex = Array.IndexOf(args, "--exec-out-append");
var execOutRewriteIndex = Array.IndexOf(args, "--exec-out-rewrite");

if (execOutRewriteIndex >= 0 && execOutRewriteIndex + 1 < args.Length)
{
    var functionName = args[execOutRewriteIndex + 1];
    var argsJson = (execOutRewriteIndex + 2 < args.Length && !args[execOutRewriteIndex + 2].StartsWith("--")) ? args[execOutRewriteIndex + 2] : "{}";

    try
    {
        var tools = new ScriptTools();
        var result = tools.CallScript(functionName, argsJson);
        Console.Write(result);

        var cleanResult = StripOutputInstructions(result);
        var outputPath = ResolveOutputPath(functionName, "--exec-out-rewrite", customPath);
        File.WriteAllText(outputPath, cleanResult, fileEncoding);

        if (hasTelegram) SendTelegram(cleanResult, args);
    }
    catch (Exception ex)
    {
        Console.Error.Write(ex.ToString());
        Environment.ExitCode = 1;
    }
    return;
}

if (execOutAppendIndex >= 0 && execOutAppendIndex + 1 < args.Length)
{
    var functionName = args[execOutAppendIndex + 1];
    var argsJson = (execOutAppendIndex + 2 < args.Length && !args[execOutAppendIndex + 2].StartsWith("--")) ? args[execOutAppendIndex + 2] : "{}";

    try
    {
        var tools = new ScriptTools();
        var result = tools.CallScript(functionName, argsJson);
        Console.Write(result);

        var cleanResult = StripOutputInstructions(result);
        var outputPath = ResolveOutputPath(functionName, "--exec-out-append", customPath);
        var text = cleanResult + Environment.NewLine;
        File.AppendAllText(outputPath, text, fileEncoding);

        if (hasTelegram) SendTelegram(cleanResult, args);
    }
    catch (Exception ex)
    {
        Console.Error.Write(ex.ToString());
        Environment.ExitCode = 1;
    }
    return;
}

if (execOutIndex >= 0 && execOutIndex + 1 < args.Length)
{
    var functionName = args[execOutIndex + 1];
    var argsJson = (execOutIndex + 2 < args.Length && !args[execOutIndex + 2].StartsWith("--")) ? args[execOutIndex + 2] : "{}";

    try
    {
        var tools = new ScriptTools();
        var result = tools.CallScript(functionName, argsJson);
        Console.Write(result);

        var cleanResult = StripOutputInstructions(result);
        var outputPath = ResolveOutputPath(functionName, "--exec-out", customPath);
        File.WriteAllText(outputPath, cleanResult, fileEncoding);

        if (hasTelegram) SendTelegram(cleanResult, args);
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
    var argsJson = (execIndex + 2 < args.Length && !args[execIndex + 2].StartsWith("--")) ? args[execIndex + 2] : "{}";

    try
    {
        var tools = new ScriptTools();
        var result = tools.CallScript(functionName, argsJson);
        Console.Write(result);

        if (hasTelegram)
        {
            var cleanResult = StripOutputInstructions(result);
            SendTelegram(cleanResult, args);
        }
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
builder.Services.AddSingleton<ScriptTools>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "scriptmcp-console", Version = "1.0.0" };
        options.ServerInstructions = McpConstants.Instructions;
    })
    .WithStdioServerTransport()
    .WithTools<ScriptTools>()
    .WithResources<ScriptResources>();

await builder.Build().RunAsync();
