namespace ScriptMCP.Library;

public static class McpConstants
{
    public const string Instructions =
        "IMPORTANT: At the start of every conversation, you MUST call list_dynamic_functions before answering any user query, " +
        "to discover available dynamic tools. " +
        "Each function has a Type field that tells you how to use it: " +
        "- Type 'code': call it via call_dynamic_function and return the result to the user. " +
        "- Type 'instructions': call call_dynamic_function to retrieve the instructions, then read and follow them yourself " +
        "when composing your response — do NOT return the raw instruction text to the user. " +
        "When executing any instructions-type function, always call dynamic functions first before resorting to other tools or web search. " +
        "If a suitable dynamic function exists for the user's request, use it instead of other tools or web search. " +
        "When you need a computation and no existing tool fits, use this workflow: " +
        "1) Call list_dynamic_functions to check if a suitable function already exists. " +
        "2) If not, call register_dynamic_function (functionType 'code' for C#, 'instructions' for plain English guidance). " +
        "3) Call call_dynamic_function to invoke it. " +
        "Registered functions persist for the lifetime of the server session. " +
        "COMPILATION: When registering a 'code' function, the server compiles the C# source via Roslyn before storing it. " +
        "If compilation fails, the function is NOT saved to the database — the error diagnostics are returned instead. " +
        "You must fix the compilation errors and re-register until it compiles successfully. " +
        "Only successfully compiled functions are persisted to the database. " +
        "SCRIPTING ENVIRONMENT: Target .NET 9 and C# 13. " +
        "Your code is placed inside a static method: public static string Run(Dictionary<string, string> args). " +
        "You must return a string. Do NOT write a class or method signature — only write the method body. " +
        "The following usings are auto-included: System, System.Collections.Generic, System.Globalization, System.IO, " +
        "System.Linq, System.Net, System.Net.Http, System.Text, System.Text.RegularExpressions, System.Threading.Tasks. " +
        "For any other namespace, use fully qualified names (e.g. System.Diagnostics.Process.Start(...)). " +
        "Available assembly references: all System.*.dll from the .NET 9 runtime directory. " +
        "NOT available: NuGet packages or assemblies outside the runtime (e.g. System.Management, Newtonsoft.Json). " +
        "Use System.Text.Json for JSON. Use System.Net.Http.HttpClient for HTTP. Use System.Diagnostics.Process for shell commands. " +
        "The method is NOT async — use .Result or .GetAwaiter().GetResult() for async calls. " +
        "Supported parameter types: string (default), int, long, double, float, bool. " +
        "Parameters are auto-parsed from the args dictionary and available as local variables in your code.";

    /// <summary>
    /// Resolves DynamicTools.SavePath to %LOCALAPPDATA%\ScriptMCP\tools.db,
    /// creating the directory if needed.
    /// </summary>
    public static void ResolveSavePath()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScriptMCP");

        Directory.CreateDirectory(appDataDir);

        DynamicTools.SavePath = Path.Combine(appDataDir, "tools.db");
    }
}
