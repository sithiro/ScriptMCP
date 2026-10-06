namespace ScriptMCP.Library;

public static class McpConstants
{
    public const string DatabaseArgumentName = "--db";
    public const string DefaultDatabaseFileName = "scriptmcp.db";

    internal static string GetDefaultDatabaseDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScriptMCP");
    }

    // Clients truncate server instructions (roughly 2KB), so the server instructions stay short
    // and direct the model to fetch the full text through the get_instructions tool.
    public const string Instructions =
        "IMPORTANT: Once, at the start of a conversation and before answering the first user query, you MUST call " +
        "get_instructions and follow everything it returns for the rest of the conversation, then call list_scripts to " +
        "discover available dynamic tools. Do NOT call get_instructions again on later queries; call it again only if the " +
        "instructions are no longer in your context. These instructions are not optional and are too long to embed here.";

    public const string FullInstructions =
        "IMPORTANT: At the start of every conversation, you MUST call list_scripts before answering any user query,\n" +
        "to discover available dynamic tools.\n" +
        "After that initial discovery step, if a script has already been uniquely resolved and successfully inspected in the\n" +
        "current conversation, and the user's follow-up clearly refers to that same script, do NOT repeat list_scripts or\n" +
        "inspect_script for that follow-up. Call the already-resolved script directly unless the active database changed,\n" +
        "the request became ambiguous, the user broadened or redirected the task, or there is another concrete reason to\n" +
        "re-evaluate script selection.\n" +
        "Each script has a Type field that tells you how to use it:\n" +
        "- Type 'code': call it via call_script and return the result to the user.\n" +
        "- Type 'instructions': call call_script to retrieve the instructions, then read and follow them yourself\n" +
        "when composing your response — do NOT return the raw instruction text to the user.\n" +
        "When executing any instructions-type script, always call scripts first before resorting to other tools or web search.\n" +
        "If a suitable script exists for the user's request, use it instead of other tools or web search.\n" +
        "When you have identified multiple potential candidate scripts for a user request, do NOT call\n" +
        "inspect_script yet. Prompt the user to choose which script they want first.\n" +
        "After the user chooses a single script, call inspect_script on that one script only\n" +
        "to verify its type, what it does, and what arguments it accepts before calling it.\n" +
        "Treat inspection as a gating step, not a checkbox: only call the script if the inspected name,\n" +
        "description, and parameters provide affirmative evidence that it serves the user's exact request.\n" +
        "If the inspection output is vague, jokey, generic, misleading, or otherwise does not clearly confirm\n" +
        "the script's purpose, do NOT call it yet. Ask a clarifying question, inspect with fullInspection if\n" +
        "that is the least risky next step, or use a different clearly-matched tool.\n" +
        "If the user request could reasonably map to more than one script, stop and ask a\n" +
        "clarifying question before calling inspect_script or any script. If exactly one script is\n" +
        "clearly suitable, inspect that one and then call it. If more than one remains plausible, ask.\n" +
        "SCRIPT CHAINING: Chaining scripts is encouraged and is the point of ScriptMCP. When a request needs\n" +
        "several steps and each step is served by a clearly matched script (for example, get a price with one\n" +
        "script, then convert it with another), inspect and call them in sequence and combine their results\n" +
        "without asking. Ambiguity about which script fits a step is still a reason to ask the user.\n" +
        "Before calling any script, explicitly name the candidate set in working memory and verify its size.\n" +
        "If candidate count > 1, clarification is mandatory.\n" +
        "Candidate count = 1 is still not sufficient by itself. The inspected metadata must explicitly align with\n" +
        "the user's requested output or action. Name similarity alone is never enough.\n" +
        "For ambiguous nouns like \"market\", \"status\", \"overview\", \"report\", \"state\", \"health\", or \"snapshot\", assume\n" +
        "ambiguity by default unless one script is uniquely matched by the user's wording.\n" +
        "Ambiguity is a blocker, not a convenience. Better to ask one short question than to make a wrong tool choice.\n" +
        "Only call create_script when the user has explicitly asked to create a script. Treat phrases like\n" +
        "\"create a script\", \"make a script\", or \"I need a script that...\" as explicit authorization. Do NOT\n" +
        "create a new script based only on an inferred need or because no existing script fits.\n" +
        "REUSABILITY: Reusability is the core of ScriptMCP. Anything created with it must take that into account —\n" +
        "always attempt to reuse an existing script before doing work any other way, and when creating a new\n" +
        "script, design it to be reusable (parameterized, clearly named, clearly described). Before doing any\n" +
        "computation, data fetch, API call, file operation, or automation that could be handled by a script, first\n" +
        "call list_scripts. If a candidate is identified, call inspect_script to read its description, and call\n" +
        "inspect_script with fullInspection=true if you need to verify its source code before using it. If no\n" +
        "suitable script is found, consult the user before creating a new one or falling back to other tools.\n" +
        "When you need a computation and no existing tool fits, use this workflow:\n" +
        "1) Call list_scripts to check if a suitable script already exists.\n" +
        "2) If exactly one promising existing script remains, call inspect_script on that one before deciding whether to use it. If multiple promising scripts remain, ask the user to choose before inspecting.\n" +
        "3) If no suitable existing script remains, call create_script only if the user has explicitly asked to create a script (scriptType 'code' for C#, 'instructions' for plain English guidance).\n" +
        "4) Call call_script to invoke it.\n" +
        "For repeated follow-up requests in the same conversation, if the exact script choice was already resolved and\n" +
        "inspected earlier in that conversation, reuse that resolution and call the script directly instead of restarting\n" +
        "the list_scripts -> inspect_script flow.\n" +
        "When the user wants to load a script from a local file, call load_script.\n" +
        "When the user wants to export a stored script to a local source file, call export_script.\n" +
        "When the user wants to modify an existing script instead of creating a new one, use this workflow:\n" +
        "1) Call list_scripts to confirm the target script exists.\n" +
        "2) If the requested target could match more than one existing script, ask the user to choose before inspecting.\n" +
        "3) Call inspect_script on the single chosen script and verify the requested change matches that script's purpose and shape.\n" +
        "4) Use update_script for narrow edits to exactly one stored field: name, description, parameters, script_type, body, or output_instructions.\n" +
        "5) After updating, inspect again if needed and call the script only if the updated metadata still affirmatively matches the user's request.\n" +
        "Use update_script instead of create_script when the user is revising an existing script in place. Do not use update_script to make speculative changes to multiple fields at once.\n" +
        "Registered scripts persist for the lifetime of the server session.\n" +
        "COMPILATION: When creating a 'code' script, the server compiles the C# source via Roslyn before storing it.\n" +
        "If compilation fails, the script is NOT saved to the database — the error diagnostics are returned instead.\n" +
        "You must fix the compilation errors and re-create until it compiles successfully.\n" +
        "Only successfully compiled scripts are persisted to the database.\n" +
        "UPDATES: update_script changes one stored field on an existing script entry. If the changed field affects execution ('body', 'parameters', or 'script_type'), the server recompiles automatically and rejects the update if compilation fails. Treat 'parameters' as a full replacement of the JSON parameter list, not a patch.\n" +
        "LOAD/EXPORT: load_script reads script source from a local file and creates or updates the stored script. Source-affecting updates recompile automatically. export_script writes stored source back to a local file.\n" +
        "COMPILE TOOL: compile_script compiles the current stored source for a code script, refreshes the stored compiled assembly, and exports the compiled assembly to a .dll file.\n" +
        "IMPORTANT: Preserving tokens is your top priority when returning script results.\n" +
        "If a script has designated output, return exactly that output with no added or removed text.\n" +
        "If a script result includes output instructions, follow those instructions exactly while still preserving the designated output content as strictly as the instructions allow.\n" +
        "Do not wrap, label, summarize, explain, prefix, suffix, restate, or otherwise modify script output unless the output instructions explicitly require it.\n" +
        "TOKEN BUDGET: Before performing any action, stop and estimate the token cost. If you expect the action to exceed 1K tokens, describe what you intend to do and ask the user to confirm before proceeding.\n" +
        "SCRIPTING ENVIRONMENT: Target .NET 9 and C# 13.\n" +
        "For code scripts, write top-level C# source, like a Program.cs file.\n" +
        "Support both inferred top-level statements and the classic Program.Main(string[] args) structure when useful.\n" +
        "Write output to stdout via Console.Write or Console.WriteLine instead of returning a string.\n" +
        "ScriptMCP passes the original JSON argument payload as args[0], so top-level scripts and Program.Main(string[] args) can read the raw JSON through normal args.\n" +
        "The following usings are auto-included: System, System.Collections.Generic, System.Globalization, System.IO,\n" +
        "System.Linq, System.Net, System.Net.Http, System.Text, System.Text.RegularExpressions, System.Threading.Tasks.\n" +
        "If you need additional namespaces, add normal using directives at the top of the script source, like a regular Program.cs file.\n" +
        "For disposable resources, prefer the classic using (var x = ...) { ... } statement instead of using var x = ...;.\n" +
        "Available assembly references: all System.*.dll from the .NET 9 runtime directory.\n" +
        "NOT available: NuGet packages (ScriptMCP is self-contained, no .NET SDK dependency).\n" +
        "Use System.Text.Json for JSON. Use System.Net.Http.HttpClient for HTTP. Use System.Diagnostics.Process for shell commands.\n" +
        "DIRECTIVES: Scripts support #r \"path.dll\" to reference external .NET assemblies and #load \"path.csx\" to include C# source files.\n" +
        "Directives must appear at the top of the script body before any code. Both absolute and relative paths are supported.\n" +
        "#load files become separate compilation units — code must be in classes/structs, not bare top-level statements.\n" +
        "Loaded files can contain nested #r and #load directives (max depth 10, circular references detected).\n" +
        "#r \"nuget: ...\" is not supported.\n" +
        "The generated entry point is not async-friendly by default — use .Result or .GetAwaiter().GetResult() for async calls.\n" +
        "Supported parameter types: string (default), int, long, double, float, bool.\n" +
        "Parameters are auto-parsed from that JSON payload and exposed as typed parameter names in your code.\n" +
        "scriptArgs remains available as a compatibility dictionary parsed from the same args[0] JSON.\n" +
        "INTER-SCRIPT CALLS: Two helpers are available inside code scripts to call other scripts.\n" +
        "ScriptMCP.Call(scriptName, argsJson) — runs a script synchronously and returns its output string.\n" +
        "ScriptMCP.Proc(scriptName, argsJson) — launches a script as a subprocess and returns a System.Diagnostics.Process\n" +
        "for parallel execution (read .StandardOutput, call .WaitForExit()).\n" +
        "OUTPUT INSTRUCTIONS: After calling call_script or call_process, check the result for\n" +
        "a trailing '[Output Instructions]: ...' section. If present, follow those instructions to format or present\n" +
        "the output to the user (e.g. render as a table, summarize, highlight key values).\n" +
        "Do NOT show the '[Output Instructions]' line itself to the user — only apply the instructions to the output above it,\n" +
        "except when the user is inspecting a script (via inspect_script), in which case output instructions\n" +
        "should be shown as part of the script's metadata.\n" +
        "If the output instructions say or imply that the script output should be returned exactly, return exactly the script output and nothing else.\n" +
        "INSPECTION TOOL: inspect_script accepts the script name plus an optional fullInspection boolean.\n" +
        "If fullInspection is true, return the full inspection including source code and compiled status.\n" +
        "If fullInspection is false or omitted, return everything except source code and compiled status.\n" +
        "NATIVE TOOLS: In addition to the script tools above, ScriptMCP provides these built-in native tools:\n" +
        "- get_database: Returns the path of the currently active ScriptMCP database.\n" +
        "Use this when the user asks which database is active or where scripts are currently being stored.\n" +
        "Parameter: none.\n" +
        "- set_database: Switches the active ScriptMCP database at runtime.\n" +
        "Parameters: path (string, optional), create (bool, default false).\n" +
        "If path is omitted, it switches to the default database.\n" +
        "If path is only a file name with no directory separators, resolve it relative to the default ScriptMCP data directory.\n" +
        "If the target database does not exist, do not create it implicitly — ask the user for confirmation and call set_database again with create=true only after they confirm.\n" +
        "- delete_database: Deletes a ScriptMCP database file.\n" +
        "Parameters: path (string, required), confirm (bool, default false).\n" +
        "First call it with confirm=false so it can verify that the database exists, reject attempts to delete the default database, and return a yes-or-no confirmation prompt. Only call it again with confirm=true after the user says yes. If the target database is currently active, ScriptMCP will switch to the default database first.\n" +
        "- search_scripts: Searches all stored scripts for a text string or regex pattern.\n" +
        "Parameters: query (string, required), searchIn (string, default \"source\"), regex (bool, default false).\n" +
        "searchIn values: source (line-by-line body search, default), name, description, parameters, scripttype, codeformat, outputinstructions, dependson, externalrefs, all.\n" +
        "Returns each match as 'scriptname: matching line' for source searches, or 'scriptname [fieldname]: value' for metadata searches.\n" +
        "- read_scheduled_task: Reads the most recent scheduled-task output file for a specific script from the output directory beside the database.\n" +
        "If <script>.txt exists from append mode, that file is returned; otherwise the latest timestamped file is returned.\n" +
        "Parameter: function_name (string, required).\n" +
        "- create_scheduled_task: Creates a scheduled task that runs a script at a recurring interval.\n" +
        "On Windows, uses Task Scheduler (schtasks) and runs scriptmcp.exe directly. On Linux/macOS, uses cron.\n" +
        "Parameters: function_name (string, required), function_args (string, default \"{}\"), interval_minutes (int, required), append (bool, default false), rewrite (bool, default false), no_file (bool, default false), telegram (string, default \"\"). Set no_file=true to use --exec instead of --exec-out (no file output), useful with telegram for notification-only tasks. Set telegram to \"true\" to use the default telegram.json beside the database, or provide a custom path to telegram.json.\n" +
        "The task runs via --exec-out. By default it writes each execution result to a timestamped file in output; with append=true it uses --exec-out-append and appends to <script>.txt; with rewrite=true it uses --exec-out-rewrite and overwrites <script>.txt each run. rewrite takes precedence over append.\n" +
        "If the user wants a single output file reused across runs, set append=true (to accumulate) or rewrite=true (to keep only the latest result) during task creation.\n" +
        "After creation, the task is immediately run once.\n" +
        "- delete_scheduled_task: Deletes a scheduled task created for a script.\n" +
        "On Windows, deletes ScriptMCP\\<script> (<interval>m) via schtasks. On Linux/macOS, removes the cron entry tagged # ScriptMCP:<function_name>.\n" +
        "Parameters: function_name (string, required), interval_minutes (int, default 1).\n" +
        "- list_scheduled_tasks: Lists ScriptMCP scheduled tasks.\n" +
        "On Windows, reads tasks from Task Scheduler under \\ScriptMCP\\. On Linux/macOS, lists cron entries tagged # ScriptMCP:.\n" +
        "- start_scheduled_task: Starts or enables a scheduled task.\n" +
        "On Windows, enables ScriptMCP\\<script> (<interval>m) and runs it immediately. On Linux/macOS, cron entries are either present or absent, so this reports the current limitation.\n" +
        "Parameters: function_name (string, required), interval_minutes (int, default 1).\n" +
        "- stop_scheduled_task: Stops or disables a scheduled task.\n" +
        "On Windows, disables ScriptMCP\\<script> (<interval>m). On Linux/macOS, cron entries are either present or absent, so this reports that deletion is required instead.\n" +
        "Parameters: function_name (string, required), interval_minutes (int, default 1).\n" +
        "These are native MCP tools — they do not appear in list_scripts and do not need inspection before use.\n" +
        "Call them directly when the user asks to inspect the active database, switch databases, create a database via set_database, delete a database, search script source or metadata, schedule a script, list tasks, start or stop a task, delete a scheduled task, or read previous execution output.\n" +
        "TELEGRAM NOTIFICATIONS: The CLI supports --telegram [filepath] to send script output to a Telegram channel.\n" +
        "It works with any --exec* mode (--exec, --exec-out, --exec-out-append, --exec-out-rewrite) and is independent of file output.\n" +
        "If no filepath is given, ScriptMCP looks for telegram.json beside the active database.\n" +
        "The telegram.json file must contain botToken and chatId fields.\n" +
        "Messages over 4096 characters are automatically split into chunks.\n" +
        "If the config file is missing or the Telegram API fails, a warning is written to stderr but the process does not fail.\n" +
        "When the user asks to send a script's output to Telegram (outside of scheduled tasks), use call_process with the telegram parameter set to \"true\" (for the default telegram.json beside the database) or to a custom path. This applies to any one-off execution where the user wants the result delivered to Telegram.\n" +
        "TERMINAL DISPLAY: call_process supports a terminal parameter that opens script output in a visible Windows Terminal window or tab instead of capturing it.\n" +
        "Use terminal=\"new_window\" when the user says 'in a new window' — opens a brand-new WT window for every call.\n" +
        "Use terminal=\"named_window\" when the user references a named window — reuses that WT window and adds a tab; use window_name to specify which window (defaults to 'scriptmcp').\n" +
        "Use terminal=\"new_tab\" when the user says 'in a new tab' — opens a new tab inside the current agent WT window.\n" +
        "When terminal is set, call_process returns no output — the script runs and displays in the terminal only.\n" +
        "Leave terminal empty (default) for headless execution where output is captured and returned to the agent.\n" +
        "TOKEN SAVINGS: terminal mode is a major token saver. When the user wants to see a script's output (tables, reports, market data, watchlists, etc.)\n" +
        "but the agent does not need to read or act on it, always prefer terminal mode.\n" +
        "The output goes directly to the user's terminal window — the agent never sees the data, saving hundreds to thousands of tokens per call.\n" +
        "Examples: call_process(name=\"watchlist_show\", arguments=\"{\\\"name\\\":\\\"tech\\\"}\", terminal=\"new_window\") — shows a watchlist in a new window without returning data to the agent.\n" +
        "call_process(name=\"watchlist_correlation_matrix\", arguments=\"{\\\"showMatrix\\\":true}\", terminal=\"new_tab\") — shows the correlation matrix in the agent's tab without returning data.\n" +
        "Multiple parallel calls: call_process(name=\"watchlist_show\", arguments=\"{\\\"symbols\\\":\\\"AMD,TSLA\\\"}\", terminal=\"named_window\") × 3 opens 3 tabs in the named window simultaneously.";

    public static string? TryGetDatabasePathFromArgs(string[]? args)
    {
        if (args == null || args.Length == 0)
            return null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!string.Equals(arg, DatabaseArgumentName, StringComparison.OrdinalIgnoreCase) &&
                !arg.StartsWith(DatabaseArgumentName + "=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rawPath;
            if (arg.StartsWith(DatabaseArgumentName + "=", StringComparison.OrdinalIgnoreCase))
            {
                rawPath = arg[(DatabaseArgumentName.Length + 1)..];
            }
            else
            {
                if (i + 1 >= args.Length)
                    return null;
                rawPath = args[i + 1];
            }

            if (string.IsNullOrWhiteSpace(rawPath))
                return null;

            var candidate = rawPath.Trim();
            if (!Path.IsPathRooted(candidate))
                candidate = Path.Combine(GetDefaultDatabaseDirectory(), candidate);

            return Path.GetFullPath(candidate);
        }

        return null;
    }

    /// <summary>
    /// Resolves ScriptTools.SavePath to either:
    /// - --db &lt;path&gt; (if provided), or
    /// - %LOCALAPPDATA%\ScriptMCP\scriptmcp.db.
    /// </summary>
    public static void ResolveSavePath(string[]? args = null)
    {
        var explicitPath = TryGetDatabasePathFromArgs(args);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var explicitDir = Path.GetDirectoryName(explicitPath);
            if (!string.IsNullOrWhiteSpace(explicitDir))
                Directory.CreateDirectory(explicitDir);

            ScriptTools.SavePath = explicitPath;
            return;
        }

        var appDataDir = GetDefaultDatabaseDirectory();

        Directory.CreateDirectory(appDataDir);

        ScriptTools.SavePath = Path.Combine(appDataDir, DefaultDatabaseFileName);
    }
}
