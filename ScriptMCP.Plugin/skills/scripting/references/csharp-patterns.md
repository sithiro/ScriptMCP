# C# Code Patterns for ScriptMCP Scripts

## Environment

- **.NET 9 / C# 13** runtime
- Write top-level C# source like a `Program.cs` file
- Output through `Console.Write` / `Console.WriteLine`
- Support both inferred top-level statements and explicit `Program.Main(string[] args)`
- The original JSON input payload is passed as `args[0]`
- Declared parameters are auto-parsed from that JSON and exposed as typed names
- `scriptArgs` remains available as a compatibility dictionary parsed from the same `args[0]` JSON
- NOT async-friendly by default — use `.Result` or `.GetAwaiter().GetResult()`
- All `System.*` assemblies available; no NuGet packages
- `#r "path.dll"` to reference external .NET assemblies
- `#load "path.csx"` to include C# source files (code must be in classes/structs)
- Prefer `using (var x = ...) { ... }` over `using var x = ...;`

## Authoring Model

ScriptMCP supports full top-level C# scripting. Treat a code script like a single-file console app:

- add normal `using` directives at the top when needed
- write top-level statements directly
- define local functions, classes, enums, records, and helper types in the same file
- use classic `Program.Main(string[] args)` if that shape is clearer for the script

### Minimal Example

```csharp
using System;

Console.WriteLine("Hello, world!");
```

### Program Class and Main Method Example

```csharp
using System;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Hello from Main.");
    }
}
```

### Reading JSON From `args[0]`

```csharp
using System.Text.Json;

var doc = JsonDocument.Parse(args[0]);
var city = doc.RootElement.TryGetProperty("city", out var value)
    ? value.GetString()
    : "Athens";

Console.WriteLine(city);
```

### Using Typed Parameters

If the script declares a `city` parameter, ScriptMCP auto-parses it from `args[0]` and exposes it as a typed local:

```csharp
using System;

Console.WriteLine(city);
```

### Optional Compatibility Dictionary

Older scripts may still use `scriptArgs`. It is just the same JSON payload parsed into a dictionary:

```csharp
using System;

Console.WriteLine(scriptArgs["city"]);
```

## Auto-Included Usings

```
System, System.Collections.Generic, System.Globalization, System.IO,
System.Linq, System.Net, System.Net.Http, System.Text,
System.Text.RegularExpressions, System.Threading.Tasks
```

If you need anything else, add normal `using` directives at the top of the script source, like a regular `Program.cs` file.

## HTTP Patterns

### Simple GET

```csharp
var client = new HttpClient();
client.DefaultRequestHeaders.Add("User-Agent", "ScriptMCP");
Console.Write(client.GetStringAsync(url).Result);
```

### GET with Headers

```csharp
var client = new HttpClient();
client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
client.DefaultRequestHeaders.Add("Accept", "application/json");
var response = client.GetStringAsync(url).Result;
Console.Write(response);
```

### POST with JSON Body

```csharp
var client = new HttpClient();
var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
var response = client.PostAsync(url, content).Result;
Console.Write(response.Content.ReadAsStringAsync().Result);
```

### Download File

```csharp
var client = new HttpClient();
var bytes = client.GetByteArrayAsync(url).Result;
File.WriteAllBytes(outputPath, bytes);
Console.Write($"Downloaded {bytes.Length} bytes to {outputPath}");
```

## JSON Patterns

### Parse and Extract

```csharp
var doc = System.Text.Json.JsonDocument.Parse(args[0]);
var root = doc.RootElement;
var name = root.GetProperty("name").GetString();
var count = root.GetProperty("count").GetInt32();
Console.Write($"Name: {name}, Count: {count}");
```

### Disposable Resource Pattern

```csharp
using (var doc = System.Text.Json.JsonDocument.Parse(args[0]))
{
    var root = doc.RootElement;
    Console.Write(root.GetProperty("name").GetString());
}
```

### Build JSON

```csharp
using System.Text.Json;
var options = new JsonSerializerOptions { WriteIndented = true };
var obj = new Dictionary<string, object> {
    ["name"] = name,
    ["value"] = int.Parse(value)
};
Console.Write(JsonSerializer.Serialize(obj, options));
```

### Iterate JSON Array

```csharp
var doc = System.Text.Json.JsonDocument.Parse(jsonString);
var sb = new StringBuilder();
foreach (var item in doc.RootElement.EnumerateArray())
{
    sb.AppendLine(item.GetProperty("name").GetString());
}
Console.Write(sb.ToString());
```

## File I/O Patterns

### Read and Process Lines

```csharp
var lines = File.ReadAllLines(path);
var filtered = lines.Where(l => l.Contains(keyword)).ToArray();
Console.Write($"Found {filtered.Length} matching lines:\n{string.Join("\n", filtered)}");
```

### Write Output

```csharp
var result = ProcessData(input);
File.WriteAllText(outputPath, result);
Console.Write($"Written to {outputPath}");
```

### CSV Processing

```csharp
var lines = File.ReadAllLines(path);
var header = lines[0].Split(',');
var sb = new StringBuilder();
sb.AppendLine(string.Join(" | ", header));
sb.AppendLine(new string('-', 40));
foreach (var line in lines.Skip(1))
{
    sb.AppendLine(string.Join(" | ", line.Split(',')));
}
Console.Write(sb.ToString());
```

## Process Execution Patterns

### Run Shell Command

```csharp
var psi = new System.Diagnostics.ProcessStartInfo {
    FileName = "cmd",
    Arguments = $"/c {command}",
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true
};
var proc = System.Diagnostics.Process.Start(psi);
var stdout = proc.StandardOutput.ReadToEnd();
var stderr = proc.StandardError.ReadToEnd();
proc.WaitForExit();
if (proc.ExitCode != 0) Console.Write($"Error (exit {proc.ExitCode}): {stderr}");
else Console.Write(stdout);
```

### Run PowerShell

```csharp
var psi = new System.Diagnostics.ProcessStartInfo {
    FileName = "powershell",
    Arguments = $"-NoProfile -Command \"{script}\"",
    RedirectStandardOutput = true,
    UseShellExecute = false,
    CreateNoWindow = true
};
var proc = System.Diagnostics.Process.Start(psi);
var output = proc.StandardOutput.ReadToEnd();
proc.WaitForExit();
Console.Write(output);
```

## String and Text Patterns

### Regex Extraction

```csharp
var matches = Regex.Matches(input, pattern);
var sb = new StringBuilder();
foreach (Match m in matches)
{
    sb.AppendLine(m.Value);
}
Console.Write(sb.ToString());
```

### String Building with Formatting

```csharp
var sb = new StringBuilder();
sb.AppendLine($"| {"Name",-20} | {"Value",-15} |");
sb.AppendLine($"|{new string('-', 22)}|{new string('-', 17)}|");
foreach (var item in items)
{
    sb.AppendLine($"| {item.Key,-20} | {item.Value,-15} |");
}
Console.Write(sb.ToString());
```

## Date and Time Patterns

```csharp
var now = DateTime.Now;
var utc = DateTime.UtcNow;
Console.Write($"Local: {now:yyyy-MM-dd HH:mm:ss}\nUTC: {utc:yyyy-MM-dd HH:mm:ss}");
```

## Directives

### Reference an External DLL (`#r`)

```csharp
#r "C:/libs/MyLibrary.dll"

var result = MyNamespace.MyClass.DoWork();
Console.Write(result);
```

### Include a Helper File (`#load`)

**helpers/format.csx:**
```csharp
public static class Fmt
{
    public static string Box(string text)
    {
        var b = new string('*', text.Length + 4);
        return $"{b}\n* {text} *\n{b}";
    }
}
```

**Script:**
```csharp
#load "C:/helpers/format.csx"

Console.Write(Fmt.Box("Hello!"));
```

### Combining `#r` and `#load`

```csharp
#r "C:/libs/DemoLib.dll"
#load "C:/helpers/format.csx"

var greeting = DemoLib.MathHelper.Greet("World");
Console.Write(Fmt.Box(greeting));
```

Directives must appear at the top of the script before any code. `#r "nuget: ..."` is not supported. Loaded files must wrap code in classes/structs (no bare top-level statements).

## Inter-Script Calling

### Synchronous Call

```csharp
// Call another script and use its result
var result = ScriptMCP.Call("other_function", "{\"param\": \"value\"}");
Console.Write($"Other script returned: {result}");
```

### Parallel Execution

```csharp
// Launch multiple scripts in parallel
var proc1 = ScriptMCP.Proc("func_a", "{}");
var proc2 = ScriptMCP.Proc("func_b", "{}");
var output1 = proc1.StandardOutput.ReadToEnd();
var output2 = proc2.StandardOutput.ReadToEnd();
proc1.WaitForExit();
proc2.WaitForExit();
Console.Write($"A: {output1}\nB: {output2}");
```

## Scheduling & Shared Output

### Create a Scheduled Task (Native Tool)

Call `create_scheduled_task` directly — it is a native MCP tool, not a script:

- `function_name`: name of the script to run
- `function_args`: JSON arguments (default `"{}"`)
- `interval_minutes`: recurrence interval
- `append`: when true, append to `<function>.txt` instead of creating a new timestamped file per run
- `rewrite`: when true, overwrite `<function>.txt` each run (takes precedence over append)
- `telegram`: set to `"true"` to send output to Telegram using default `telegram.json`, or provide a custom path

The task runs via `--exec-out`, which by default writes each result to a timestamped file in `output`. With append mode it uses `--exec-out-append` and appends to `<function>.txt`. With rewrite mode it uses `--exec-out-rewrite` and overwrites `<function>.txt` each run. With telegram enabled, output is also sent to the configured Telegram channel.

Call `delete_scheduled_task` directly to remove a scheduled task:

- `function_name`: required
- `interval_minutes`: interval used when the task was created (default `1`)

On Windows this deletes `ScriptMCP\<function> (<interval>m)` via `schtasks`. On Linux/macOS it removes the cron entry tagged `# ScriptMCP:<function_name>`.

Call `list_scheduled_tasks` directly to list ScriptMCP-managed scheduled tasks:

- no parameters

On Windows this lists tasks under `\ScriptMCP\`. On Linux/macOS it lists cron entries tagged `# ScriptMCP:`.

Call `start_scheduled_task` directly to enable and start a task:

- `function_name`: required
- `interval_minutes`: interval used when the task was created (default `1`)

Call `stop_scheduled_task` directly to disable a task:

- `function_name`: required
- `interval_minutes`: interval used when the task was created (default `1`)

### Read Exec Output (Native Tool)

Call `read_scheduled_task` directly to read the result written for a script by scheduled tasks or `--exec-out` / `--exec-out-append` / `--exec-out-rewrite`:

- `function_name`: required, returns `<function>.txt` if append mode is active; otherwise the latest matching timestamped file

### Terminal Display (Token-Free Output)

`call_process` supports a `terminal` parameter that sends output directly to a visible Windows Terminal window or tab — **the agent never sees the data**. This is a major token saver for any script that produces tables, reports, or market data the user wants to view but the agent does not need to process.

| `terminal` value | Behavior | Use when user says |
|---|---|---|
| `"new_window"` | New WT window for every call | "in a new window" |
| `"named_window"` | One named WT window, subsequent calls add tabs | "in the scriptmcp window" |
| `"new_tab"` | New tab in the current agent WT window | "in a new tab" |
| _(empty)_ | Headless — output captured and returned to agent | _(default)_ |

**Show a watchlist in a new window (agent sees no data):**

```
call_process(name="watchlist_show", arguments={"name":"tech"}, terminal="new_window")
```

**Show correlation matrix in agent's own tab:**

```
call_process(name="watchlist_correlation_matrix", arguments={"showMatrix":true}, terminal="new_tab")
```

**Open three symbols in parallel tabs:**

```
call_process(name="watchlist_show", arguments={"symbols":"AMD"}, terminal="named_window")
call_process(name="watchlist_show", arguments={"symbols":"TSLA"}, terminal="named_window")   // parallel
call_process(name="watchlist_show", arguments={"symbols":"ABTC"}, terminal="named_window")   // parallel
```

When `terminal` is set, `call_process` returns immediately with no output. Do not wait for or relay any result.

### Telegram Notifications

The CLI supports `--telegram [filepath]` to send script output to a Telegram channel alongside any `--exec*` mode. If no filepath is given, it looks for `telegram.json` beside the active database. The file must contain `botToken` and `chatId`. Telegram failures are non-fatal (warning to stderr).

For one-off execution with Telegram delivery, use `call_process` with the `telegram` parameter set to `"true"` (default `telegram.json`) or a custom path. This applies whenever the user wants a script's output sent to Telegram outside of scheduled tasks.

### Writing Scripts for Scheduled Use

Scripts intended for scheduled execution should be self-contained and write meaningful output, since stdout is captured to the output file:
Scripts intended for scheduled execution should be self-contained and write meaningful output, since stdout is what gets captured to the output file:

```csharp
// Good: writes a meaningful result
var client = new HttpClient();
var price = client.GetStringAsync("https://api.example.com/price").Result;
Console.Write($"Price: {price}");
```

## Error Handling

### Standard Pattern

```csharp
try {
    // risky operation
    var result = DoSomething();
    Console.Write(result);
} catch (HttpRequestException ex) {
    Console.Write($"HTTP Error: {ex.Message}");
} catch (FileNotFoundException ex) {
    Console.Write($"File not found: {ex.FileName}");
} catch (Exception ex) {
    Console.Write($"Error: {ex.GetType().Name}: {ex.Message}");
}
```

### Timeout Pattern

```csharp
try {
    var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
    var client = new HttpClient();
    var response = client.GetStringAsync(url, cts.Token).Result;
    Console.Write(response);
} catch (TaskCanceledException) {
    Console.Write("Error: Request timed out after 30 seconds");
}
```

## Parameter Type Examples

### Registration with Typed Parameters

```json
[
  {"name": "url", "type": "string", "description": "Target URL"},
  {"name": "count", "type": "int", "description": "Number of retries"},
  {"name": "timeout", "type": "double", "description": "Timeout in seconds"},
  {"name": "verbose", "type": "bool", "description": "Enable verbose output"}
]
```

Parameters are auto-parsed and available as local variables matching their declared types.

## Full Single-File Example

```csharp
using System;
using System.Text.Json;

enum OutputMode
{
    NumberOnly,
    Sentence
}

static long Fibonacci(int value)
{
    if (value <= 1)
        return value;

    long a = 0;
    long b = 1;

    for (var i = 2; i <= value; i++)
    {
        var next = a + b;
        a = b;
        b = next;
    }

    return b;
}

var doc = JsonDocument.Parse(args[0]);
var n = doc.RootElement.TryGetProperty("n", out var nValue) ? nValue.GetInt32() : 10;
var mode = doc.RootElement.TryGetProperty("mode", out var modeValue) &&
           string.Equals(modeValue.GetString(), "sentence", StringComparison.OrdinalIgnoreCase)
    ? OutputMode.Sentence
    : OutputMode.NumberOnly;

var result = Fibonacci(n);

if (mode == OutputMode.Sentence)
    Console.WriteLine($"Fibonacci({n}) = {result}");
else
    Console.WriteLine(result);
```
