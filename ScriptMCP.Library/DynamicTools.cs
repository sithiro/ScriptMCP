using System.ComponentModel;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;

namespace ScriptMCP.Library;

// ── Serialization models ──────────────────────────────────────────────────────

public class DynParam
{
    [JsonPropertyName("Name")]        public string Name        { get; set; } = "";
    [JsonPropertyName("Type")]        public string Type        { get; set; } = "string";
    [JsonPropertyName("Description")] public string Description { get; set; } = "";
}

public class DynamicFunction
{
    [JsonPropertyName("Name")]         public string        Name         { get; set; } = "";
    [JsonPropertyName("Description")]  public string        Description  { get; set; } = "";
    [JsonPropertyName("Parameters")]   public List<DynParam> Parameters  { get; set; } = new();
    [JsonPropertyName("FunctionType")] public string        FunctionType { get; set; } = "code";
    [JsonPropertyName("Body")]         public string        Body         { get; set; } = "";
}

// ── DynamicTools ──────────────────────────────────────────────────────────────

public class DynamicTools
{
    private static bool _initialized;
    private static readonly object _initLock = new();

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Path to the SQLite database file. Set by McpConstants.ResolveSavePath().
    /// </summary>
    public static string SavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScriptMCP",
        "tools.db");

    private static string ConnectionString => $"Data Source={SavePath}";

    public DynamicTools() => Initialize();

    // ── Initialization ────────────────────────────────────────────────────────

    private void Initialize()
    {
        lock (_initLock)
        {
            if (_initialized) return;
            _initialized = true;

            EnsureDatabase();
            MigrateFromJson();
        }
    }

    private static void EnsureDatabase()
    {
        var dir = Path.GetDirectoryName(SavePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS functions (
                name              TEXT PRIMARY KEY COLLATE NOCASE,
                description       TEXT NOT NULL,
                parameters        TEXT NOT NULL,
                function_type     TEXT NOT NULL DEFAULT 'code',
                body              TEXT NOT NULL,
                compiled_assembly BLOB
            );";
        cmd.ExecuteNonQuery();
    }

    private void MigrateFromJson()
    {
        // Look for JSON file in same directory or parent directory
        var dbDir = Path.GetDirectoryName(SavePath) ?? Directory.GetCurrentDirectory();
        var jsonPath = Path.Combine(dbDir, "dynamic_functions.json");
        if (!File.Exists(jsonPath))
        {
            var parentJson = Path.GetFullPath(Path.Combine(dbDir, "..", "dynamic_functions.json"));
            if (File.Exists(parentJson))
                jsonPath = parentJson;
            else
                return;
        }

        // Only migrate if DB is empty
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(*) FROM functions";
            var count = (long)countCmd.ExecuteScalar()!;
            if (count > 0) return;
        }

        try
        {
            var json = File.ReadAllText(jsonPath);
            var funcs = JsonSerializer.Deserialize<List<DynamicFunction>>(json, ReadOptions);
            if (funcs == null || funcs.Count == 0) return;

            int migrated = 0;
            foreach (var func in funcs)
            {
                byte[]? assemblyBytes = null;
                if (!IsInstructions(func))
                {
                    var (bytes, errors) = CompileFunction(func);
                    if (bytes == null)
                    {
                        Console.Error.WriteLine($"Migration: failed to compile '{func.Name}': {errors}");
                        // Store without compiled assembly — will fail at call time but data is preserved
                    }
                    assemblyBytes = bytes;
                }

                InsertFunction(conn, func, assemblyBytes);
                migrated++;
            }

            Console.Error.WriteLine($"Migrated {migrated} function(s) from {jsonPath} to SQLite.");

            // Rename old JSON file
            var backupPath = jsonPath + ".migrated";
            try { File.Move(jsonPath, backupPath, overwrite: true); }
            catch { /* best effort */ }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"JSON migration failed: {ex.Message}");
        }
    }

    // ── Listing ───────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_dynamic_functions")]
    [Description("Lists all registered dynamic functions with their name, description, and parameter signatures")]
    public string ListDynamicFunctions()
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, description, parameters, function_type FROM functions";

        using var reader = cmd.ExecuteReader();
        var sb = new StringBuilder();
        bool any = false;

        while (reader.Read())
        {
            any = true;
            var name = reader.GetString(0);
            var description = reader.GetString(1);
            var parametersJson = reader.GetString(2);
            var functionType = reader.GetString(3);
            var isInstr = string.Equals(functionType, "instructions", StringComparison.OrdinalIgnoreCase);

            var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parametersJson, ReadOptions)
                            ?? new List<DynParam>();

            sb.AppendLine($"Name: {name}");
            sb.AppendLine($"  Type: {functionType} → " +
                          (isInstr
                              ? "read and follow the instructions inside"
                              : "execute with call_dynamic_function"));
            sb.AppendLine($"  Description: {description}");

            if (dynParams.Count == 0)
            {
                sb.AppendLine("  Parameters: (none)");
            }
            else
            {
                sb.AppendLine("  Parameters:");
                foreach (var p in dynParams)
                    sb.AppendLine($"    - {p.Name} ({p.Type}): {p.Description}");
            }
            sb.AppendLine();
        }

        return any ? sb.ToString().TrimEnd() : "No dynamic functions registered.";
    }

    // ── Deletion ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "delete_dynamic_function")]
    [Description("Deletes a registered dynamic function from the database by name")]
    public string DeleteDynamicFunction(
        [Description("The name of the dynamic function to delete")] string name)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM functions WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);

        var rows = cmd.ExecuteNonQuery();
        return rows > 0
            ? $"Function '{name}' deleted successfully."
            : $"Function '{name}' not found.";
    }

    // ── Inspection ─────────────────────────────────────────────────────────────

    [McpServerTool(Name = "inspect_dynamic_function")]
    [Description("Inspects a registered dynamic function and returns its full details including source code, parameters, and metadata in a pretty-printed format")]
    public string InspectDynamicFunction(
        [Description("The name of the dynamic function to inspect")] string name)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, description, parameters, function_type, body, compiled_assembly FROM functions WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return $"Function '{name}' not found. Use list_dynamic_functions to see available functions.";

        var funcName       = reader.GetString(0);
        var description    = reader.GetString(1);
        var parametersJson = reader.GetString(2);
        var functionType   = reader.GetString(3);
        var body           = reader.GetString(4);
        var hasAssembly    = !reader.IsDBNull(5);

        var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parametersJson, ReadOptions)
                        ?? new List<DynParam>();

        var isInstr = string.Equals(functionType, "instructions", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine($"Function: {funcName}");
        sb.AppendLine($"Type:        {functionType}");
        sb.AppendLine($"Description: {description}");
        sb.AppendLine($"Compiled:    {(isInstr ? "N/A (instructions)" : hasAssembly ? "Yes" : "No (missing assembly)")}");
        sb.AppendLine();

        if (dynParams.Count == 0)
        {
            sb.AppendLine("Parameters: (none)");
        }
        else
        {
            sb.AppendLine("Parameters:");
            foreach (var p in dynParams)
                sb.AppendLine($"  - {p.Name} ({p.Type}): {p.Description}");
        }

        sb.AppendLine();
        sb.AppendLine($"Source ({(isInstr ? "Instructions" : "C# Code")}):");

        var lines = body.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var lineNum = (i + 1).ToString().PadLeft(3);
            sb.AppendLine($"  {lineNum} | {lines[i].TrimEnd('\r')}");
        }

        return sb.ToString().TrimEnd();
    }

    // ── Registration ──────────────────────────────────────────────────────────

    [McpServerTool(Name = "register_dynamic_function")]
    [Description("Registers a new dynamic function that can be called later. Use functionType 'instructions' " +
                 "for plain English instructions (supports {paramName} substitution). " +
                 "Use functionType 'code' for C# script bodies that are compiled and executed at runtime via Roslyn.")]
    public string RegisterDynamicFunction(
        [Description("Function name")] string name,
        [Description("Description of what the function does")] string description,
        [Description("JSON array of parameters, e.g. [{\"name\":\"x\",\"type\":\"int\",\"description\":\"The number\"}]")]
            string parameters,
        [Description("Plain English instructions (supports {paramName} substitution) or C# body depending on functionType")]
            string body,
        [Description("Function type: 'instructions' for plain English (recommended), or 'code' for C# (compiled at runtime)")]
            string functionType = "instructions")
    {
        try
        {
            var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parameters, ReadOptions)
                            ?? new List<DynParam>();

            var func = new DynamicFunction
            {
                Name         = name,
                Description  = description,
                Parameters   = dynParams,
                FunctionType = functionType ?? "instructions",
                Body         = body,
            };

            byte[]? assemblyBytes = null;

            if (!IsInstructions(func))
            {
                var (bytes, errors) = CompileFunction(func);
                if (bytes == null)
                    return $"Compilation failed:\n{errors}";
                assemblyBytes = bytes;
            }

            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            InsertFunction(conn, func, assemblyBytes);

            return $"{(IsInstructions(func) ? "Instructions" : "Code")} function '{func.Name}' registered successfully " +
                   $"with {func.Parameters.Count} parameter(s).";
        }
        catch (Exception ex)
        {
            return $"Registration failed: {ex.Message}";
        }
    }

    // ── Save (kept for backward compatibility but is now a no-op) ─────────────

    [McpServerTool(Name = "save_dynamic_functions")]
    [Description("Saves all registered dynamic functions to disk as JSON so they persist across server restarts")]
    public string SaveDynamicFunctions()
    {
        return "Functions are now automatically persisted to SQLite on registration. No manual save needed.";
    }

    // ── Invocation ────────────────────────────────────────────────────────────

    [McpServerTool(Name = "call_dynamic_function")]
    [Description("Calls a previously registered dynamic function with the given arguments")]
    public string CallDynamicFunction(
        [Description("The name of the dynamic function to call")] string name,
        [Description("JSON object of argument values, e.g. {\"x\": 5}")] string arguments = "{}")
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, description, parameters, function_type, body, compiled_assembly FROM functions WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return $"Dynamic function '{name}' not found. " +
                   "Use list_dynamic_functions to see available functions.";

        var functionType = reader.GetString(3);
        var body = reader.GetString(4);
        var parametersJson = reader.GetString(2);
        var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parametersJson, ReadOptions)
                        ?? new List<DynParam>();

        if (string.Equals(functionType, "instructions", StringComparison.OrdinalIgnoreCase))
            return ExecuteInstructions(body, dynParams, arguments);

        // Code function — load compiled assembly
        if (reader.IsDBNull(5))
            return $"Function '{name}' has no compiled assembly. Re-register it to compile.";

        var assemblyBytes = (byte[])reader[5];
        return ExecuteCompiledCode(name, assemblyBytes, dynParams, arguments);
    }

    // ── Compilation ───────────────────────────────────────────────────────────

    private static (byte[]? bytes, string? errors) CompileFunction(DynamicFunction func)
    {
        var preamble = new StringBuilder();
        foreach (var param in func.Parameters)
        {
            string csType = (param.Type?.ToLowerInvariant()) switch
            {
                "int"    => "int",
                "long"   => "long",
                "double" => "double",
                "float"  => "float",
                "bool"   => "bool",
                _        => "string",
            };

            string defaultValue = csType switch
            {
                "int"    => "0",
                "long"   => "0L",
                "double" => "0.0",
                "float"  => "0f",
                "bool"   => "false",
                _        => "\"\"",
            };

            string parseExpr = csType switch
            {
                "int"    => $"args.ContainsKey(\"{param.Name}\") && int.TryParse(args[\"{param.Name}\"], out var __{param.Name}_v) ? __{param.Name}_v : {defaultValue}",
                "long"   => $"args.ContainsKey(\"{param.Name}\") && long.TryParse(args[\"{param.Name}\"], out var __{param.Name}_v) ? __{param.Name}_v : {defaultValue}",
                "double" => $"args.ContainsKey(\"{param.Name}\") && double.TryParse(args[\"{param.Name}\"], out var __{param.Name}_v) ? __{param.Name}_v : {defaultValue}",
                "float"  => $"args.ContainsKey(\"{param.Name}\") && float.TryParse(args[\"{param.Name}\"], out var __{param.Name}_v) ? __{param.Name}_v : {defaultValue}",
                "bool"   => $"args.ContainsKey(\"{param.Name}\") && bool.TryParse(args[\"{param.Name}\"], out var __{param.Name}_v) ? __{param.Name}_v : {defaultValue}",
                _        => $"args.ContainsKey(\"{param.Name}\") ? args[\"{param.Name}\"] : {defaultValue}",
            };

            preamble.AppendLine($"            {csType} {param.Name} = {parseExpr};");
        }

        var sourceCode = $$"""
            using System;
            using System.Collections.Generic;
            using System.Globalization;
            using System.IO;
            using System.Linq;
            using System.Net;
            using System.Net.Http;
            using System.Text;
            using System.Text.RegularExpressions;
            using System.Threading.Tasks;

            public static class DynamicScript
            {
                public static string Run(Dictionary<string, string> args)
                {
            {{preamble}}
            {{func.Body}}
                }
            }
            """;

        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

        // Gather references from the runtime directory
        // In self-contained single-file publishes, Assembly.Location can be empty.
        // Fall back to AppContext.BaseDirectory which points to the extraction/publish directory.
        var asmLocation = typeof(object).Assembly.Location;
        var runtimeDir = !string.IsNullOrEmpty(asmLocation)
            ? Path.GetDirectoryName(asmLocation)!
            : AppContext.BaseDirectory;
        var references = new List<MetadataReference>();

        // Core references
        var coreAssemblies = new[]
        {
            "System.Runtime.dll",
            "System.Collections.dll",
            "System.Linq.dll",
            "System.Console.dll",
            "System.Text.RegularExpressions.dll",
            "System.ComponentModel.Primitives.dll",
            "System.Private.CoreLib.dll",
            "System.Private.Uri.dll",
            "netstandard.dll",
        };

        foreach (var asm in coreAssemblies)
        {
            var path = Path.Combine(runtimeDir, asm);
            if (File.Exists(path))
                references.Add(MetadataReference.CreateFromFile(path));
        }

        // Add all System.Net.* and System.* assemblies for broad compatibility
        foreach (var dllPath in Directory.GetFiles(runtimeDir, "System.*.dll"))
        {
            try
            {
                AssemblyName.GetAssemblyName(dllPath);
                var mref = MetadataReference.CreateFromFile(dllPath);
                if (!references.Any(r => ((PortableExecutableReference)r).FilePath == dllPath))
                    references.Add(mref);
            }
            catch { /* skip native DLLs */ }
        }

        var compilation = CSharpCompilation.Create(
            assemblyName: $"DynFunc_{func.Name}_{Guid.NewGuid():N}",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release));

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);

        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString());
            return (null, string.Join("\n", errors));
        }

        return (peStream.ToArray(), null);
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    private static string ExecuteCompiledCode(string funcName, byte[] assemblyBytes, List<DynParam> dynParams, string arguments)
    {
        AssemblyLoadContext? alc = null;
        try
        {
            JsonElement argsElem = ParseArguments(arguments);

            // Build args dictionary
            var args = new Dictionary<string, string>();
            foreach (var param in dynParams)
            {
                if (argsElem.TryGetProperty(param.Name, out var val))
                    args[param.Name] = val.ValueKind == JsonValueKind.String
                        ? val.GetString() ?? ""
                        : val.GetRawText();
                else
                    args[param.Name] = "";
            }

            // Load into collectible ALC
            alc = new AssemblyLoadContext(funcName, isCollectible: true);
            var assembly = alc.LoadFromStream(new MemoryStream(assemblyBytes));

            var scriptType = assembly.GetType("DynamicScript")
                ?? throw new InvalidOperationException("Compiled assembly missing DynamicScript type.");
            var runMethod = scriptType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("Compiled assembly missing Run method.");

            var result = (string?)runMethod.Invoke(null, new object[] { args });

            return result ?? "(no output)";
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            return $"Script execution failed: {inner.Message}";
        }
        catch (Exception ex)
        {
            return $"Script execution failed: {ex.Message}";
        }
        finally
        {
            alc?.Unload();
        }
    }

    private static string ExecuteInstructions(string body, List<DynParam> dynParams, string arguments)
    {
        try
        {
            JsonElement argsElem = ParseArguments(arguments);

            var text = body;
            foreach (var param in dynParams)
            {
                if (argsElem.TryGetProperty(param.Name, out var val))
                    text = text.Replace("{" + param.Name + "}", val.GetString() ?? "");
            }

            return text;
        }
        catch (Exception ex)
        {
            return $"Instructions execution failed: {ex.Message}";
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsInstructions(DynamicFunction f) =>
        string.Equals(f.FunctionType, "instructions", StringComparison.OrdinalIgnoreCase);

    private static JsonElement ParseArguments(string arguments)
    {
        try
        {
            return string.IsNullOrWhiteSpace(arguments)
                ? JsonDocument.Parse("{}").RootElement
                : JsonDocument.Parse(arguments).RootElement;
        }
        catch
        {
            return JsonDocument.Parse("{}").RootElement;
        }
    }

    private static void InsertFunction(SqliteConnection conn, DynamicFunction func, byte[]? assemblyBytes)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO functions (name, description, parameters, function_type, body, compiled_assembly)
            VALUES (@name, @description, @parameters, @function_type, @body, @compiled_assembly)";
        cmd.Parameters.AddWithValue("@name", func.Name);
        cmd.Parameters.AddWithValue("@description", func.Description);
        cmd.Parameters.AddWithValue("@parameters", JsonSerializer.Serialize(func.Parameters));
        cmd.Parameters.AddWithValue("@function_type", func.FunctionType ?? "code");
        cmd.Parameters.AddWithValue("@body", func.Body);
        cmd.Parameters.AddWithValue("@compiled_assembly", (object?)assemblyBytes ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
