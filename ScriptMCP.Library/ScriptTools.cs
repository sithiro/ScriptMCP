using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

public class Script
{
    [JsonPropertyName("Name")]                public string        Name                { get; set; } = "";
    [JsonPropertyName("Description")]         public string        Description         { get; set; } = "";
    [JsonPropertyName("Parameters")]          public List<DynParam> Parameters         { get; set; } = new();
    [JsonPropertyName("FunctionType")]        public string        FunctionType        { get; set; } = "code";
    [JsonPropertyName("Body")]                public string        Body                { get; set; } = "";
    [JsonPropertyName("OutputInstructions")]  public string?       OutputInstructions  { get; set; }
    [JsonPropertyName("Dependencies")]        public string?       Dependencies        { get; set; } = "";
    [JsonPropertyName("CodeFormat")]          public string?       CodeFormat          { get; set; }
}

internal sealed class CompilationOutcome
{
    public byte[]? Bytes { get; init; }
    public string? Errors { get; init; }
    public List<string>? ExternalReferencePaths { get; init; }
}

internal sealed class PreprocessResult
{
    public string CleanedBody { get; init; } = "";
    public List<string> DllReferences { get; init; } = new();
    public List<string> LoadPaths { get; init; } = new();
    public string? Error { get; init; }
}

// ── ScriptTools ──────────────────────────────────────────────────────────────

public class ScriptTools
{
    private const string TopLevelCodeFormat = "top_level";
    private const string LibraryCodeFormat = "library";
    private const string UnmigratedCodeFormat = "legacy_method_body";

    private enum ScriptProcessOutputMode
    {
        Default,
        WriteNew,
        WriteAppend,
        WriteRewrite
    }

    private static bool _initialized;
    private static readonly object _initLock = new();

    // ── Lazy-compiled ScriptMCP helper assembly ────────────────────────────────
    private static readonly Lazy<(byte[] bytes, MetadataReference reference)> _helperAssembly = new(CompileHelperAssembly);

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object _consoleRedirectLock = new();

    /// <summary>
    /// Path to the SQLite database file. Set by McpConstants.ResolveSavePath().
    /// </summary>
    public static string SavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScriptMCP",
        "scriptmcp.db");

    private static string ConnectionString => $"Data Source={SavePath}";

    private static void AppendDatabaseArgumentFromCurrentProcess(System.Diagnostics.ProcessStartInfo psi)
    {
        psi.ArgumentList.Add(McpConstants.DatabaseArgumentName);
        psi.ArgumentList.Add(SavePath);
    }

    private static string BuildDatabaseArgumentForShell()
    {
        return $" {McpConstants.DatabaseArgumentName} \"{SavePath.Replace("\"", "\\\"")}\"";
    }

    public ScriptTools() => Initialize();

    // ── Initialization ────────────────────────────────────────────────────────

    private void Initialize()
    {
        lock (_initLock)
        {
            if (_initialized) return;
            _initialized = true;

            EnsureDatabase();
            MigrateFromJson();
            PreloadAssemblies();
        }
    }

    /// <summary>
    /// Explicitly load key assemblies into the AppDomain so Roslyn can resolve
    /// forwarded types in single-file publish mode (Strategy 2).
    /// </summary>
    private static void PreloadAssemblies()
    {
        // Touch types to load their declaring assemblies
        _ = typeof(System.Net.Http.HttpClient);
        _ = typeof(System.Net.HttpStatusCode);
        _ = typeof(System.Text.Json.JsonDocument);
        _ = typeof(System.Text.RegularExpressions.Regex);
        _ = typeof(System.Diagnostics.Process);
        _ = typeof(System.Globalization.CultureInfo);

        // Explicitly load forwarding assemblies that Roslyn needs for type resolution
        foreach (var name in new[]
        {
            "System.Net.Http",
            "System.Net.Primitives",
            "System.Text.Json",
            "System.Diagnostics.Process",
            "System.Collections",
            "System.Linq",
            "System.Runtime",
        })
        {
            try { Assembly.Load(name); } catch { }
        }
    }

    private static void ConfigureConnection(SqliteConnection conn)
    {
        using var busyCmd = conn.CreateCommand();
        busyCmd.CommandText = "PRAGMA busy_timeout = 5000";
        busyCmd.ExecuteNonQuery();

        using var walCmd = conn.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode = WAL";
        walCmd.ExecuteNonQuery();
    }

    private static void EnsureDatabase()
    {
        var dir = Path.GetDirectoryName(SavePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        // auto_vacuum must be set before WAL mode and before any tables are created.
        // On existing databases this is a no-op (the mode is baked in at creation time).
        using var avCmd = conn.CreateCommand();
        avCmd.CommandText = "PRAGMA auto_vacuum = INCREMENTAL";
        avCmd.ExecuteNonQuery();

        ConfigureConnection(conn);

        // Integrity check — catch corruption early before it spreads.
        // quick_check skips index cross-validation (faster, sufficient for startup gate).
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "PRAGMA quick_check";
        var checkResult = (string?)checkCmd.ExecuteScalar();
        if (!string.Equals(checkResult, "ok", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"WARNING: Database integrity check failed for '{SavePath}': {checkResult}");
            Console.Error.WriteLine("The database may be corrupted. Consider restoring from a backup or deleting and recreating it.");
        }

        using var tx = conn.BeginTransaction();

        // Migrate: rename old 'functions' table to 'scripts' if needed
        using var checkOld = conn.CreateCommand();
        checkOld.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='functions'";
        var hasOldTable = (long)checkOld.ExecuteScalar()! > 0;

        using var checkNew = conn.CreateCommand();
        checkNew.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='scripts'";
        var hasNewTable = (long)checkNew.ExecuteScalar()! > 0;

        if (hasOldTable && !hasNewTable)
        {
            using var rename = conn.CreateCommand();
            rename.CommandText = "ALTER TABLE functions RENAME TO scripts";
            rename.ExecuteNonQuery();

            using var renameCol = conn.CreateCommand();
            renameCol.CommandText = "ALTER TABLE scripts RENAME COLUMN function_type TO script_type";
            renameCol.ExecuteNonQuery();

            Console.Error.WriteLine("Migrated table 'functions' -> 'scripts' and column 'function_type' -> 'script_type'.");
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS scripts (
                name                TEXT PRIMARY KEY COLLATE NOCASE,
                description         TEXT NOT NULL,
                parameters          TEXT NOT NULL,
                script_type         TEXT NOT NULL DEFAULT 'code',
                code_format         TEXT NOT NULL DEFAULT 'top_level',
                body                TEXT NOT NULL,
                compiled_assembly   BLOB,
                output_instructions TEXT
            );";
        cmd.ExecuteNonQuery();

        // Read existing columns once and add any missing ones
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(scripts)";
        using (var reader = pragma.ExecuteReader())
        {
            while (reader.Read())
                existingColumns.Add(reader.GetString(1));
        }

        string[] requiredColumns = ["output_instructions", "dependencies", "code_format", "external_refs"];
        foreach (var col in requiredColumns)
        {
            if (!existingColumns.Contains(col))
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE scripts ADD COLUMN {col} TEXT";
                alter.ExecuteNonQuery();
            }
        }

        // Check schema version. Detection logic for dependencies changed at version 1
        // (switched from lexical name scan to Call/Proc/#load parsing), so databases
        // at version 0 must re-scan every script to purge old false positives.
        const int CurrentSchemaVersion = 1;
        using var versionCmd = conn.CreateCommand();
        versionCmd.CommandText = "PRAGMA user_version";
        var schemaVersion = Convert.ToInt32(versionCmd.ExecuteScalar());
        bool forceRescan = schemaVersion < 1;

        BackfillDependencies(conn, forceRescan);
        MigrateLegacyCodeScripts(conn);

        if (schemaVersion < CurrentSchemaVersion)
        {
            using var setVersion = conn.CreateCommand();
            setVersion.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion}";
            setVersion.ExecuteNonQuery();
        }

        tx.Commit();

        // Reclaim free pages left by previous deletes/updates (no-op if auto_vacuum is off).
        using var vacuumCmd = conn.CreateCommand();
        vacuumCmd.CommandText = "PRAGMA incremental_vacuum";
        vacuumCmd.ExecuteNonQuery();
    }

    private static void BackfillDependencies(SqliteConnection conn, bool forceRescan = false)
    {
        var knownNames = GetScriptNames(conn);

        using var scanCmd = conn.CreateCommand();
        scanCmd.CommandText = forceRescan
            ? "SELECT name, parameters, script_type, body FROM scripts"
            : "SELECT name, parameters, script_type, body FROM scripts WHERE dependencies IS NULL";

        var toUpdate = new List<(string name, string deps)>();
        using (var scanReader = scanCmd.ExecuteReader())
        {
            while (scanReader.Read())
            {
                var func = new Script
                {
                    Name = scanReader.GetString(0),
                    Parameters = JsonSerializer.Deserialize<List<DynParam>>(scanReader.GetString(1), ReadOptions) ?? new List<DynParam>(),
                    FunctionType = scanReader.GetString(2),
                    Body = scanReader.GetString(3),
                };
                var deps = ExtractDependencies(func, knownNames);
                toUpdate.Add((func.Name, DependenciesToCsv(deps)));
            }
        }

        foreach (var (name, deps) in toUpdate)
        {
            using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE scripts SET dependencies = @deps WHERE name = @name";
            upd.Parameters.AddWithValue("@deps", deps);
            upd.Parameters.AddWithValue("@name", name);
            upd.ExecuteNonQuery();
        }

        if (toUpdate.Count > 0)
            Console.Error.WriteLine(forceRescan
                ? $"Rescanned dependencies for {toUpdate.Count} script(s)."
                : $"Backfilled dependencies for {toUpdate.Count} script(s).");
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
        ConfigureConnection(conn);

        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(*) FROM scripts";
            var count = (long)countCmd.ExecuteScalar()!;
            if (count > 0) return;
        }

        try
        {
            var json = File.ReadAllText(jsonPath);
            var funcs = JsonSerializer.Deserialize<List<Script>>(json, ReadOptions);
            if (funcs == null || funcs.Count == 0) return;

            var migrationNames = funcs.Select(f => f.Name).ToList();

            using var tx = conn.BeginTransaction();
            int migrated = 0;
            foreach (var func in funcs)
            {
                byte[]? assemblyBytes = null;
                if (!IsInstructions(func))
                {
                    func.Body = ConvertLegacyMethodBodyToTopLevel(func);
                    func.CodeFormat = TopLevelCodeFormat;
                    var compiled = CompileFunction(func);
                    if (compiled.Bytes == null)
                    {
                        Console.Error.WriteLine($"Migration: failed to compile '{func.Name}': {compiled.Errors}");
                        // Store without compiled assembly — will fail at call time but data is preserved
                    }
                    assemblyBytes = compiled.Bytes;
                }

                var deps = ExtractDependencies(func, migrationNames);
                func.Dependencies = DependenciesToCsv(deps);
                InsertScript(conn, func, assemblyBytes);
                migrated++;
            }
            tx.Commit();

            Console.Error.WriteLine($"Migrated {migrated} script(s) from {jsonPath} to SQLite.");

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

    [McpServerTool(Name = "list_scripts")]
    [Description("Lists all registered script names as a comma-delimited string")]
    public string ListScripts()
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        ConfigureConnection(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM scripts ORDER BY name";

        using var reader = cmd.ExecuteReader();
        var names = new List<string>();

        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return string.Join(", ", names);
    }

    // ── Deletion ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "delete_script")]
    [Description("Deletes a registered script from the database by name")]
    public string DeleteScript(
        [Description("The name of the script to delete")] string name,
        [Description("Set to true to force deletion when other scripts depend on this one")] bool forced = false)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        ConfigureConnection(conn);

        var dependents = FindDependentsOf(conn, name);

        if (dependents.Count > 0 && !forced)
        {
            return $"Cannot delete '{name}' because these scripts depend on it: {string.Join(", ", dependents)}.\n" +
                   "User confirmation is required before forced deletion.";
        }

        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM scripts WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);

        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
        {
            tx.Rollback();
            return $"Script '{name}' not found.";
        }

        tx.Commit();
        var msg = $"Script '{name}' deleted successfully.";
        if (dependents.Count > 0)
            msg += $" Note: the following script(s) depended on it and may break: {string.Join(", ", dependents)}.";
        return msg;
    }

    // ── Inspection ─────────────────────────────────────────────────────────────

    [McpServerTool(Name = "inspect_script")]
    [Description("Inspects a registered script and returns metadata and parameters. Set fullInspection=true to also include source code and compiled status.")]
    public string InspectScript(
        [Description("The name of the script to inspect")] string name,
        [Description("When true, include source code and compiled status. When false or omitted, omit those details.")] bool fullInspection = false)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        ConfigureConnection(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, description, parameters, script_type, code_format, body, compiled_assembly, output_instructions, dependencies FROM scripts WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return $"Script '{name}' not found. Use list_scripts to see available scripts.";

        var funcName            = reader.GetString(0);
        var description         = reader.GetString(1);
        var parametersJson      = reader.GetString(2);
        var functionType        = reader.GetString(3);
        var codeFormat          = reader.IsDBNull(4) ? TopLevelCodeFormat : reader.GetString(4);
        var body                = reader.GetString(5);
        var hasAssembly         = !reader.IsDBNull(6);
        var outputInstructions  = reader.IsDBNull(7) ? null : reader.GetString(7);
        var dependencies        = reader.IsDBNull(8) ? null : reader.GetString(8);

        var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parametersJson, ReadOptions)
                        ?? new List<DynParam>();

            var isInstr = string.Equals(functionType, "instructions", StringComparison.OrdinalIgnoreCase);
            var isLibrary = string.Equals(codeFormat, LibraryCodeFormat, StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine($"Script: {funcName}");
        sb.AppendLine($"Type:        {functionType}");
        if (!isInstr)
            sb.AppendLine($"Code Format: {codeFormat}");
        sb.AppendLine($"Description: {description}");

        if (dynParams.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Parameters: (none)");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("Parameters:");
            foreach (var p in dynParams)
                sb.AppendLine($"  - {p.Name} ({p.Type}): {p.Description}");
        }

        sb.AppendLine();
        sb.AppendLine($"Depends on:  {(string.IsNullOrWhiteSpace(dependencies) ? "(none)" : dependencies)}");

        if (fullInspection)
        {
            sb.AppendLine();
            sb.AppendLine($"Compiled:    {(isInstr ? "N/A (instructions)" : hasAssembly ? "Yes" : "No (missing assembly)")}");
            sb.AppendLine();
            sb.AppendLine($"Source ({(isInstr ? "Instructions" : isLibrary ? "C# Library Code" : "C# Code")}):");

            var lines = body.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var lineNum = (i + 1).ToString().PadLeft(3);
                sb.AppendLine($"  {lineNum} | {lines[i].TrimEnd('\r')}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Output Instructions: {(string.IsNullOrWhiteSpace(outputInstructions) ? "(none)" : outputInstructions)}");

        return sb.ToString().TrimEnd();
    }

    // ── Registration ──────────────────────────────────────────────────────────

    [McpServerTool(Name = "create_script")]
    [Description("Creates a new script that can be called later. Use scriptType 'instructions' " +
                 "for plain English instructions (supports {paramName} substitution). " +
                 "Use scriptType 'code' for C# source compiled via Roslyn. codeFormat 'top_level' creates a runnable script, while 'library' creates a load-only compiled module.")]
    public string CreateScript(
        [Description("Script name")] string name,
        [Description("Description of what the script does")] string description,
        [Description("JSON array of parameters, e.g. [{\"name\":\"x\",\"type\":\"int\",\"description\":\"The number\"}]")]
            string parameters,
        [Description("Plain English instructions (supports {paramName} substitution) or top-level C# source depending on scriptType")]
            string body,
        [Description("Script type: 'instructions' for plain English (recommended), or 'code' for C# source (compiled at runtime)")]
            string functionType = "instructions",
        [Description("Optional instructions for how to present/format the output after execution (e.g. 'present as a markdown table', 'summarize in bullet points')")]
            string outputInstructions = "",
        [Description("For code scripts only: 'top_level' (default runnable script) or 'library' (load-only module for #load).")]
            string codeFormat = "")
    {
        try
        {
            var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parameters, ReadOptions)
                            ?? new List<DynParam>();
            var resolvedFunctionType = string.IsNullOrWhiteSpace(functionType) ? "instructions" : functionType;
            var resolvedCodeFormat = ResolveCodeFormat(resolvedFunctionType, codeFormat);

            var func = new Script
            {
                Name                = name,
                Description         = description,
                Parameters          = dynParams,
                FunctionType        = resolvedFunctionType,
                Body                = body,
                OutputInstructions  = string.IsNullOrWhiteSpace(outputInstructions)
                    ? null
                    : outputInstructions,
                CodeFormat          = resolvedCodeFormat,
            };

            ValidateScriptName(func.Name);

            byte[]? assemblyBytes = null;
            List<string>? externalRefs = null;

            if (!IsInstructions(func))
            {
                var compiled = CompileFunction(func);
                if (compiled.Bytes == null)
                    return $"Compilation failed:\n{compiled.Errors}";
                assemblyBytes = compiled.Bytes;
                externalRefs = compiled.ExternalReferencePaths;
            }

            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            ConfigureConnection(conn);

            using var tx = conn.BeginTransaction();
            var knownNames = GetScriptNames(conn);
            var deps = ExtractDependencies(func, knownNames);
            var mutualDeps = FindDirectMutualDependencies(conn, func.Name, deps);
            if (mutualDeps.Count > 0)
            {
                tx.Rollback();
                return $"Creation failed: direct circular dependency detected for '{func.Name}': " +
                       $"{string.Join(", ", mutualDeps.Select(d => $"{func.Name} <-> {d}"))}.";
            }
            func.Dependencies = DependenciesToCsv(deps);

            InsertScript(conn, func, assemblyBytes, externalRefs);
            tx.Commit();

            return $"{(IsInstructions(func) ? "Instructions" : "Code")} script '{func.Name}' created successfully " +
                   $"with {func.Parameters.Count} parameter(s).";
        }
        catch (Exception ex)
        {
            return $"Creation failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "load_script")]
    [Description("Loads a script from a file. If the script does not exist, it is created. If it already exists, it is updated from the file contents. " +
                 "By default, updates preserve the existing description, parameters, script_type, and output_instructions unless new values are provided.")]
    public string LoadScript(
        [Description("Path to the local file containing the script source or instructions")] string path,
        [Description("Optional script name. Defaults to the file name without extension.")] string name = "",
        [Description("Optional description. On update, omit to preserve the existing description.")] string description = "",
        [Description("Optional JSON array of parameters. On update, omit to preserve the existing parameters.")] string parameters = "",
        [Description("Optional script type: 'code' or 'instructions'. On update, omit to preserve the existing type. New scripts default to 'code'.")] string scriptType = "",
        [Description("Optional code format for code scripts: 'top_level' or 'library'. On update, omit to preserve the existing format.")] string codeFormat = "",
        [Description("Optional output instructions. On update, omit to preserve existing output instructions.")] string outputInstructions = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return "Load failed: path is required.";

            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                return $"Load failed: file not found: {fullPath}";

            var rawContent = File.ReadAllText(fullPath);
            var (headerMeta, body) = ParseScriptMetadataHeader(rawContent);

            var resolvedName = !string.IsNullOrWhiteSpace(name)
                ? name.Trim()
                : headerMeta.TryGetValue("name", out var hName) && !string.IsNullOrWhiteSpace(hName)
                    ? hName
                    : Path.GetFileNameWithoutExtension(fullPath);

            ValidateScriptName(resolvedName);

            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            ConfigureConnection(conn);

            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = @"
                SELECT description, parameters, script_type, code_format, output_instructions
                FROM scripts
                WHERE name = @name";
            readCmd.Parameters.AddWithValue("@name", resolvedName);

            using var reader = readCmd.ExecuteReader();
            var exists = reader.Read();

            headerMeta.TryGetValue("description", out var hDesc);
            headerMeta.TryGetValue("parameters", out var hParams);
            headerMeta.TryGetValue("type", out var hType);
            headerMeta.TryGetValue("code_format", out var hCodeFormat);
            headerMeta.TryGetValue("output_instructions", out var hOutput);

            var resolvedDescription = !string.IsNullOrWhiteSpace(description)
                ? description
                : !string.IsNullOrWhiteSpace(hDesc)
                    ? hDesc
                    : exists
                        ? reader.GetString(0)
                        : $"Loaded from file: {fullPath}";

            var resolvedParameters = !string.IsNullOrWhiteSpace(parameters)
                ? parameters
                : !string.IsNullOrWhiteSpace(hParams)
                    ? hParams
                    : exists
                        ? reader.GetString(1)
                        : "[]";

            var resolvedScriptType = !string.IsNullOrWhiteSpace(scriptType)
                ? scriptType
                : !string.IsNullOrWhiteSpace(hType)
                    ? hType
                    : exists
                        ? reader.GetString(2)
                        : "code";

            var resolvedCodeFormat = !string.IsNullOrWhiteSpace(codeFormat)
                ? codeFormat
                : !string.IsNullOrWhiteSpace(hCodeFormat)
                    ? hCodeFormat
                    : exists && !reader.IsDBNull(3)
                        ? reader.GetString(3)
                        : TopLevelCodeFormat;

            var resolvedOutputInstructions = !string.IsNullOrWhiteSpace(outputInstructions)
                ? outputInstructions
                : !string.IsNullOrWhiteSpace(hOutput)
                    ? hOutput
                    : exists && !reader.IsDBNull(4)
                        ? reader.GetString(4)
                        : "";

            reader.Close();

            var result = CreateScript(
                name: resolvedName,
                description: resolvedDescription,
                parameters: resolvedParameters,
                body: rawContent,
                functionType: resolvedScriptType,
                outputInstructions: resolvedOutputInstructions,
                codeFormat: resolvedCodeFormat);

            if (result.StartsWith("Compilation failed:", StringComparison.OrdinalIgnoreCase) ||
                result.StartsWith("Creation failed:", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            return exists
                ? $"Script '{resolvedName}' loaded from '{fullPath}' and updated."
                : $"Script '{resolvedName}' loaded from '{fullPath}' and created.";
        }
        catch (Exception ex)
        {
            return $"Load failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "export_script")]
    [Description("Exports a stored script to a local file. By default it writes to <name>.csx for code scripts and <name>.txt for instructions scripts.")]
    public string ExportScript(
        [Description("The name of the script to export")] string name,
        [Description("Optional destination path. Defaults to <name>.csx for code scripts or <name>.txt for instructions scripts in the current working directory.")] string path = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Export failed: name is required.";

            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            ConfigureConnection(conn);

            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = @"
                SELECT script_type, code_format, body, description, parameters, output_instructions
                FROM scripts
                WHERE name = @name";
            readCmd.Parameters.AddWithValue("@name", name);

            using var reader = readCmd.ExecuteReader();
            if (!reader.Read())
                return $"Script '{name}' not found.";

            var scriptType = reader.GetString(0);
            var codeFormat = reader.IsDBNull(1) ? TopLevelCodeFormat : reader.GetString(1);
            var body = reader.GetString(2);
            var description = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var parameters = reader.IsDBNull(4) ? "[]" : reader.GetString(4);
            var outputInstructions = reader.IsDBNull(5) ? "" : reader.GetString(5);
            reader.Close();

            var isCode = !string.Equals(scriptType, "instructions", StringComparison.OrdinalIgnoreCase);
            var extension = isCode ? ".csx" : ".txt";
            var prefix = isCode ? "// " : "";

            bool bodyHasMetadata = body.Split('\n')
                .Any(l => ScriptMetadataLineRegex.IsMatch(l.TrimEnd('\r').Trim()));

            string content;
            if (bodyHasMetadata)
            {
                content = body;
            }
            else
            {
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                var versionStr = version != null ? version.ToString() : "0.0.0";

                var metaHeader = new StringBuilder();
                metaHeader.AppendLine($"{prefix}@scriptmcp version: {versionStr}");
                metaHeader.AppendLine($"{prefix}@scriptmcp name: {name}");
                metaHeader.AppendLine($"{prefix}@scriptmcp description: {description}");
                metaHeader.AppendLine($"{prefix}@scriptmcp type: {scriptType}");
                if (isCode && !string.IsNullOrWhiteSpace(codeFormat))
                    metaHeader.AppendLine($"{prefix}@scriptmcp code_format: {codeFormat}");
                metaHeader.AppendLine($"{prefix}@scriptmcp parameters: {parameters}");
                if (!string.IsNullOrWhiteSpace(outputInstructions))
                    metaHeader.AppendLine($"{prefix}@scriptmcp output_instructions: {outputInstructions}");
                metaHeader.AppendLine();

                content = metaHeader.ToString() + body;
            }

            var resolvedPath = string.IsNullOrWhiteSpace(path)
                ? Path.Combine(Directory.GetCurrentDirectory(), name + extension)
                : Path.GetFullPath(path);

            var resolvedDirectory = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(resolvedDirectory))
                Directory.CreateDirectory(resolvedDirectory);

            File.WriteAllText(resolvedPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return $"Script '{name}' exported to '{resolvedPath}'.";
        }
        catch (Exception ex)
        {
            return $"Export failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "update_script")]
    [Description("Updates a single field on an existing script entry. " +
                 "Supported fields: name, description, parameters, script_type, body, output_instructions, dependencies. " +
                 "When the update affects execution, the script is recompiled automatically.")]
    public string UpdateScript(
        [Description("The existing script name to update")] string name,
        [Description("The field/column to update: name, description, parameters, script_type, code_format, body, output_instructions, or dependencies")] string field,
        [Description("The new value for that field")] string value)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        ConfigureConnection(conn);

        using var readCmd = conn.CreateCommand();
        readCmd.CommandText = @"
            SELECT name, description, parameters, script_type, body, output_instructions, dependencies
                 , code_format
            FROM scripts
            WHERE name = @name";
        readCmd.Parameters.AddWithValue("@name", name);

        using var reader = readCmd.ExecuteReader();
        if (!reader.Read())
            return $"Script '{name}' not found.";

        var func = new Script
        {
            Name = reader.GetString(0),
            Description = reader.GetString(1),
            Parameters = JsonSerializer.Deserialize<List<DynParam>>(reader.GetString(2), ReadOptions) ?? new List<DynParam>(),
            FunctionType = reader.GetString(3),
            Body = reader.GetString(4),
            OutputInstructions = reader.IsDBNull(5) ? null : reader.GetString(5),
            Dependencies = reader.IsDBNull(6) ? "" : reader.GetString(6),
            CodeFormat = reader.IsDBNull(7) ? TopLevelCodeFormat : reader.GetString(7),
        };

        reader.Close();

        string normalizedField;
        try
        {
            normalizedField = NormalizeUpdatableField(field);
            ApplyFieldUpdate(func, normalizedField, value);
        }
        catch (Exception ex)
        {
            return $"Update failed: {ex.Message}";
        }

        byte[]? assemblyBytes = null;
        List<string>? externalRefs = null;
        if (!IsInstructions(func))
        {
            var compiled = CompileFunction(func);
            if (compiled.Bytes == null)
                return $"Update failed: compilation failed after changing '{normalizedField}':\n{compiled.Errors}";

            assemblyBytes = compiled.Bytes;
            externalRefs = compiled.ExternalReferencePaths;
        }

        // Auto-compute dependencies unless the user is explicitly setting them
        if (!string.Equals(normalizedField, "dependencies", StringComparison.OrdinalIgnoreCase))
        {
            var knownNames = GetScriptNames(conn);
            var deps = ExtractDependencies(func, knownNames);
            if (string.Equals(normalizedField, "body", StringComparison.OrdinalIgnoreCase))
            {
                var mutualDeps = FindDirectMutualDependencies(conn, func.Name, deps);
                if (mutualDeps.Count > 0)
                {
                    return $"Update failed: direct circular dependency detected for '{func.Name}': " +
                           $"{string.Join(", ", mutualDeps.Select(d => $"{func.Name} <-> {d}"))}.";
                }
            }
            func.Dependencies = DependenciesToCsv(deps);
        }

        using var tx = conn.BeginTransaction();
        using var updateCmd = conn.CreateCommand();
        updateCmd.Transaction = tx;
        updateCmd.CommandText = @"
            UPDATE scripts
            SET name = @new_name,
                description = @description,
                parameters = @parameters,
                script_type = @script_type,
                body = @body,
                compiled_assembly = @compiled_assembly,
                output_instructions = @output_instructions,
                dependencies = @dependencies,
                code_format = @code_format,
                external_refs = @external_refs
            WHERE name = @original_name";
        updateCmd.Parameters.AddWithValue("@new_name", func.Name);
        updateCmd.Parameters.AddWithValue("@description", func.Description);
        updateCmd.Parameters.AddWithValue("@parameters", JsonSerializer.Serialize(func.Parameters));
        updateCmd.Parameters.AddWithValue("@script_type", func.FunctionType ?? "code");
        updateCmd.Parameters.AddWithValue("@body", func.Body);
        updateCmd.Parameters.AddWithValue("@compiled_assembly", (object?)assemblyBytes ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@output_instructions", (object?)func.OutputInstructions ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@dependencies", (object?)func.Dependencies ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@code_format", (object?)func.CodeFormat ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@external_refs",
            externalRefs != null && externalRefs.Count > 0
                ? JsonSerializer.Serialize(externalRefs)
                : (object)DBNull.Value);
        updateCmd.Parameters.AddWithValue("@original_name", name);

        try
        {
            var rows = updateCmd.ExecuteNonQuery();
            if (rows == 0)
            {
                tx.Rollback();
                return $"Script '{name}' not found.";
            }

            // On rename: auto-patch callers that reference the old name
            var patchedCallers = new List<string>();
            bool isRename = string.Equals(normalizedField, "name", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(name, func.Name, StringComparison.OrdinalIgnoreCase);
            if (isRename)
            {
                var dependents = FindDependentsOf(conn, name);
                foreach (var depName in dependents)
                {
                    // Skip the function being renamed (already updated above)
                    if (string.Equals(depName, name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    using var readDep = conn.CreateCommand();
                    readDep.Transaction = tx;
                    readDep.CommandText = "SELECT name, description, parameters, script_type, body, output_instructions, code_format FROM scripts WHERE name = @n";
                    readDep.Parameters.AddWithValue("@n", depName);

                    Script? depFunc = null;
                    using (var depReader = readDep.ExecuteReader())
                    {
                        if (!depReader.Read()) continue;
                        depFunc = new Script
                        {
                            Name = depReader.GetString(0),
                            Description = depReader.GetString(1),
                            Parameters = JsonSerializer.Deserialize<List<DynParam>>(depReader.GetString(2), ReadOptions) ?? new List<DynParam>(),
                            FunctionType = depReader.GetString(3),
                            Body = depReader.GetString(4),
                            OutputInstructions = depReader.IsDBNull(5) ? null : depReader.GetString(5),
                            CodeFormat = depReader.IsDBNull(6) ? TopLevelCodeFormat : depReader.GetString(6),
                        };
                    }

                    // Replace old name with new name in Call/Proc patterns
                    var patternOld = new Regex(
                        @"(ScriptMCP\.\s*(?:Call|Proc)\s*\(\s*"")" + Regex.Escape(name) + @"("")",
                        RegexOptions.IgnoreCase);
                    var newBody = patternOld.Replace(depFunc.Body, "${1}" + func.Name + "${2}");
                    if (newBody == depFunc.Body) continue;

                    depFunc.Body = newBody;

                    // Recompile the patched caller
                    byte[]? depAsm = null;
                    if (!IsInstructions(depFunc))
                    {
                        var compiledDep = CompileFunction(depFunc);
                        if (compiledDep.Bytes == null)
                        {
                            // Can't patch this caller — skip but don't fail the rename
                            Console.Error.WriteLine($"Rename auto-patch: failed to recompile '{depName}': {compiledDep.Errors}");
                            continue;
                        }
                        depAsm = compiledDep.Bytes;
                    }

                    var depKnown = GetScriptNames(conn);
                    var depDeps = ExtractDependencies(depFunc, depKnown);
                    depFunc.Dependencies = DependenciesToCsv(depDeps);

                    using var patchCmd = conn.CreateCommand();
                    patchCmd.Transaction = tx;
                    patchCmd.CommandText = @"
                        UPDATE scripts
                        SET body = @body,
                            compiled_assembly = @asm,
                            dependencies = @deps
                        WHERE name = @n";
                    patchCmd.Parameters.AddWithValue("@body", depFunc.Body);
                    patchCmd.Parameters.AddWithValue("@asm", (object?)depAsm ?? DBNull.Value);
                    patchCmd.Parameters.AddWithValue("@deps", (object?)depFunc.Dependencies ?? DBNull.Value);
                    patchCmd.Parameters.AddWithValue("@n", depName);
                    patchCmd.ExecuteNonQuery();

                    patchedCallers.Add(depName);
                }
            }

            tx.Commit();

            var msg = $"Script '{name}' updated successfully: {normalizedField}.";
            if (patchedCallers.Count > 0)
                msg += $" Auto-patched caller(s): {string.Join(", ", patchedCallers)}.";
            return msg;
        }
        catch (SqliteException) when (string.Equals(normalizedField, "name", StringComparison.OrdinalIgnoreCase))
        {
            tx.Rollback();
            return $"Update failed: a script named '{func.Name}' already exists.";
        }
    }

    // ── Compilation ──────────────────────────────────────────────────────────

    [McpServerTool(Name = "compile_script")]
    [Description("Compiles a registered code script from its stored source, refreshes the stored compiled assembly, and exports the assembly to a local file.")]
    public string CompileScript(
        [Description("The name of the script to compile")] string name,
        [Description("Optional destination path for the compiled assembly. Defaults to <name>.dll in the current working directory.")] string path = "")
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        ConfigureConnection(conn);

        using var readCmd = conn.CreateCommand();
        readCmd.CommandText = "SELECT parameters, script_type, body FROM scripts WHERE name = @name";
        readCmd.Parameters.AddWithValue("@name", name);

        using var reader = readCmd.ExecuteReader();
        if (!reader.Read())
            return $"Script '{name}' not found.";

        var parametersJson = reader.GetString(0);
        var functionType   = reader.GetString(1);
        var body           = reader.GetString(2);
        reader.Close();

        if (string.Equals(functionType, "instructions", StringComparison.OrdinalIgnoreCase))
            return $"Script '{name}' is an instructions script — nothing to compile.";

        var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parametersJson, ReadOptions)
                        ?? new List<DynParam>();

        using var formatCmd = conn.CreateCommand();
        formatCmd.CommandText = "SELECT code_format FROM scripts WHERE name = @name";
        formatCmd.Parameters.AddWithValue("@name", name);
        var codeFormatObj = formatCmd.ExecuteScalar();
        var codeFormat = codeFormatObj == null || codeFormatObj == DBNull.Value
            ? TopLevelCodeFormat
            : Convert.ToString(codeFormatObj, CultureInfo.InvariantCulture) ?? TopLevelCodeFormat;

        var func = new Script
        {
            Name         = name,
            FunctionType = functionType,
            Body         = body,
            Parameters   = dynParams,
            CodeFormat   = ResolveCodeFormat(functionType, codeFormat),
        };

        var compiled = CompileFunction(func);
        if (compiled.Bytes == null)
            return $"Recompilation failed:\n{compiled.Errors}";

        var resolvedPath = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(Directory.GetCurrentDirectory(), $"{name}.dll")
            : Path.GetFullPath(path);
        var resolvedDirectory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(resolvedDirectory))
            Directory.CreateDirectory(resolvedDirectory);

        using var tx = conn.BeginTransaction();
        using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = "UPDATE scripts SET compiled_assembly = @asm, code_format = @code_format, external_refs = @external_refs WHERE name = @name";
        updateCmd.Parameters.AddWithValue("@name", name);
        updateCmd.Parameters.AddWithValue("@asm", compiled.Bytes);
        updateCmd.Parameters.AddWithValue("@code_format", func.CodeFormat);
        updateCmd.Parameters.AddWithValue("@external_refs",
            compiled.ExternalReferencePaths != null && compiled.ExternalReferencePaths.Count > 0
                ? JsonSerializer.Serialize(compiled.ExternalReferencePaths)
                : (object)DBNull.Value);
        updateCmd.ExecuteNonQuery();
        tx.Commit();

        File.WriteAllBytes(resolvedPath, compiled.Bytes);
        return $"Script '{name}' compiled and exported to '{resolvedPath}'.";
    }

    // ── Invocation ────────────────────────────────────────────────────────────

    [McpServerTool(Name = "call_script")]
    [Description("Calls a previously registered script with the given arguments")]
    public string CallScript(
        [Description("The name of the script to call")] string name,
        [Description("JSON object of argument values, e.g. {\"x\": 5}")] string arguments = "{}")
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        ConfigureConnection(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, description, parameters, script_type, body, compiled_assembly, output_instructions, external_refs, code_format FROM scripts WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return $"Script '{name}' not found. " +
                   "Use list_scripts to see available scripts.";

        var functionType = reader.GetString(3);
        var body = reader.GetString(4);
        var parametersJson = reader.GetString(2);
        var outputInstructions = reader.IsDBNull(6) ? null : reader.GetString(6);
        var externalRefsJson = reader.IsDBNull(7) ? null : reader.GetString(7);
        var codeFormat = reader.IsDBNull(8) ? TopLevelCodeFormat : reader.GetString(8);
        var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parametersJson, ReadOptions)
                        ?? new List<DynParam>();
        var externalRefs = !string.IsNullOrEmpty(externalRefsJson)
            ? JsonSerializer.Deserialize<List<string>>(externalRefsJson) : null;

        string result;

        if (string.Equals(functionType, "instructions", StringComparison.OrdinalIgnoreCase))
        {
            result = ExecuteInstructions(body, dynParams, arguments);
        }
        else
        {
            if (string.Equals(codeFormat, LibraryCodeFormat, StringComparison.OrdinalIgnoreCase))
                return $"Script '{name}' is a library code script and cannot be executed directly. Load it from another script with #load.";

            // Code function — load compiled assembly
            if (reader.IsDBNull(5))
                return $"Script '{name}' has no compiled assembly. Re-register it to compile.";

            var assemblyBytes = (byte[])reader[5];
            result = ExecuteCompiledCode(name, assemblyBytes, dynParams, arguments, externalRefs);
        }

        if (!string.IsNullOrWhiteSpace(outputInstructions))
            result += $"\n\n[Output Instructions]: {outputInstructions}";

        return result;
    }

    public void CallScriptStreaming(string name, string arguments = "{}")
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        ConfigureConnection(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, parameters, script_type, body, compiled_assembly, external_refs, code_format FROM scripts WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            Console.Error.WriteLine($"Script '{name}' not found.");
            return;
        }

        var functionType = reader.GetString(2);
        var parametersJson = reader.GetString(1);
        var externalRefsJson = reader.IsDBNull(5) ? null : reader.GetString(5);
        var codeFormat = reader.IsDBNull(6) ? TopLevelCodeFormat : reader.GetString(6);
        var dynParams = JsonSerializer.Deserialize<List<DynParam>>(parametersJson, ReadOptions) ?? new List<DynParam>();
        var externalRefs = !string.IsNullOrEmpty(externalRefsJson)
            ? JsonSerializer.Deserialize<List<string>>(externalRefsJson) : null;

        if (string.Equals(functionType, "instructions", StringComparison.OrdinalIgnoreCase))
        {
            Console.Write(ExecuteInstructions(reader.GetString(3), dynParams, arguments));
            return;
        }

        if (string.Equals(codeFormat, LibraryCodeFormat, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Script '{name}' is a library code script and cannot be executed directly.");
            Environment.ExitCode = 1;
            return;
        }

        if (reader.IsDBNull(4))
        {
            Console.Error.WriteLine($"Script '{name}' has no compiled assembly.");
            return;
        }

        var assemblyBytes = (byte[])reader[4];

        AssemblyLoadContext? alc = null;
        try
        {
            var rawArguments = NormalizeRawArguments(arguments);
            var commandLineArgs = BuildTopLevelCommandLineArgs(rawArguments);

            alc = new AssemblyLoadContext(name, isCollectible: true);

            if (externalRefs != null && externalRefs.Count > 0)
            {
                alc.Resolving += (context, assemblyName) =>
                {
                    foreach (var refPath in externalRefs)
                    {
                        if (!File.Exists(refPath)) continue;
                        try
                        {
                            var refName = AssemblyName.GetAssemblyName(refPath);
                            if (string.Equals(refName.Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                                return context.LoadFromAssemblyPath(refPath);
                        }
                        catch { }
                    }
                    return null;
                };
            }

            var helperAssembly = alc.LoadFromStream(new MemoryStream(_helperAssembly.Value.bytes));
            var assembly = alc.LoadFromStream(new MemoryStream(assemblyBytes));

            Environment.SetEnvironmentVariable("SCRIPTMCP_DB", SavePath);
            SetScriptRuntimeArgs(helperAssembly, rawArguments);

            var entryPoint = assembly.EntryPoint
                ?? throw new InvalidOperationException("Compiled assembly missing entry point.");

            ExecuteTopLevelAssemblyStreaming(entryPoint, commandLineArgs);
        }
        catch (TargetInvocationException ex)
        {
            Console.Error.WriteLine($"Script execution failed: {(ex.InnerException ?? ex).Message}");
            Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Script execution failed: {ex.Message}");
            Environment.ExitCode = 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCRIPTMCP_DB", null);
            alc?.Unload();
        }
    }

    // ── Out-of-process invocation ──────────────────────────────────────────────

    [McpServerTool(Name = "call_process")]
    [Description("Calls a script in a separate process (out-of-process execution). " +
                 "Useful for parallel execution or isolating side effects. " +
                 "Set terminal to display output in a visible Windows Terminal window or tab: " +
                 "'window' opens a brand-new WT window for every call (use when user says 'in a new window'); " +
                 "'tabs' reuses one named WT window and adds a tab for each call (use when user says 'in the scriptmcp window'); " +
                 "'self' opens a new tab inside the current agent WT window (use when user says 'in a new tab' or 'in my terminal'). " +
                 "Leave terminal empty for headless execution where output is captured and returned.")]
    public string CallProcess(
        [Description("The name of the script to call")] string name,
        [Description("JSON object of argument values, e.g. {\"x\": 5}")] string arguments = "{}",
        [Description("Output mode: Default (uses --exec, no persisted output file), WriteNew (uses --exec-out, writes a new file per execution), WriteAppend (uses --exec-out-append, appends to one stable file), WriteRewrite (uses --exec-out-rewrite, overwrites one stable file each run)")] string output_mode = "Default",
        [Description("When true, send the script output to a Telegram channel using telegram.json beside the database. Or provide a custom path to telegram.json.")] string telegram = "",
        [Description("Open output in a visible Windows Terminal window or tab instead of capturing it. Values: 'window' (new WT window each call), 'tabs' (one named WT window, subsequent calls add tabs), 'self' (new tab in the current agent WT window). Leave empty for headless execution.")] string terminal = "",
        [Description("Custom title for the terminal window or tab. Defaults to the script name if not specified.")] string title = "")
    {
        if (!string.IsNullOrWhiteSpace(terminal))
        {
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                return "Error: terminal parameter is only supported on Windows.";

            var exePath2 = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath2))
                return "Error: unable to resolve the current executable path.";

            string dbPath2 = "";
            var cmdArgs2 = Environment.GetCommandLineArgs();
            for (int i = 0; i < cmdArgs2.Length - 1; i++)
                if (string.Equals(cmdArgs2[i], "--db", StringComparison.OrdinalIgnoreCase)) { dbPath2 = cmdArgs2[i + 1]; break; }
            if (string.IsNullOrEmpty(dbPath2))
                dbPath2 = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScriptMCP", "scriptmcp.db");

            var exeEsc2    = exePath2.Replace("'", "''");
            var dbEsc2     = dbPath2.Replace("'", "''");
            var nameEsc2   = name.Replace("'", "''");
            var argsForPS2 = arguments.Replace("'", "''").Replace("\"", "\\\"");

            var psCmd2 =
                "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " +
                $"& '{exeEsc2}' --db '{dbEsc2}' --exec-stream {nameEsc2} '{argsForPS2}'; " +
                "Write-Host ''; " +
                "Write-Host 'Press any key to close...' -NoNewline; " +
                "$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')";
            var encoded2 = Convert.ToBase64String(Encoding.Unicode.GetBytes(psCmd2));

            string wtFlag;
            switch (terminal.Trim().ToLowerInvariant())
            {
                case "window": wtFlag = "-w new";        break;
                case "tabs":   wtFlag = "-w scriptmcp";  break;
                case "self":   wtFlag = "-w 0";          break;
                default: return $"Error: invalid terminal value '{terminal}'. Supported: window, tabs, self.";
            }

            try
            {
                var tabTitle = string.IsNullOrWhiteSpace(title) ? name : title;
                var psi2 = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "wt.exe",
                    Arguments = $"{wtFlag} new-tab --title \"{tabTitle}\" powershell.exe -NoProfile -EncodedCommand {encoded2}",
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(psi2);
                return string.Empty;
            }
            catch (Exception ex2)
            {
                return $"Error launching terminal: {ex2.Message}";
            }
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return "Error: unable to resolve the current executable path.";

        if (!Enum.TryParse<ScriptProcessOutputMode>(output_mode, ignoreCase: true, out var outputMode))
            return "Error: invalid output_mode. Supported values: Default, WriteNew, WriteAppend, WriteRewrite.";

        var execFlag = outputMode switch
        {
            ScriptProcessOutputMode.WriteNew => "--exec-out",
            ScriptProcessOutputMode.WriteAppend => "--exec-out-append",
            ScriptProcessOutputMode.WriteRewrite => "--exec-out-rewrite",
            _ => "--exec"
        };

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                CreateNoWindow = true,
            };
            AppendDatabaseArgumentFromCurrentProcess(psi);
            psi.ArgumentList.Add(execFlag);
            psi.ArgumentList.Add(name);
            psi.ArgumentList.Add(arguments);

            if (!string.IsNullOrWhiteSpace(telegram))
            {
                psi.ArgumentList.Add("--telegram");
                if (!string.Equals(telegram, "true", StringComparison.OrdinalIgnoreCase))
                    psi.ArgumentList.Add(telegram);
            }

            var proc = System.Diagnostics.Process.Start(psi)!;
            proc.StandardInput.Close();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(120_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return $"Error: process timed out after 120 seconds.";
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            return proc.ExitCode == 0
                ? stdout
                : $"Error (exit code {proc.ExitCode}):\n{stderr}\n{stdout}".Trim();
        }
        catch (Exception ex)
        {
            return $"Error spawning process: {ex.Message}";
        }
    }

    // ── Scheduled Task Output ────────────────────────────────────────────────

    [McpServerTool(Name = "read_scheduled_task")]
    [Description("Reads the most recent scheduled-task output file for the specified script.")]
    public string ReadScheduledTask(
        [Description("Script name whose latest scheduled-task output should be returned")] string function_name)
    {
        var outputDirs = new[]
        {
            GetScheduledTaskOutputDirectory()
        }
        .Where(Directory.Exists)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        if (outputDirs.Length == 0)
            return "(empty)";

        var prefix = GetScheduledTaskFilePrefix(function_name);
        var appendFile = outputDirs
            .Select(dir => Path.Combine(dir, $"{prefix}.txt"))
            .Where(File.Exists)
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        var pattern = $"^{Regex.Escape(prefix)}_(\\d{{6}}_\\d{{6}})\\.txt$";
        var latestFile = outputDirs
            .SelectMany(dir => Directory.EnumerateFiles(dir, $"{prefix}_*.txt"))
            .Select(path => new FileInfo(path))
            .Select(file => new
            {
                File = file,
                Match = Regex.Match(file.Name, pattern, RegexOptions.CultureInvariant)
            })
            .Where(x => x.Match.Success)
            .OrderByDescending(x => x.Match.Groups[1].Value, StringComparer.Ordinal)
            .Select(x => x.File)
            .FirstOrDefault();

        var chosenFile = latestFile;
        if (appendFile != null && appendFile.Exists &&
            (chosenFile == null || appendFile.LastWriteTimeUtc >= chosenFile.LastWriteTimeUtc))
        {
            chosenFile = appendFile;
        }

        if (chosenFile == null || !chosenFile.Exists)
            return $"No scheduled-task output found for '{function_name}'";

        var content = File.ReadAllText(chosenFile.FullName);
        return string.IsNullOrEmpty(content) ? "(empty)" : content;
    }

    public static string GetScheduledTaskOutputDirectory() =>
        Path.Combine(Path.GetDirectoryName(SavePath) ?? ".", "output");

    public static string GetScheduledTaskFilePrefix(string functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return "unnamed";

        var sanitized = new string(functionName
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();

        return string.IsNullOrEmpty(sanitized) ? "unnamed" : sanitized;
    }

    public static string GetScheduledTaskOutputPath(string functionName, DateTime? utcNow = null)
    {
        var outputDir = GetScheduledTaskOutputDirectory();
        Directory.CreateDirectory(outputDir);

        var prefix = GetScheduledTaskFilePrefix(functionName);
        var timestamp = (utcNow ?? DateTime.UtcNow).ToString("yyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(outputDir, $"{prefix}_{timestamp}.txt");
    }

    public static string GetScheduledTaskAppendOutputPath(string functionName)
    {
        var outputDir = GetScheduledTaskOutputDirectory();
        Directory.CreateDirectory(outputDir);

        var prefix = GetScheduledTaskFilePrefix(functionName);
        return Path.Combine(outputDir, $"{prefix}.txt");
    }

    // ── Scheduled Tasks ────────────────────────────────────────────────────────

    [McpServerTool(Name = "create_scheduled_task")]
    [Description("Creates a scheduled task (Windows Task Scheduler or cron on Linux/macOS) that runs a ScriptMCP script at a given interval in minutes")]
    public string CreateScheduledTask(
        [Description("Name of the ScriptMCP script to run")] string function_name,
        [Description("JSON arguments for the script (default: {})")] string function_args = "{}",
        [Description("How often to run the task, in minutes")] int interval_minutes = 1,
        [Description("When true, append each result to a stable <script>.txt file instead of creating a new timestamped file")] bool append = false,
        [Description("When true, overwrite a stable <script>.txt file each run instead of creating a new timestamped file. Takes precedence over append.")] bool rewrite = false,
        [Description("When true, do not write output to a file. Uses --exec instead of --exec-out. Useful with telegram for notification-only tasks.")] bool no_file = false,
        [Description("When true, send the script output to a Telegram channel using telegram.json beside the database. Or provide a custom path to telegram.json.")] string telegram = "")
    {
        string exePath = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exePath))
            return "Error: Unable to resolve the current executable path.";

        var execFlag = no_file ? "--exec" : rewrite ? "--exec-out-rewrite" : append ? "--exec-out-append" : "--exec-out";
        var telegramArg = !string.IsNullOrWhiteSpace(telegram) && !string.Equals(telegram, "true", StringComparison.OrdinalIgnoreCase)
            ? $" --telegram \"{telegram.Replace("\"", "\\\"")}\""
            : string.Equals(telegram, "true", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(telegram)
                ? " --telegram"
                : "";

        if (OperatingSystem.IsWindows())
            return CreateScheduledTaskWindows(exePath, function_name, function_args, interval_minutes, execFlag, telegramArg);
        else
            return CreateScheduledTaskCron(exePath, function_name, function_args, interval_minutes, execFlag, telegramArg);
    }

    [McpServerTool(Name = "delete_scheduled_task")]
    [Description("Deletes a scheduled task (Windows Task Scheduler or cron on Linux/macOS) for a ScriptMCP script.")]
    public string DeleteScheduledTask(
        [Description("Name of the ScriptMCP script whose scheduled task should be deleted")] string function_name,
        [Description("Interval in minutes used when the task was created")] int interval_minutes = 1)
    {
        if (OperatingSystem.IsWindows())
            return DeleteScheduledTaskWindows(function_name, interval_minutes);
        else
            return DeleteScheduledTaskCron(function_name);
    }

    [McpServerTool(Name = "list_scheduled_tasks")]
    [Description("Lists ScriptMCP scheduled tasks from Windows Task Scheduler or cron.")]
    public string ListScheduledTasks()
    {
        if (OperatingSystem.IsWindows())
            return ListScheduledTasksWindows();
        else
            return ListScheduledTasksCron();
    }

    [McpServerTool(Name = "start_scheduled_task")]
    [Description("Starts or enables a scheduled task for a ScriptMCP script.")]
    public string StartScheduledTask(
        [Description("Name of the ScriptMCP script whose scheduled task should be started")] string function_name,
        [Description("Interval in minutes used when the task was created")] int interval_minutes = 1)
    {
        if (OperatingSystem.IsWindows())
            return StartScheduledTaskWindows(function_name, interval_minutes);
        else
            return StartScheduledTaskCron(function_name);
    }

    [McpServerTool(Name = "stop_scheduled_task")]
    [Description("Stops or disables a scheduled task for a ScriptMCP script.")]
    public string StopScheduledTask(
        [Description("Name of the ScriptMCP script whose scheduled task should be stopped")] string function_name,
        [Description("Interval in minutes used when the task was created")] int interval_minutes = 1)
    {
        if (OperatingSystem.IsWindows())
            return StopScheduledTaskWindows(function_name, interval_minutes);
        else
            return StopScheduledTaskCron(function_name);
    }

    private static string GetScheduledTaskName(string function_name, int interval_minutes) =>
        $"ScriptMCP\\{function_name} ({interval_minutes}m)";

    private string ListScheduledTasksWindows()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("/Query");
        psi.ArgumentList.Add("/FO");
        psi.ArgumentList.Add("LIST");

        var proc = System.Diagnostics.Process.Start(psi)!;
        string output = proc.StandardOutput.ReadToEnd();
        string error = proc.StandardError.ReadToEnd().Trim();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            var err = new StringBuilder();
            err.AppendLine($"Failed to list scheduled tasks. Exit code: {proc.ExitCode}");
            if (!string.IsNullOrEmpty(error)) err.AppendLine(error);
            return err.ToString().Trim();
        }

        var blocks = Regex.Split(output.Trim(), @"\r?\n\r?\n")
            .Where(block => Regex.IsMatch(block, @"TaskName:\s*\\ScriptMCP\\", RegexOptions.IgnoreCase))
            .ToList();

        if (blocks.Count == 0)
            return "(empty)";

        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            string? taskName = null;
            string? status = null;
            string? nextRun = null;

            foreach (var rawLine in block.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int idx = rawLine.IndexOf(':');
                if (idx < 0) continue;

                var key = rawLine[..idx].Trim();
                var value = rawLine[(idx + 1)..].Trim();

                if (key.Equals("TaskName", StringComparison.OrdinalIgnoreCase))
                    taskName = value;
                else if (key.Equals("Status", StringComparison.OrdinalIgnoreCase))
                    status = value;
                else if (key.Equals("Next Run Time", StringComparison.OrdinalIgnoreCase))
                    nextRun = value;
            }

            if (taskName == null)
                continue;

            sb.AppendLine(taskName);
            if (!string.IsNullOrEmpty(status))
                sb.AppendLine($"  Status: {status}");
            if (!string.IsNullOrEmpty(nextRun))
                sb.AppendLine($"  Next:   {nextRun}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private string ListScheduledTasksCron()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "crontab",
            Arguments = "-l",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var proc = System.Diagnostics.Process.Start(psi)!;
        string output = proc.StandardOutput.ReadToEnd();
        string error = proc.StandardError.ReadToEnd().Trim();
        proc.WaitForExit();

        if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
        {
            var err = new StringBuilder();
            err.AppendLine($"Failed to list scheduled tasks. Exit code: {proc.ExitCode}");
            if (!string.IsNullOrEmpty(error)) err.AppendLine(error);
            return err.ToString().Trim();
        }

        var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("# ScriptMCP:", StringComparison.Ordinal))
            .ToList();

        if (lines.Count == 0)
            return "(empty)";

        return string.Join(Environment.NewLine, lines);
    }

    private string CreateScheduledTaskWindows(string exePath, string function_name, string function_args, int interval_minutes, string execFlag, string telegramArg)
    {
        string tn = GetScheduledTaskName(function_name, interval_minutes);

        // Quote the JSON argument payload for the target process because schtasks
        // stores the executable path separately from the argument string.
        string escapedArgs = function_args.Replace("\"", "\\\"");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Use ArgumentList for proper quoting — schtasks gets each arg correctly escaped
        psi.ArgumentList.Add("/Create");
        psi.ArgumentList.Add("/TN");
        psi.ArgumentList.Add(tn);
        psi.ArgumentList.Add("/TR");
        var dbArg = BuildDatabaseArgumentForShell();
        var taskCommand = new StringBuilder();
        taskCommand.Append($"\"{exePath}\"{dbArg} {execFlag} {function_name} \"{escapedArgs}\"{telegramArg}");
        psi.ArgumentList.Add(taskCommand.ToString());
        psi.ArgumentList.Add("/SC");
        psi.ArgumentList.Add("MINUTE");
        psi.ArgumentList.Add("/MO");
        psi.ArgumentList.Add(interval_minutes.ToString());
        psi.ArgumentList.Add("/F");

        // Create the task
        var proc = System.Diagnostics.Process.Start(psi)!;
        string output = proc.StandardOutput.ReadToEnd().Trim();
        string error = proc.StandardError.ReadToEnd().Trim();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            var err = new StringBuilder();
            err.AppendLine($"Failed to create task. Exit code: {proc.ExitCode}");
            if (!string.IsNullOrEmpty(output)) err.AppendLine(output);
            if (!string.IsNullOrEmpty(error)) err.AppendLine(error);
            return err.ToString().Trim();
        }

        // Run it immediately
        var runPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        runPsi.ArgumentList.Add("/Run");
        runPsi.ArgumentList.Add("/TN");
        runPsi.ArgumentList.Add(tn);
        var runProc = System.Diagnostics.Process.Start(runPsi)!;
        runProc.StandardOutput.ReadToEnd();
        runProc.StandardError.ReadToEnd();
        runProc.WaitForExit();

        var sb = new StringBuilder();
        sb.AppendLine($"Scheduled task created and started.");
        sb.AppendLine($"  Name:     {tn}");
        sb.AppendLine($"  Script:   {function_name}({function_args})");
        sb.AppendLine($"  Exe:      {exePath}");
        sb.AppendLine($"  Interval: Every {interval_minutes} minute(s)");
        var outputDesc = execFlag switch
        {
            "--exec-out-append" => "Append to <script>.txt",
            "--exec-out-rewrite" => "Overwrite <script>.txt each run",
            _ => "New timestamped file per run"
        };
        sb.AppendLine($"  Output:   {outputDesc}");
        if (!string.IsNullOrEmpty(telegramArg))
            sb.AppendLine($"  Telegram: Enabled");
        sb.AppendLine();
        sb.AppendLine("Manage with:");
        sb.AppendLine($"  Run now:  schtasks /Run /TN \"{tn}\"");
        sb.AppendLine($"  Disable:  schtasks /Change /TN \"{tn}\" /Disable");
        sb.AppendLine($"  Delete:   schtasks /Delete /TN \"{tn}\" /F");

        return sb.ToString().Trim();
    }

    private string DeleteScheduledTaskWindows(string function_name, int interval_minutes)
    {
        string tn = GetScheduledTaskName(function_name, interval_minutes);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("/Delete");
        psi.ArgumentList.Add("/TN");
        psi.ArgumentList.Add(tn);
        psi.ArgumentList.Add("/F");

        var proc = System.Diagnostics.Process.Start(psi)!;
        string output = proc.StandardOutput.ReadToEnd().Trim();
        string error = proc.StandardError.ReadToEnd().Trim();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            var err = new StringBuilder();
            err.AppendLine($"Failed to delete task. Exit code: {proc.ExitCode}");
            if (!string.IsNullOrEmpty(output)) err.AppendLine(output);
            if (!string.IsNullOrEmpty(error)) err.AppendLine(error);
            return err.ToString().Trim();
        }

        var sb = new StringBuilder();
        sb.AppendLine("Scheduled task deleted.");
        sb.AppendLine($"  Name:     {tn}");
        sb.AppendLine($"  Script:   {function_name}");
        sb.AppendLine($"  Interval: Every {interval_minutes} minute(s)");
        return sb.ToString().Trim();
    }

    private string StartScheduledTaskWindows(string function_name, int interval_minutes)
    {
        string tn = GetScheduledTaskName(function_name, interval_minutes);

        var enablePsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        enablePsi.ArgumentList.Add("/Change");
        enablePsi.ArgumentList.Add("/TN");
        enablePsi.ArgumentList.Add(tn);
        enablePsi.ArgumentList.Add("/ENABLE");

        var enableProc = System.Diagnostics.Process.Start(enablePsi)!;
        string enableOutput = enableProc.StandardOutput.ReadToEnd().Trim();
        string enableError = enableProc.StandardError.ReadToEnd().Trim();
        enableProc.WaitForExit();

        if (enableProc.ExitCode != 0)
        {
            var err = new StringBuilder();
            err.AppendLine($"Failed to enable task. Exit code: {enableProc.ExitCode}");
            if (!string.IsNullOrEmpty(enableOutput)) err.AppendLine(enableOutput);
            if (!string.IsNullOrEmpty(enableError)) err.AppendLine(enableError);
            return err.ToString().Trim();
        }

        var runPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        runPsi.ArgumentList.Add("/Run");
        runPsi.ArgumentList.Add("/TN");
        runPsi.ArgumentList.Add(tn);

        var runProc = System.Diagnostics.Process.Start(runPsi)!;
        runProc.StandardOutput.ReadToEnd();
        runProc.StandardError.ReadToEnd();
        runProc.WaitForExit();

        var sb = new StringBuilder();
        sb.AppendLine("Scheduled task enabled and started.");
        sb.AppendLine($"  Name:     {tn}");
        sb.AppendLine($"  Script:   {function_name}");
        sb.AppendLine($"  Interval: Every {interval_minutes} minute(s)");
        return sb.ToString().Trim();
    }

    private string StopScheduledTaskWindows(string function_name, int interval_minutes)
    {
        string tn = GetScheduledTaskName(function_name, interval_minutes);

        var disablePsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        disablePsi.ArgumentList.Add("/Change");
        disablePsi.ArgumentList.Add("/TN");
        disablePsi.ArgumentList.Add(tn);
        disablePsi.ArgumentList.Add("/DISABLE");

        var disableProc = System.Diagnostics.Process.Start(disablePsi)!;
        string disableOutput = disableProc.StandardOutput.ReadToEnd().Trim();
        string disableError = disableProc.StandardError.ReadToEnd().Trim();
        disableProc.WaitForExit();

        if (disableProc.ExitCode != 0)
        {
            var err = new StringBuilder();
            err.AppendLine($"Failed to disable task. Exit code: {disableProc.ExitCode}");
            if (!string.IsNullOrEmpty(disableOutput)) err.AppendLine(disableOutput);
            if (!string.IsNullOrEmpty(disableError)) err.AppendLine(disableError);
            return err.ToString().Trim();
        }

        var sb = new StringBuilder();
        sb.AppendLine("Scheduled task disabled.");
        sb.AppendLine($"  Name:     {tn}");
        sb.AppendLine($"  Script:   {function_name}");
        sb.AppendLine($"  Interval: Every {interval_minutes} minute(s)");
        return sb.ToString().Trim();
    }

    private string CreateScheduledTaskCron(string exePath, string function_name, string function_args, int interval_minutes, string execFlag, string telegramArg)
    {
        // Build the cron command line
        string escapedArgs = function_args.Replace("'", "'\\''");
        var dbArg = BuildDatabaseArgumentForShell();
        string command = $"'{exePath}'{dbArg} {execFlag} {function_name} '{escapedArgs}'{telegramArg}";

        // Build the cron schedule expression
        string schedule = interval_minutes switch
        {
            < 60 => $"*/{interval_minutes} * * * *",
            60 => "0 * * * *",
            _ when interval_minutes % 60 == 0 => $"0 */{interval_minutes / 60} * * *",
            _ => $"*/{interval_minutes} * * * *",
        };

        string cronLine = $"{schedule} {command} # ScriptMCP:{function_name}";

        // Read existing crontab, remove any previous entry for this function, append new one
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "crontab",
            Arguments = "-l",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var proc = System.Diagnostics.Process.Start(psi)!;
        string existing = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        // Filter out previous ScriptMCP entries for this function
        string tag = $"# ScriptMCP:{function_name}";
        var lines = existing.Split('\n')
            .Where(l => !l.Contains(tag))
            .ToList();

        // Remove trailing empty lines, then append new entry
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);
        lines.Add(cronLine);
        lines.Add(""); // trailing newline

        string newCrontab = string.Join("\n", lines);

        // Install the new crontab via stdin
        var installPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "crontab",
            Arguments = "-",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var installProc = System.Diagnostics.Process.Start(installPsi)!;
        installProc.StandardInput.Write(newCrontab);
        installProc.StandardInput.Close();
        string installOutput = installProc.StandardOutput.ReadToEnd().Trim();
        string installError = installProc.StandardError.ReadToEnd().Trim();
        installProc.WaitForExit();

        if (installProc.ExitCode != 0)
        {
            var err = new StringBuilder();
            err.AppendLine($"Failed to install crontab. Exit code: {installProc.ExitCode}");
            if (!string.IsNullOrEmpty(installOutput)) err.AppendLine(installOutput);
            if (!string.IsNullOrEmpty(installError)) err.AppendLine(installError);
            return err.ToString().Trim();
        }

        // Run it immediately
        var runPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        AppendDatabaseArgumentFromCurrentProcess(runPsi);
        runPsi.ArgumentList.Add(execFlag);
        runPsi.ArgumentList.Add(function_name);
        runPsi.ArgumentList.Add(function_args);
        if (!string.IsNullOrEmpty(telegramArg))
            runPsi.ArgumentList.Add("--telegram");
        var runProc = System.Diagnostics.Process.Start(runPsi)!;
        runProc.StandardOutput.ReadToEnd();
        runProc.StandardError.ReadToEnd();
        runProc.WaitForExit();

        var sb = new StringBuilder();
        sb.AppendLine($"Cron job created and run once.");
        sb.AppendLine($"  Script:   {function_name}({function_args})");
        sb.AppendLine($"  Exe:      {exePath}");
        sb.AppendLine($"  Schedule: {schedule}");
        sb.AppendLine($"  Tag:      {tag}");
        var outputDesc = execFlag switch
        {
            "--exec-out-append" => "Append to <script>.txt",
            "--exec-out-rewrite" => "Overwrite <script>.txt each run",
            _ => "New timestamped file per run"
        };
        sb.AppendLine($"  Output:   {outputDesc}");
        if (!string.IsNullOrEmpty(telegramArg))
            sb.AppendLine($"  Telegram: Enabled");
        sb.AppendLine();
        sb.AppendLine("Manage with:");
        sb.AppendLine($"  List:     crontab -l");
        sb.AppendLine($"  Remove:   crontab -l | grep -v '{tag}' | crontab -");

        return sb.ToString().Trim();
    }

    private string DeleteScheduledTaskCron(string function_name)
    {
        string tag = $"# ScriptMCP:{function_name}";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "crontab",
            Arguments = "-l",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var proc = System.Diagnostics.Process.Start(psi)!;
        string existing = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        var lines = existing.Split('\n').ToList();
        var filtered = lines.Where(l => !l.Contains(tag)).ToList();
        bool removed = filtered.Count != lines.Count;

        if (!removed)
            return $"Scheduled task not found for '{function_name}'.";

        while (filtered.Count > 0 && string.IsNullOrWhiteSpace(filtered[^1]))
            filtered.RemoveAt(filtered.Count - 1);
        filtered.Add("");

        string newCrontab = string.Join("\n", filtered);

        var installPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "crontab",
            Arguments = "-",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var installProc = System.Diagnostics.Process.Start(installPsi)!;
        installProc.StandardInput.Write(newCrontab);
        installProc.StandardInput.Close();
        string installOutput = installProc.StandardOutput.ReadToEnd().Trim();
        string installError = installProc.StandardError.ReadToEnd().Trim();
        installProc.WaitForExit();

        if (installProc.ExitCode != 0)
        {
            var err = new StringBuilder();
            err.AppendLine($"Failed to install crontab. Exit code: {installProc.ExitCode}");
            if (!string.IsNullOrEmpty(installOutput)) err.AppendLine(installOutput);
            if (!string.IsNullOrEmpty(installError)) err.AppendLine(installError);
            return err.ToString().Trim();
        }

        var sb = new StringBuilder();
        sb.AppendLine("Cron job deleted.");
        sb.AppendLine($"  Script:   {function_name}");
        sb.AppendLine($"  Tag:      {tag}");
        sb.AppendLine();
        sb.AppendLine("Manage with:");
        sb.AppendLine("  List:     crontab -l");
        return sb.ToString().Trim();
    }

    private string StartScheduledTaskCron(string function_name)
    {
        string tag = $"# ScriptMCP:{function_name}";
        return $"Cron jobs cannot be paused/resumed individually. Matching entries remain active if present.\n  Tag:      {tag}";
    }

    private string StopScheduledTaskCron(string function_name)
    {
        string tag = $"# ScriptMCP:{function_name}";
        return $"Cron jobs cannot be paused/resumed individually. Remove the entry with delete_scheduled_task to stop it.\n  Tag:      {tag}";
    }

    // ── Preprocessing ────────────────────────────────────────────────────────

    private static readonly Regex DirectiveRegex = new(
        @"^\s*#(r|load)\s+""(.+)""\s*$", RegexOptions.Compiled);

    private static readonly Regex NuGetSpecRegex = new(
        @"^nuget:\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Scans the top of a script body for #r and #load directives.
    /// Directives are only recognised before the first line of real code.
    /// Returns the cleaned body (directives stripped) plus collected references.
    /// If an unsupported directive is encountered, Error is set.
    /// </summary>
    private static PreprocessResult PreprocessDirectives(string body)
    {
        var dllRefs = new List<string>();
        var loadPaths = new List<string>();
        var cleanedLines = new List<string>();
        var directivesDone = false;

        foreach (var line in body.Split('\n'))
        {
            if (!directivesDone)
            {
                var trimmed = line.TrimStart();

                // Skip blank lines and single-line comments in the directive region
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//"))
                {
                    cleanedLines.Add(line);
                    continue;
                }

                var match = DirectiveRegex.Match(trimmed);
                if (match.Success)
                {
                    var directive = match.Groups[1].Value;  // "r" or "load"
                    var value = match.Groups[2].Value;      // path or nuget spec

                    if (directive == "r")
                    {
                        if (NuGetSpecRegex.IsMatch(value))
                            return new PreprocessResult
                            {
                                Error = $"#r \"nuget:\" directives are not supported. ScriptMCP is self-contained and does not require the .NET SDK. Use #r \"path.dll\" to reference a local assembly instead.",
                            };

                        dllRefs.Add(value);
                    }
                    else // "load"
                    {
                        loadPaths.Add(value);
                    }

                    // Don't add directive lines to cleaned output
                    continue;
                }

                // First non-blank, non-comment, non-directive line — stop scanning
                directivesDone = true;
            }

            cleanedLines.Add(line);
        }

        return new PreprocessResult
        {
            CleanedBody = string.Join("\n", cleanedLines),
            DllReferences = dllRefs,
            LoadPaths = loadPaths,
        };
    }

    /// <summary>
    /// Recursively resolves #load files, preprocessing each for nested directives.
    /// Returns accumulated syntax trees and DLL references.
    /// </summary>
    private static (List<SyntaxTree> trees, List<string> dllRefs, string? error)
        ResolveLoadFiles(List<string> loadPaths, string baseDir, HashSet<string>? visited = null, int depth = 0)
    {
        visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trees = new List<SyntaxTree>();
        var dllRefs = new List<string>();

        if (depth > 10)
            return (trees, dllRefs, "#load directive error: maximum nesting depth (10) exceeded");

        foreach (var loadPath in loadPaths)
        {
            var resolvedPath = Path.IsPathRooted(loadPath)
                ? Path.GetFullPath(loadPath)
                : Path.GetFullPath(Path.Combine(baseDir, loadPath));
            var hasExplicitExtension = !string.IsNullOrWhiteSpace(Path.GetExtension(loadPath));

            string content;
            bool fromDatabase = false;

            if (!hasExplicitExtension)
            {
                var scriptName = Path.GetFileName(loadPath);
                var dbBody = LoadScriptBodyFromDatabase(scriptName);
                if (dbBody == null)
                {
                    return (trees, dllRefs,
                        $"#load directive error: script '{scriptName}' was not found in the active database. " +
                        "Extensionless #load targets are resolved from the database; verify that you are using the correct database.");
                }

                content = dbBody;
                fromDatabase = true;
            }
            else if (File.Exists(resolvedPath))
            {
                content = File.ReadAllText(resolvedPath);
            }
            else
            {
                // Fall back to database: strip .cs extension from filename and look up by script name
                var fileName = Path.GetFileNameWithoutExtension(resolvedPath);
                var dbBody = LoadScriptBodyFromDatabase(fileName);
                if (dbBody == null)
                    return (trees, dllRefs, $"#load directive error: file not found: '{resolvedPath}' (also not found as script '{fileName}' in database)");
                content = dbBody;
                fromDatabase = true;
            }

            if (!visited.Add(resolvedPath))
                return (trees, dllRefs, $"#load directive error: circular reference detected: '{resolvedPath}'");

            var nested = PreprocessDirectives(content);

            if (nested.Error != null)
                return (trees, dllRefs, nested.Error);

            dllRefs.AddRange(nested.DllReferences);

            // Recursively resolve nested #load directives
            if (nested.LoadPaths.Count > 0)
            {
                var loadDir = fromDatabase ? baseDir : (Path.GetDirectoryName(resolvedPath) ?? baseDir);
                var (nestedTrees, nestedDlls, nestedError) =
                    ResolveLoadFiles(nested.LoadPaths, loadDir, visited, depth + 1);

                if (nestedError != null)
                    return (trees, dllRefs, nestedError);

                trees.AddRange(nestedTrees);
                dllRefs.AddRange(nestedDlls);
            }

            trees.Add(CSharpSyntaxTree.ParseText(nested.CleanedBody, path: resolvedPath));
        }

        return (trees, dllRefs, null);
    }

    /// <summary>
    /// Attempts to load a script body from the database by name.
    /// Returns null if the script does not exist.
    /// </summary>
    private static string? LoadScriptBodyFromDatabase(string scriptName)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={SavePath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT body FROM scripts WHERE name = @name";
            cmd.Parameters.AddWithValue("@name", scriptName);
            return cmd.ExecuteScalar() as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves #r "path.dll" references to MetadataReferences.
    /// Relative paths are resolved against baseDir.
    /// Returns the references plus resolved absolute paths, or an error.
    /// </summary>
    private static (List<MetadataReference> refs, List<string> resolvedPaths, string? error)
        ResolveDllReferences(List<string> dllPaths, string baseDir)
    {
        var refs = new List<MetadataReference>();
        var resolvedPaths = new List<string>();

        foreach (var dllPath in dllPaths)
        {
            var resolvedPath = Path.IsPathRooted(dllPath)
                ? Path.GetFullPath(dllPath)
                : Path.GetFullPath(Path.Combine(baseDir, dllPath));

            if (!File.Exists(resolvedPath))
                return (refs, resolvedPaths, $"#r directive error: file not found: '{resolvedPath}'");

            try
            {
                AssemblyName.GetAssemblyName(resolvedPath);
            }
            catch
            {
                return (refs, resolvedPaths, $"#r directive error: '{resolvedPath}' is not a valid .NET assembly");
            }

            refs.Add(MetadataReference.CreateFromFile(resolvedPath));
            resolvedPaths.Add(resolvedPath);
        }

        return (refs, resolvedPaths, null);
    }

    // ── Compilation ───────────────────────────────────────────────────────────

    private static CompilationOutcome CompileFunction(Script func)
    {
        var userSource = func.Body ?? string.Empty;
        var isLibrary = IsLibraryCode(func);

        // Phase 1: Preprocess directives
        var preprocessed = PreprocessDirectives(userSource);
        if (preprocessed.Error != null)
            return new CompilationOutcome { Errors = preprocessed.Error };

        // Phase 2: Resolve #load files (recursive, with circular detection)
        var baseDir = Path.GetDirectoryName(SavePath) ?? Directory.GetCurrentDirectory();
        var allDllRefs = new List<string>(preprocessed.DllReferences);
        var syntaxTreeList = new List<SyntaxTree>();

        if (preprocessed.LoadPaths.Count > 0)
        {
            var (loadTrees, loadDlls, loadError) =
                ResolveLoadFiles(preprocessed.LoadPaths, baseDir);
            if (loadError != null)
                return new CompilationOutcome { Errors = loadError };

            syntaxTreeList.AddRange(loadTrees);
            allDllRefs.AddRange(loadDlls);
        }

        // Add support source and user source trees
        if (!isLibrary)
        {
            var supportSource = BuildTopLevelSupportSource(func.Parameters);
            syntaxTreeList.Add(CSharpSyntaxTree.ParseText(supportSource, path: "__ScriptMcpSupport.cs"));
        }
        syntaxTreeList.Add(CSharpSyntaxTree.ParseText(preprocessed.CleanedBody, path: $"{func.Name}.csx"));

        // Phase 3: Resolve #r "path.dll" references
        var references = GatherMetadataReferences();
        references.Add(_helperAssembly.Value.reference);
        var allExternalPaths = new List<string>();

        if (allDllRefs.Count > 0)
        {
            var (dllMetaRefs, dllPaths, dllError) = ResolveDllReferences(allDllRefs, baseDir);
            if (dllError != null)
                return new CompilationOutcome { Errors = dllError };

            references.AddRange(dllMetaRefs);
            allExternalPaths.AddRange(dllPaths);
        }

        var compilation = CSharpCompilation.Create(
            assemblyName: $"Script_{func.Name}_{Guid.NewGuid():N}",
            syntaxTrees: syntaxTreeList,
            references: references,
            options: new CSharpCompilationOptions(isLibrary ? OutputKind.DynamicallyLinkedLibrary : OutputKind.ConsoleApplication)
                .WithOptimizationLevel(OptimizationLevel.Release));

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);

        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString());
            return new CompilationOutcome { Errors = string.Join("\n", errors) };
        }

        return new CompilationOutcome
        {
            Bytes = peStream.ToArray(),
            ExternalReferencePaths = allExternalPaths.Count > 0 ? allExternalPaths : null,
        };
    }

    private static string BuildTopLevelSupportSource(List<DynParam> parameters)
    {
        var typedMembers = new StringBuilder();
        foreach (var param in parameters)
            typedMembers.AppendLine(BuildTopLevelProperty(param));

        return $$"""
            global using System;
            global using System.Collections.Generic;
            global using System.Globalization;
            global using System.IO;
            global using System.Linq;
            global using System.Net;
            global using System.Net.Http;
            global using System.Text;
            global using System.Text.RegularExpressions;
            global using System.Threading.Tasks;
            global using static __ScriptMcpGlobals;

            internal static class __ScriptMcpGlobals
            {
                public static Dictionary<string, string> scriptArgs => ScriptRuntime.GetArgs();
            {{typedMembers}}
            }
            """;
    }

    private static string BuildTopLevelProperty(DynParam param)
    {
        var csType = GetCSharpParameterType(param.Type);
        var defaultValue = GetDefaultLiteral(csType);
        var argName = EscapeStringLiteral(param.Name);

        var expression = csType switch
        {
            "int" => $$"""scriptArgs.TryGetValue("{{argName}}", out var raw) && int.TryParse(raw, out var parsed) ? parsed : {{defaultValue}}""",
            "long" => $$"""scriptArgs.TryGetValue("{{argName}}", out var raw) && long.TryParse(raw, out var parsed) ? parsed : {{defaultValue}}""",
            "double" => $$"""scriptArgs.TryGetValue("{{argName}}", out var raw) && double.TryParse(raw, out var parsed) ? parsed : {{defaultValue}}""",
            "float" => $$"""scriptArgs.TryGetValue("{{argName}}", out var raw) && float.TryParse(raw, out var parsed) ? parsed : {{defaultValue}}""",
            "bool" => $$"""scriptArgs.TryGetValue("{{argName}}", out var raw) && bool.TryParse(raw, out var parsed) ? parsed : {{defaultValue}}""",
            _ => $$"""scriptArgs.TryGetValue("{{argName}}", out var raw) ? raw : {{defaultValue}}""",
        };

        return $"    public static {csType} {param.Name} => {expression};";
    }

    private static string ConvertLegacyMethodBodyToTopLevel(Script func)
    {
        return $$"""
            var __scriptmcpArgs = scriptArgs;

            string __ScriptMcpLegacyMain()
            {
                var args = __scriptmcpArgs;
            {{IndentCode(func.Body, 4)}}
            }

            var __scriptmcpResult = __ScriptMcpLegacyMain();
            if (!string.IsNullOrEmpty(__scriptmcpResult))
                Console.Write(__scriptmcpResult);
            """;
    }

    private static string IndentCode(string code, int spaces)
    {
        var indent = new string(' ', spaces);
        var lines = (code ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line => indent + line));
    }

    private static string GetCSharpParameterType(string? type) =>
        (type?.ToLowerInvariant()) switch
        {
            "int" => "int",
            "long" => "long",
            "double" => "double",
            "float" => "float",
            "bool" => "bool",
            _ => "string",
        };

    private static string GetDefaultLiteral(string csType) =>
        csType switch
        {
            "int" => "0",
            "long" => "0L",
            "double" => "0.0",
            "float" => "0f",
            "bool" => "false",
            _ => "\"\"",
        };

    private static string EscapeStringLiteral(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Resolves MetadataReferences for Roslyn compilation.
    /// Strategy 1 (dotnet run / normal exe): load from DLL files on disk via typeof(object).Assembly.Location.
    /// Strategy 2 (single-file publish): Assembly.Location is empty and DLLs are bundled in the exe,
    ///   so we read raw metadata directly from loaded assemblies in memory.
    /// </summary>
    private static List<MetadataReference> GatherMetadataReferences()
    {
        var references = new List<MetadataReference>();

        // Strategy 1: File-based — works for dotnet run and non-single-file publishes
        // IL3000: We intentionally check Assembly.Location and handle the empty case in Strategy 2.
#pragma warning disable IL3000
        var asmLocation = typeof(object).Assembly.Location;
#pragma warning restore IL3000
        if (!string.IsNullOrEmpty(asmLocation))
        {
            var runtimeDir = Path.GetDirectoryName(asmLocation)!;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Core references
            foreach (var name in new[]
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
            })
            {
                var path = Path.Combine(runtimeDir, name);
                if (File.Exists(path) && seen.Add(path))
                    references.Add(MetadataReference.CreateFromFile(path));
            }

            // Add all System.* assemblies for broad compatibility
            foreach (var dllPath in Directory.GetFiles(runtimeDir, "System.*.dll"))
            {
                if (!seen.Add(dllPath)) continue;
                try
                {
                    AssemblyName.GetAssemblyName(dllPath);
                    references.Add(MetadataReference.CreateFromFile(dllPath));
                }
                catch { /* skip native DLLs */ }
            }

            return references;
        }

        // Strategy 2: In-memory — works for self-contained single-file publishes
        // Assembly.Location is empty, DLLs are bundled inside the exe.
        // Use TryGetRawMetadata to read metadata directly from loaded assemblies.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            // Skip ScriptMCP assemblies to avoid namespace conflict with the ScriptMCP helper class
            var asmName = asm.GetName().Name;
            if (asmName != null && asmName.StartsWith("ScriptMCP", StringComparison.Ordinal)) continue;

            try
            {
                unsafe
                {
                    if (asm.TryGetRawMetadata(out byte* blob, out int length))
                    {
                        var moduleMetadata = ModuleMetadata.CreateFromMetadata((IntPtr)blob, length);
                        var assemblyMetadata = AssemblyMetadata.Create(moduleMetadata);
                        references.Add(assemblyMetadata.GetReference(display: asm.FullName));
                    }
                }
            }
            catch { /* skip assemblies that can't provide metadata */ }
        }

        return references;
    }

    // ── ScriptMCP helper assembly (compiled once, loaded into each ALC) ──────

    private const string HelperSourceCode = """
        using System;
        using System.Collections.Generic;
        using System.Diagnostics;
        using System.Text.Json;
        using System.Threading;

        public static class ScriptRuntime
        {
            private static readonly AsyncLocal<string?> CurrentRawArguments = new();
            private static readonly AsyncLocal<Dictionary<string, string>?> CurrentArgs = new();

            public static void SetRawArguments(string arguments)
            {
                var raw = string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments;
                CurrentRawArguments.Value = raw;
                CurrentArgs.Value = ParseNamedArgs(raw);
            }

            public static void ClearArgs()
            {
                CurrentRawArguments.Value = null;
                CurrentArgs.Value = null;
            }

            public static string GetRawArguments()
            {
                return CurrentRawArguments.Value ?? "{}";
            }

            public static Dictionary<string, string> GetArgs()
            {
                return CurrentArgs.Value ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            private static Dictionary<string, string> ParseNamedArgs(string arguments)
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(arguments))
                    return values;

                try
                {
                    using var doc = JsonDocument.Parse(arguments);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        return values;

                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        values[property.Name] = property.Value.ValueKind == JsonValueKind.String
                            ? property.Value.GetString() ?? string.Empty
                            : property.Value.GetRawText();
                    }
                }
                catch
                {
                }

                return values;
            }
        }

        public static class ScriptMCP
        {
            private static string? GetDbPath()
            {
                return Environment.GetEnvironmentVariable("SCRIPTMCP_DB");
            }

            private static ProcessStartInfo CreateStartInfo(string functionName, string arguments)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                    throw new InvalidOperationException("Unable to resolve the current executable path.");
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                    CreateNoWindow = true,
                };
                var dbPath = GetDbPath();
                if (!string.IsNullOrWhiteSpace(dbPath))
                {
                    psi.ArgumentList.Add("--db");
                    psi.ArgumentList.Add(dbPath);
                }
                psi.ArgumentList.Add("--exec");
                psi.ArgumentList.Add(functionName);
                psi.ArgumentList.Add(arguments);
                return psi;
            }

            public static string Call(string functionName, string arguments = "{}")
            {
                var psi = CreateStartInfo(functionName, arguments);
                var proc = Process.Start(psi)!;
                proc.StandardInput.Close();
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(120_000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    throw new TimeoutException($"ScriptMCP.Call(\"{functionName}\") timed out after 120 seconds.");
                }
                var stdout = stdoutTask.GetAwaiter().GetResult();
                var stderr = stderrTask.GetAwaiter().GetResult();
                if (proc.ExitCode != 0)
                    throw new Exception($"ScriptMCP.Call(\"{functionName}\") failed (exit code {proc.ExitCode}):\n{stderr}\n{stdout}".Trim());
                return stdout;
            }

            public static Process Proc(string functionName, string arguments = "{}")
            {
                var psi = CreateStartInfo(functionName, arguments);
                var proc = Process.Start(psi)!;
                proc.StandardInput.Close();
                return proc;
            }
        }
        """;

    private static (byte[] bytes, MetadataReference reference) CompileHelperAssembly()
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(HelperSourceCode);
        var references = GatherMetadataReferences();

        var compilation = CSharpCompilation.Create(
            assemblyName: "ScriptMCP.Helpers",
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
            throw new InvalidOperationException(
                $"Failed to compile ScriptMCP helper assembly:\n{string.Join("\n", errors)}");
        }

        var bytes = peStream.ToArray();
        var metadataRef = MetadataReference.CreateFromImage(bytes);
        return (bytes, metadataRef);
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    private static string ExecuteCompiledCode(string funcName, byte[] assemblyBytes, List<DynParam> dynParams, string arguments, List<string>? externalRefs = null)
    {
        AssemblyLoadContext? alc = null;
        try
        {
            var rawArguments = NormalizeRawArguments(arguments);
            var commandLineArgs = BuildTopLevelCommandLineArgs(rawArguments);

            // Load into collectible ALC
            alc = new AssemblyLoadContext(funcName, isCollectible: true);

            // Register resolver for external DLL references (#r directive / NuGet)
            if (externalRefs != null && externalRefs.Count > 0)
            {
                alc.Resolving += (context, assemblyName) =>
                {
                    foreach (var refPath in externalRefs)
                    {
                        if (!File.Exists(refPath)) continue;
                        try
                        {
                            var refName = AssemblyName.GetAssemblyName(refPath);
                            if (string.Equals(refName.Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                                return context.LoadFromAssemblyPath(refPath);
                        }
                        catch { /* skip invalid files */ }
                    }

                    var candidateDirectories = externalRefs
                        .Select(Path.GetDirectoryName)
                        .Where(static dir => !string.IsNullOrWhiteSpace(dir))
                        .Distinct(StringComparer.OrdinalIgnoreCase);

                    foreach (var dir in candidateDirectories)
                    {
                        var candidatePath = Path.Combine(dir!, assemblyName.Name + ".dll");
                        if (!File.Exists(candidatePath)) continue;
                        try
                        {
                            return context.LoadFromAssemblyPath(candidatePath);
                        }
                        catch { /* skip unloadable files */ }
                    }

                    return null;
                };
            }

            var helperAssembly = alc.LoadFromStream(new MemoryStream(_helperAssembly.Value.bytes));
            var assembly = alc.LoadFromStream(new MemoryStream(assemblyBytes));

            Environment.SetEnvironmentVariable("SCRIPTMCP_DB", SavePath);
            SetScriptRuntimeArgs(helperAssembly, rawArguments);

            var entryPoint = assembly.EntryPoint;
            if (entryPoint == null)
                throw new InvalidOperationException("Compiled assembly missing entry point.");

            return ExecuteTopLevelAssembly(entryPoint, commandLineArgs);
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
            Environment.SetEnvironmentVariable("SCRIPTMCP_DB", null);
            alc?.Unload();
        }
    }

    private static void MigrateLegacyCodeScripts(SqliteConnection conn)
    {
        var pending = new List<Script>();

        using (var scanCmd = conn.CreateCommand())
        {
            scanCmd.CommandText = @"
                SELECT name, description, parameters, script_type, body, output_instructions, dependencies, code_format
                FROM scripts
                WHERE script_type = 'code' AND (code_format IS NULL OR code_format = '' OR code_format = @legacy)";
            scanCmd.Parameters.AddWithValue("@legacy", UnmigratedCodeFormat);

            using var reader = scanCmd.ExecuteReader();
            while (reader.Read())
            {
                pending.Add(new Script
                {
                    Name = reader.GetString(0),
                    Description = reader.GetString(1),
                    Parameters = JsonSerializer.Deserialize<List<DynParam>>(reader.GetString(2), ReadOptions) ?? new List<DynParam>(),
                    FunctionType = reader.GetString(3),
                    Body = reader.GetString(4),
                    OutputInstructions = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Dependencies = reader.IsDBNull(6) ? "" : reader.GetString(6),
                    CodeFormat = reader.IsDBNull(7) ? null : reader.GetString(7),
                });
            }
        }

        foreach (var func in pending)
        {
            var migratedBody = ConvertLegacyMethodBodyToTopLevel(func);
            var migratedFunc = new Script
            {
                Name = func.Name,
                Description = func.Description,
                Parameters = func.Parameters,
                FunctionType = func.FunctionType,
                Body = migratedBody,
                OutputInstructions = func.OutputInstructions,
                Dependencies = func.Dependencies,
                CodeFormat = TopLevelCodeFormat,
            };

            var compiled = CompileFunction(migratedFunc);
            using var update = conn.CreateCommand();
            update.CommandText = @"
                UPDATE scripts
                SET body = @body,
                    code_format = @code_format,
                    compiled_assembly = @compiled_assembly
                WHERE name = @name";
            update.Parameters.AddWithValue("@name", migratedFunc.Name);
            update.Parameters.AddWithValue("@body", migratedBody);
            update.Parameters.AddWithValue("@code_format", TopLevelCodeFormat);
            update.Parameters.AddWithValue("@compiled_assembly", (object?)compiled.Bytes ?? DBNull.Value);
            update.ExecuteNonQuery();

            if (compiled.Bytes == null)
                Console.Error.WriteLine($"Top-level migration failed for '{migratedFunc.Name}': {compiled.Errors}");
        }

        if (pending.Count > 0)
            Console.Error.WriteLine($"Migrated {pending.Count} existing code script(s) to top-level source.");
    }

    private static void SetScriptRuntimeArgs(Assembly helperAssembly, string arguments)
    {
        var runtimeType = helperAssembly.GetType("ScriptRuntime")
            ?? throw new InvalidOperationException("Helper assembly missing ScriptRuntime type.");
        var setArgsMethod = runtimeType.GetMethod("SetRawArguments", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Helper assembly missing ScriptRuntime.SetRawArguments method.");
        setArgsMethod.Invoke(null, new object[] { arguments });
    }

    private static string[] BuildTopLevelCommandLineArgs(string arguments)
    {
        return new[] { arguments };
    }

    private static string NormalizeRawArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return "{}";

        try
        {
            return JsonDocument.Parse(arguments).RootElement.GetRawText();
        }
        catch
        {
            return "{}";
        }
    }

    private static string ExecuteTopLevelAssembly(MethodInfo entryPoint, string[] commandLineArgs)
    {
        lock (_consoleRedirectLock)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            using var stdout = new StringWriter(CultureInfo.InvariantCulture);
            using var stderr = new StringWriter(CultureInfo.InvariantCulture);

            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);

                var parameters = entryPoint.GetParameters();
                object? invocationResult = parameters.Length == 0
                    ? entryPoint.Invoke(null, null)
                    : entryPoint.Invoke(null, new object?[] { commandLineArgs });

                if (invocationResult is System.Threading.Tasks.Task task)
                    task.GetAwaiter().GetResult();

                var stderrText = stderr.ToString();
                if (!string.IsNullOrEmpty(stderrText))
                    return stdout.ToString() + stderrText;

                return stdout.ToString();
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
        }
    }

    private static void ExecuteTopLevelAssemblyStreaming(MethodInfo entryPoint, string[] commandLineArgs)
    {
        lock (_consoleRedirectLock)
        {
            var parameters = entryPoint.GetParameters();
            object? invocationResult = parameters.Length == 0
                ? entryPoint.Invoke(null, null)
                : entryPoint.Invoke(null, new object?[] { commandLineArgs });

            if (invocationResult is System.Threading.Tasks.Task task)
                task.GetAwaiter().GetResult();
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

    private static bool IsInstructions(Script f) =>
        string.Equals(f.FunctionType, "instructions", StringComparison.OrdinalIgnoreCase);

    private static bool IsLibraryCode(Script f) =>
        string.Equals(f.FunctionType, "code", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(f.CodeFormat, LibraryCodeFormat, StringComparison.OrdinalIgnoreCase);

    private static string ResolveCodeFormat(string? functionType, string? requestedCodeFormat)
    {
        if (string.Equals(functionType, "instructions", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var normalized = string.IsNullOrWhiteSpace(requestedCodeFormat)
            ? TopLevelCodeFormat
            : requestedCodeFormat.Trim().ToLowerInvariant();

        return normalized switch
        {
            TopLevelCodeFormat => TopLevelCodeFormat,
            LibraryCodeFormat => LibraryCodeFormat,
            "" => TopLevelCodeFormat,
            _ => throw new ArgumentException("code_format must be 'top_level' or 'library' for code scripts."),
        };
    }

    private static void ValidateScriptName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name cannot be empty.");

        if (!Regex.IsMatch(name.Trim(), "^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException(
                "name must contain only letters, numbers, underscore, or hyphen.");
        }
    }

    private static string NormalizeUpdatableField(string field)
    {
        var normalized = (field ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "name" => "name",
            "description" => "description",
            "parameters" => "parameters",
            "script_type" => "script_type",
            "scripttype" => "script_type",
            "function_type" => "script_type",   // backward compat
            "functiontype" => "script_type",    // backward compat
            "code_format" => "code_format",
            "codeformat" => "code_format",
            "body" => "body",
            "output_instructions" => "output_instructions",
            "outputinstructions" => "output_instructions",
            "dependencies" => "dependencies",
            _ => throw new ArgumentException(
                "field must be one of: name, description, parameters, script_type, code_format, body, output_instructions, dependencies."),
        };
    }

    private static void ApplyFieldUpdate(Script func, string field, string value)
    {
        switch (field)
        {
            case "name":
                ValidateScriptName(value);
                func.Name = value.Trim();
                break;

            case "description":
                func.Description = value ?? "";
                break;

            case "parameters":
                func.Parameters = JsonSerializer.Deserialize<List<DynParam>>(value ?? "[]", ReadOptions)
                    ?? new List<DynParam>();
                break;

            case "script_type":
                var scriptType = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
                if (!string.Equals(scriptType, "code", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(scriptType, "instructions", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("script_type must be 'code' or 'instructions'.");
                }
                func.FunctionType = scriptType;
                func.CodeFormat = ResolveCodeFormat(scriptType, func.CodeFormat);
                break;

            case "code_format":
                func.CodeFormat = ResolveCodeFormat(func.FunctionType, value);
                break;

            case "body":
                func.Body = value ?? "";
                break;

            case "output_instructions":
                func.OutputInstructions = string.IsNullOrWhiteSpace(value) ? null : value;
                break;

            case "dependencies":
                func.Dependencies = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
                break;

            default:
                throw new ArgumentException(
                    "field must be one of: name, description, parameters, script_type, code_format, body, output_instructions, dependencies.");
        }
    }

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

    // ── Dependency tracking ────────────────────────────────────────────────────

    private static List<string> GetScriptNames(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM scripts ORDER BY LENGTH(name) DESC";
        var names = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static readonly Regex ScriptMetadataLineRegex = new(
        @"^(?://\s*)?@scriptmcp\s+(version|name|description|type|code_format|parameters|output_instructions):\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static (Dictionary<string, string> metadata, string body) ParseScriptMetadataHeader(string content)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = content.Split('\n');
        int bodyStart = 0;
        bool foundAny = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd('\r').Trim();

            if (string.IsNullOrEmpty(trimmed))
            {
                if (foundAny)
                {
                    bodyStart = i + 1;
                    break;
                }
                continue;
            }

            var match = ScriptMetadataLineRegex.Match(trimmed);
            if (match.Success)
            {
                foundAny = true;
                var key = match.Groups[1].Value.ToLowerInvariant();
                var value = match.Groups[2].Value.Trim();
                metadata[key] = value;
                bodyStart = i + 1;
            }
            else if (!foundAny)
            {
                break;
            }
            else
            {
                bodyStart = i;
                break;
            }
        }

        var body = bodyStart < lines.Length
            ? string.Join("\n", lines.Skip(bodyStart))
            : "";

        return (metadata, body);
    }

    private static readonly Regex CallProcInvocationRegex = new(
        @"ScriptMCP\s*\.\s*(?:Call|Proc)\s*\(\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex LoadDirectiveDependencyRegex = new(
        @"#\s*load\s+""([^""]+)""",
        RegexOptions.Compiled);

    private static List<string> ExtractDependencies(Script func, IReadOnlyList<string>? knownFunctions = null)
    {
        if (string.IsNullOrWhiteSpace(func.Body) || knownFunctions == null || knownFunctions.Count == 0)
            return new List<string>();

        // Build a case-insensitive lookup that preserves canonical casing from the database
        var canonicalByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in knownFunctions)
            canonicalByName[name] = name;

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var body = func.Body;

        // 1. Detect ScriptMCP.Call("name", ...) and ScriptMCP.Proc("name", ...) invocations
        foreach (Match m in CallProcInvocationRegex.Matches(body))
        {
            var name = m.Groups[1].Value;
            if (string.Equals(name, func.Name, StringComparison.OrdinalIgnoreCase))
                continue; // skip self-reference
            if (canonicalByName.TryGetValue(name, out var canonical))
                found.Add(canonical);
        }

        // 2. Detect #load "path.cs" directives where the basename matches a known script
        foreach (Match m in LoadDirectiveDependencyRegex.Matches(body))
        {
            var path = m.Groups[1].Value;
            string baseName;
            try
            {
                baseName = Path.GetFileNameWithoutExtension(path);
            }
            catch
            {
                continue; // malformed path — skip
            }
            if (string.IsNullOrEmpty(baseName))
                continue;
            if (string.Equals(baseName, func.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (canonicalByName.TryGetValue(baseName, out var canonical))
                found.Add(canonical);
        }

        return found
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string DependenciesToCsv(List<string> deps)
        => deps.Count == 0 ? "" : string.Join(",", deps);

    private static List<string> FindDirectMutualDependencies(
        SqliteConnection conn,
        string functionName,
        IReadOnlyCollection<string> dependencies)
    {
        if (string.IsNullOrWhiteSpace(functionName) || dependencies.Count == 0)
            return new List<string>();

        return dependencies
            .Where(dep => FunctionBodyReferencesName(conn, dep, functionName))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool FunctionBodyReferencesName(SqliteConnection conn, string sourceFunctionName, string referencedFunctionName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT body FROM scripts WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", sourceFunctionName);

        var body = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(referencedFunctionName))
            return false;

        if (!body.Contains(referencedFunctionName, StringComparison.OrdinalIgnoreCase))
            return false;

        return Regex.IsMatch(
            body,
            @"(?<![A-Za-z0-9_-])" + Regex.Escape(referencedFunctionName) + @"(?![A-Za-z0-9_-])",
            RegexOptions.IgnoreCase);
    }

    private static List<string> FindDependentsOf(SqliteConnection conn, string functionName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT name FROM scripts
            WHERE dependencies = @exact
               OR dependencies LIKE @start
               OR dependencies LIKE @mid
               OR dependencies LIKE @end";
        cmd.Parameters.AddWithValue("@exact", functionName);
        cmd.Parameters.AddWithValue("@start", functionName + ",%");
        cmd.Parameters.AddWithValue("@mid", "%," + functionName + ",%");
        cmd.Parameters.AddWithValue("@end", "%," + functionName);

        var dependents = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            dependents.Add(reader.GetString(0));
        return dependents;
    }

    private static void InsertScript(SqliteConnection conn, Script func, byte[]? assemblyBytes, List<string>? externalRefs = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO scripts (name, description, parameters, script_type, code_format, body, compiled_assembly, output_instructions, dependencies, external_refs)
            VALUES (@name, @description, @parameters, @script_type, @code_format, @body, @compiled_assembly, @output_instructions, @dependencies, @external_refs)";
        cmd.Parameters.AddWithValue("@name", func.Name);
        cmd.Parameters.AddWithValue("@description", func.Description);
        cmd.Parameters.AddWithValue("@parameters", JsonSerializer.Serialize(func.Parameters));
        cmd.Parameters.AddWithValue("@script_type", func.FunctionType ?? "code");
        cmd.Parameters.AddWithValue("@code_format", (object?)func.CodeFormat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@body", func.Body);
        cmd.Parameters.AddWithValue("@compiled_assembly", (object?)assemblyBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@output_instructions", (object?)func.OutputInstructions ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dependencies", (object?)func.Dependencies ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@external_refs",
            externalRefs != null && externalRefs.Count > 0
                ? JsonSerializer.Serialize(externalRefs)
                : (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ── Database Switching ────────────────────────────────────────────────────

    private static readonly string DefaultDatabasePath = Path.Combine(
        McpConstants.GetDefaultDatabaseDirectory(),
        McpConstants.DefaultDatabaseFileName);

    [McpServerTool(Name = "get_database")]
    [Description("Returns the path of the currently active ScriptMCP database.")]
    public string GetDatabase()
    {
        return SavePath;
    }

    [McpServerTool(Name = "set_database")]
    [Description("Sets the active ScriptMCP database at runtime. Similar to the --db CLI argument but can be used during a session. If no path is provided, switches to the default database. If only a name is provided (no directory separators), it is resolved relative to the default database directory. If the database does not exist, the user must confirm creation by setting create=true.")]
    public string SetDatabase(
        [Description("Path to the SQLite database file, a database name (resolved relative to the default directory), or omit to switch to the default database")] string path = "",
        [Description("Set to true to confirm creating a new database when the file does not exist")] bool create = false)
    {
        string resolvedPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            resolvedPath = DefaultDatabasePath;
        }
        else
        {
            var trimmed = path.Trim();

            // If it's just a name with no directory separators, resolve relative to the default directory
            if (!trimmed.Contains(Path.DirectorySeparatorChar) &&
                !trimmed.Contains(Path.AltDirectorySeparatorChar))
            {
                // Append .db extension if not already present
                if (!trimmed.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                    trimmed += ".db";

                resolvedPath = Path.Combine(McpConstants.GetDefaultDatabaseDirectory(), trimmed);
            }
            else
            {
                resolvedPath = Path.GetFullPath(trimmed);
            }
        }

        var previousPath = SavePath;

        if (string.Equals(resolvedPath, previousPath, StringComparison.OrdinalIgnoreCase))
            return $"Already using database: {resolvedPath}";

        if (!File.Exists(resolvedPath) && !create)
            return $"Database does not exist:\n  {resolvedPath}\nAsk the user if they want to create it. If yes, call set_database again with create=true.";

        SavePath = resolvedPath;
        EnsureDatabase();

        return $"Switched database from:\n  {previousPath}\nto:\n  {resolvedPath}";
    }

    [McpServerTool(Name = "delete_database")]
    [Description("Deletes a ScriptMCP database file. Call with confirm=false first to validate the path and get a yes-or-no confirmation prompt. The default database cannot be deleted. If the target database is currently active, it will be switched to the default database first.")]
    public string DeleteDatabase(
        [Description("Path to the SQLite database file, or a database name (resolved relative to the default directory)")] string path,
        [Description("Must be set to true to confirm deletion. If false, returns a confirmation prompt instead.")] bool confirm = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Error: path cannot be empty.";

        var trimmed = path.Trim();

        string resolvedPath;
        if (!trimmed.Contains(Path.DirectorySeparatorChar) &&
            !trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            if (!trimmed.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                trimmed += ".db";
            resolvedPath = Path.Combine(McpConstants.GetDefaultDatabaseDirectory(), trimmed);
        }
        else
        {
            resolvedPath = Path.GetFullPath(trimmed);
        }

        if (string.Equals(resolvedPath, DefaultDatabasePath, StringComparison.OrdinalIgnoreCase))
            return "Error: the default database cannot be deleted.";

        if (!File.Exists(resolvedPath))
            return $"Error: database not found: {resolvedPath}";

        if (!confirm)
            return $"Delete this database?\n  {resolvedPath}\nSay yes or no.";

        // If deleting the currently active database, switch to default first
        if (string.Equals(resolvedPath, SavePath, StringComparison.OrdinalIgnoreCase))
        {
            SavePath = DefaultDatabasePath;
            EnsureDatabase();
        }

        SqliteConnection.ClearAllPools();
        DeleteDatabaseFileWithRetry(resolvedPath);
        return $"Deleted database: {resolvedPath}\nActive database: {SavePath}";
    }

    private static void DeleteDatabaseFileWithRetry(string path)
    {
        const int maxAttempts = 5;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(50);
                SqliteConnection.ClearAllPools();
            }
        }
    }

    [McpServerTool(Name = "search_scripts")]
    [Description("Searches stored scripts for a text string or regex pattern in script source or metadata. Use this tool only when the user explicitly asks to search inside scripts. Do not use it for normal script selection. Use searchIn to target a specific field: source (default, line-by-line body search), name, description, parameters, scripttype, codeformat, outputinstructions, dependson, externalrefs, or all.")]
    public string SearchScripts(
        [Description("Text or regex pattern to search for")] string query,
        [Description("Field to search: source (default), name, description, parameters, scripttype, codeformat, outputinstructions, dependson, externalrefs, all")] string searchIn = "source",
        [Description("If true, treat query as a case-insensitive regex pattern. Default false.")] bool regex = false)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "No search query provided.";

        System.Text.RegularExpressions.Regex? pattern = null;
        if (regex)
        {
            try
            {
                pattern = new System.Text.RegularExpressions.Regex(query,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch (Exception ex)
            {
                return $"Invalid regex: {ex.Message}";
            }
        }

        // Map user-facing names to column names; body is searched line-by-line, others as whole values
        var fieldMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"]             = "body",
            ["name"]               = "name",
            ["description"]        = "description",
            ["parameters"]         = "parameters",
            ["scripttype"]         = "script_type",
            ["codeformat"]         = "code_format",
            ["outputinstructions"] = "output_instructions",
            ["dependson"]          = "dependencies",
            ["externalrefs"]       = "external_refs",
        };

        bool searchAll = searchIn.Equals("all", StringComparison.OrdinalIgnoreCase);

        if (!searchAll && !fieldMap.ContainsKey(searchIn))
            return $"Unknown searchIn '{searchIn}'. Valid: source, name, description, parameters, scripttype, codeformat, outputinstructions, dependson, externalrefs, all";

        var results = new System.Text.StringBuilder();

        using var conn = new SqliteConnection($"Data Source={SavePath}");
        conn.Open();
        ConfigureConnection(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, body, description, parameters, script_type, code_format, output_instructions, dependencies, external_refs FROM scripts ORDER BY name;";

        bool Matches(string? value) => value != null && (
            regex ? pattern!.IsMatch(value) : value.Contains(query, StringComparison.OrdinalIgnoreCase));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var scriptName   = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var body         = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var description  = reader.IsDBNull(2) ? null : reader.GetString(2);
            var parameters   = reader.IsDBNull(3) ? null : reader.GetString(3);
            var scriptType   = reader.IsDBNull(4) ? null : reader.GetString(4);
            var codeFormat   = reader.IsDBNull(5) ? null : reader.GetString(5);
            var outputInstr  = reader.IsDBNull(6) ? null : reader.GetString(6);
            var dependencies = reader.IsDBNull(7) ? null : reader.GetString(7);
            var externalRefs = reader.IsDBNull(8) ? null : reader.GetString(8);

            bool doSource = searchAll || fieldMap[searchIn] == "body";
            bool doMeta   = searchAll || fieldMap[searchIn] != "body";

            if (doSource)
            {
                foreach (var raw in body.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (Matches(line))
                        results.AppendLine($"{scriptName}: {line.Trim()}");
                }
            }

            if (doMeta)
            {
                var metaFields = new (string label, string col, string? value)[]
                {
                    ("name",               "name",               scriptName),
                    ("description",        "description",        description),
                    ("parameters",         "parameters",         parameters),
                    ("scripttype",         "script_type",        scriptType),
                    ("codeformat",         "code_format",        codeFormat),
                    ("outputinstructions", "output_instructions",outputInstr),
                    ("dependson",          "dependencies",       dependencies),
                    ("externalrefs",       "external_refs",      externalRefs),
                };

                foreach (var (label, col, value) in metaFields)
                {
                    if (!searchAll && fieldMap[searchIn] != col) continue;
                    if (Matches(value))
                        results.AppendLine($"{scriptName} [{label}]: {value}");
                }
            }
        }

        return results.Length == 0
            ? $"No matches found for: {query}"
            : results.ToString().TrimEnd();
    }
}
