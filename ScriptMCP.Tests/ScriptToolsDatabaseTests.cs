using ScriptMCP.Library;

namespace ScriptMCP.Tests;

[Collection("ScriptTools tests")]
public sealed class ScriptToolsDatabaseTests
{
    private readonly TestDatabaseFixture _fixture;
    private readonly ScriptTools _tools;

    public ScriptToolsDatabaseTests(TestDatabaseFixture fixture)
    {
        _fixture = fixture;
        _tools = new ScriptTools();
    }

    [Fact]
    public void RegistersAndExecutesFunctionUsingDedicatedTestDatabaseFile()
    {
        var name = UniqueName("test_add_two_numbers");
        var registerResult = _tools.CreateScript(
            name: name,
            description: "Adds two integers.",
            parameters: """[{"name":"x","type":"int","description":"first"},{"name":"y","type":"int","description":"second"}]""",
            body: "Console.Write((x + y).ToString());",
            functionType: "code",
            outputInstructions: "");

        Assert.Contains("created successfully", registerResult, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(_fixture.DatabasePath));

        var listResult = _tools.ListScripts();
        Assert.Contains(name, listResult, StringComparison.Ordinal);

        var callResult = _tools.CallScript(name, """{"x":2,"y":3}""");
        Assert.Equal("5", callResult);
    }

    [Fact]
    public void InspectSupportsBasicAndFullInspectionModes()
    {
        var name = UniqueName("test_inspect");
        _tools.CreateScript(
            name: name,
            description: "Inspect me.",
            parameters: """[{"name":"x","type":"int","description":"value"}]""",
            body: "Console.Write(x.ToString());",
            functionType: "code",
            outputInstructions: "return exactly");

        var basic = _tools.InspectScript(name);
        Assert.Contains($"Script: {name}", basic, StringComparison.Ordinal);
        Assert.DoesNotContain("Compiled:", basic, StringComparison.Ordinal);
        Assert.DoesNotContain("Source (C# Code):", basic, StringComparison.Ordinal);
        Assert.Contains("Output Instructions: return exactly", basic, StringComparison.Ordinal);

        var full = _tools.InspectScript(name, fullInspection: true);
        Assert.Contains("Compiled:    Yes", full, StringComparison.Ordinal);
        Assert.Contains("Source (C# Code):", full, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateBodyRecompilesAndChangesBehavior()
    {
        var name = UniqueName("test_update");
        _tools.CreateScript(
            name: name,
            description: "Math function.",
            parameters: """[{"name":"x","type":"int","description":"first"},{"name":"y","type":"int","description":"second"}]""",
            body: "Console.Write((x + y).ToString());",
            functionType: "code",
            outputInstructions: "");

        var before = _tools.CallScript(name, """{"x":2,"y":3}""");
        Assert.Equal("5", before);

        var update = _tools.UpdateScript(name, "body", "Console.Write((x * y).ToString());");
        Assert.Contains("updated successfully: body", update, StringComparison.OrdinalIgnoreCase);

        var after = _tools.CallScript(name, """{"x":2,"y":3}""");
        Assert.Equal("6", after);
    }

    [Fact]
    public void UpdateScriptTypeToInstructionsClearsCompiledAssemblyAndKeepsNonNullCodeFormat()
    {
        var name = UniqueName("test_update_to_instructions");
        _tools.CreateScript(
            name: name,
            description: "Starts as compiled code.",
            parameters: "[]",
            body: "Console.Write(\"code-output\");",
            functionType: "code",
            outputInstructions: "");

        var update = _tools.UpdateScript(name, "script_type", "instructions");
        Assert.Contains("updated successfully: script_type", update, StringComparison.OrdinalIgnoreCase);

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_fixture.DatabasePath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT script_type, code_format, compiled_assembly FROM scripts WHERE name = @name";
            cmd.Parameters.AddWithValue("@name", name);
            using var reader = cmd.ExecuteReader();

            Assert.True(reader.Read());
            Assert.Equal("instructions", reader.GetString(0));
            Assert.False(reader.IsDBNull(1));
            Assert.Equal("", reader.GetString(1));
            Assert.True(reader.IsDBNull(2));
        }

        var inspection = _tools.InspectScript(name, fullInspection: true);
        Assert.Contains("Type:        instructions", inspection, StringComparison.Ordinal);
        Assert.Contains("Compiled:    N/A (instructions)", inspection, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistersAndExecutesTopLevelConsoleScriptUsingStdout()
    {
        var name = UniqueName("test_top_level");
        var registerResult = _tools.CreateScript(
            name: name,
            description: "Writes a greeting from top-level code.",
            parameters: """[{"name":"name","type":"string","description":"person"}]""",
            body: """
using System;

string Format(string value) => $"Hello, {value}!";

Console.Write(Format(name));
""",
            functionType: "code",
            outputInstructions: "");

        Assert.Contains("created successfully", registerResult, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name, """{"name":"Bill"}""");
        Assert.Equal("Hello, Bill!", callResult);
    }

    [Fact]
    public void TopLevelConsoleScriptReceivesRawJsonArgumentAndTypedProperties()
    {
        var name = UniqueName("test_top_level_raw_json");
        var registerResult = _tools.CreateScript(
            name: name,
            description: "Uses raw json args and typed imports.",
            parameters: """[{"name":"city","type":"string","description":"city"}]""",
            body: """
using System;

Console.Write(args[0] + "|" + city + "|" + scriptArgs["city"]);
""",
            functionType: "code",
            outputInstructions: "");

        Assert.Contains("created successfully", registerResult, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name, """{"city":"Athens"}""");
        Assert.Equal("""{"city":"Athens"}|Athens|Athens""", callResult);
    }

    [Fact]
    public void TopLevelConsoleScriptReceivesJsonPayloadAtArgsZero()
    {
        var name = UniqueName("test_top_level_args_zero");
        var registerResult = _tools.CreateScript(
            name: name,
            description: "Uses raw json payload at args zero.",
            parameters: "[]",
            body: """
using System;

Console.Write(args.Length + "|" + args[0]);
""",
            functionType: "code",
            outputInstructions: "");

        Assert.Contains("created successfully", registerResult, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name, """{"city":"Athens","units":"metric"}""");
        Assert.Equal("""1|{"city":"Athens","units":"metric"}""", callResult);
    }

    [Fact]
    public void CreateScriptRejectsLegacyMethodBodySyntax()
    {
        var name = UniqueName("test_legacy_rejected");
        var registerResult = _tools.CreateScript(
            name: name,
            description: "Legacy body should fail.",
            parameters: """[{"name":"x","type":"int","description":"value"}]""",
            body: "return x.ToString();",
            functionType: "code",
            outputInstructions: "");

        Assert.Contains("Compilation failed:", registerResult, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupMigratesExistingLegacyCodeScriptToTopLevel()
    {
        var name = UniqueName("test_startup_migration");

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_fixture.DatabasePath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO scripts (name, description, parameters, script_type, body, compiled_assembly, output_instructions, dependencies, code_format)
                VALUES (@name, @description, @parameters, 'code', @body, NULL, NULL, '', 'legacy_method_body')";
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@description", "Legacy body awaiting migration.");
            cmd.Parameters.AddWithValue("@parameters", """[{"name":"x","type":"int","description":"value"}]""");
            cmd.Parameters.AddWithValue("@body", "return x.ToString();");
            cmd.ExecuteNonQuery();
        }

        ResetScriptToolsInitialization();
        var migratedTools = new ScriptTools();

        var callResult = migratedTools.CallScript(name, """{"x":7}""");
        Assert.Equal("7", callResult);

        var inspection = migratedTools.InspectScript(name, fullInspection: true);
        Assert.Contains("Code Format: top_level", inspection, StringComparison.Ordinal);
        Assert.Contains("Console.Write(__scriptmcpResult);", inspection, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadScriptCreatesNewScriptFromFile()
    {
        var name = UniqueName("test_load_create");
        var sourcePath = Path.Combine(_fixture.TestDataDirectory, $"{name}.cs");
        File.WriteAllText(sourcePath, "Console.Write(\"loaded-create\");");

        var result = _tools.LoadScript(sourcePath, name: name);
        Assert.Contains("loaded from", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("created", result, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name, "{}");
        Assert.Equal("loaded-create", callResult);
    }

    [Fact]
    public void LoadScriptUpdatesExistingScriptFromFileAndPreservesMetadata()
    {
        var name = UniqueName("test_load_update");
        var sourcePath = Path.Combine(_fixture.TestDataDirectory, $"{name}.cs");

        Assert.Contains("created successfully", _tools.CreateScript(
            name: name,
            description: "Original description",
            parameters: """[{"name":"city","type":"string","description":"City name"}]""",
            body: "Console.Write(city);",
            functionType: "code",
            outputInstructions: "return exactly"), StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(sourcePath, "Console.Write(city.ToUpperInvariant());");

        var result = _tools.LoadScript(sourcePath, name: name);
        Assert.Contains("loaded from", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("updated", result, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name, """{"city":"athens"}""");
        Assert.Contains("ATHENS", callResult, StringComparison.Ordinal);

        var inspection = _tools.InspectScript(name);
        Assert.Contains("Original description", inspection, StringComparison.Ordinal);
        Assert.Contains("city (string): City name", inspection, StringComparison.Ordinal);
        Assert.Contains("Output Instructions: return exactly", inspection, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportScriptWritesCodeScriptToCsFile()
    {
        var name = UniqueName("test_export_code");
        var exportPath = Path.Combine(_fixture.TestDataDirectory, $"{name}.cs");

        Assert.Contains("created successfully", _tools.CreateScript(
            name: name,
            description: "Export me.",
            parameters: "[]",
            body: "Console.Write(\"exported\");",
            functionType: "code",
            outputInstructions: ""), StringComparison.OrdinalIgnoreCase);

        var result = _tools.ExportScript(name, exportPath);
        Assert.Contains("exported to", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(exportPath));

        var content = File.ReadAllText(exportPath);
        Assert.Contains("@scriptmcp name: " + name, content, StringComparison.Ordinal);
        Assert.Contains("@scriptmcp description: Export me.", content, StringComparison.Ordinal);
        Assert.Contains("@scriptmcp type: code", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@scriptmcp version:", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Console.Write(\"exported\");", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportScriptUsesDefaultExtensionForInstructionsScript()
    {
        var name = UniqueName("test_export_instructions");
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_fixture.TestDataDirectory);

        try
        {
            Assert.Contains("created successfully", _tools.CreateScript(
                name: name,
                description: "Instruction export.",
                parameters: "[]",
                body: "Do the thing.",
                functionType: "instructions",
                outputInstructions: ""), StringComparison.OrdinalIgnoreCase);

            var result = _tools.ExportScript(name);
            var exportPath = Path.Combine(_fixture.TestDataDirectory, $"{name}.txt");

            Assert.Contains("exported to", result, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(exportPath));

            var content = File.ReadAllText(exportPath);
            Assert.Contains("@scriptmcp name: " + name, content, StringComparison.Ordinal);
            Assert.Contains("@scriptmcp type: instructions", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Do the thing.", content, StringComparison.Ordinal);
            Assert.DoesNotContain("//", content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Fact]
    public void CreateLibraryCodeScriptCompilesWithoutEntryPointAndCannotBeCalledDirectly()
    {
        var name = UniqueName("test_library_code");

        var result = _tools.CreateScript(
            name: name,
            description: "Reusable library helper.",
            parameters: "[]",
            body: """
public static class LibraryHelper
{
    public static string Message => "hello from library";
}
""",
            functionType: "code",
            outputInstructions: "",
            codeFormat: "library");

        Assert.Contains("created successfully", result, StringComparison.OrdinalIgnoreCase);

        var inspection = _tools.InspectScript(name, fullInspection: true);
        Assert.Contains("Type:        code", inspection, StringComparison.Ordinal);
        Assert.Contains("Code Format: library", inspection, StringComparison.Ordinal);
        Assert.Contains("Compiled:    Yes", inspection, StringComparison.Ordinal);

        var callResult = _tools.CallScript(name);
        Assert.Equal($"Script '{name}' is a library code script and cannot be executed directly. Load it from another script with #load.", callResult);
    }

    [Fact]
    public void LibraryCodeScriptCanBeLoadedFromDatabase()
    {
        var helperName = UniqueName("test_library_helper");
        var consumerName = UniqueName("test_library_consumer");

        Assert.Contains("created successfully", _tools.CreateScript(
            name: helperName,
            description: "Shared library helper.",
            parameters: "[]",
            body: """
public static class LoadedLibraryHelper
{
    public static string Message => "library-loaded";
    public static string Format(string name) => $"{Message}:{name}";
}
""",
            functionType: "code",
            outputInstructions: "",
            codeFormat: "library"), StringComparison.OrdinalIgnoreCase);

        Assert.Contains("created successfully", _tools.CreateScript(
            name: consumerName,
            description: "Consumes the library helper.",
            parameters: "[]",
            body: $"""
                #load "{helperName}"

                Console.Write(LoadedLibraryHelper.Format("ok"));
                """,
            functionType: "code",
            outputInstructions: ""), StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(consumerName);
        Assert.Equal("library-loaded:ok", callResult);
    }

    [Fact]
    public void ExportLibraryCodeScriptUsesCsxAndIncludesCodeFormatMetadata()
    {
        var name = UniqueName("test_export_library");
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_fixture.TestDataDirectory);

        try
        {
            Assert.Contains("created successfully", _tools.CreateScript(
                name: name,
                description: "Library export.",
                parameters: "[]",
                body: "public static class ExportedLibrary { public static string Value => \"x\"; }",
                functionType: "code",
                outputInstructions: "",
                codeFormat: "library"), StringComparison.OrdinalIgnoreCase);

            var result = _tools.ExportScript(name);
            var exportPath = Path.Combine(_fixture.TestDataDirectory, $"{name}.csx");

            Assert.Contains("exported to", result, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(exportPath));

            var content = File.ReadAllText(exportPath);
            Assert.Contains("@scriptmcp type: code", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("@scriptmcp code_format: library", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Fact]
    public void CompileScriptExportsAssemblyToDllFile()
    {
        var name = UniqueName("test_compile_export");
        var dllPath = Path.Combine(_fixture.TestDataDirectory, $"{name}.dll");

        Assert.Contains("created successfully", _tools.CreateScript(
            name: name,
            description: "Compile me.",
            parameters: "[]",
            body: "Console.Write(\"compiled\");",
            functionType: "code",
            outputInstructions: ""), StringComparison.OrdinalIgnoreCase);

        var result = _tools.CompileScript(name, dllPath);
        Assert.Contains("compiled and exported to", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(dllPath));
        Assert.True(new FileInfo(dllPath).Length > 0);
    }

    [Fact]
    public void CompileScriptRejectsInstructionsScript()
    {
        var name = UniqueName("test_compile_instructions");

        Assert.Contains("created successfully", _tools.CreateScript(
            name: name,
            description: "Instructions only.",
            parameters: "[]",
            body: "Follow the process.",
            functionType: "instructions",
            outputInstructions: ""), StringComparison.OrdinalIgnoreCase);

        var result = _tools.CompileScript(name);
        Assert.Contains("is an instructions script", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterRejectsDirectCircularDependency()
    {
        var first = UniqueName("test_cycle_first");
        var second = UniqueName("test_cycle_second");

        var firstRegister = _tools.CreateScript(
            name: first,
            description: "Calls the second function.",
            parameters: "[]",
            body: $$"""Console.Write(ScriptMCP.Call("{{second}}"));""",
            functionType: "code",
            outputInstructions: "");
        Assert.Contains("created successfully", firstRegister, StringComparison.OrdinalIgnoreCase);

        var secondRegister = _tools.CreateScript(
            name: second,
            description: "Calls the first function.",
            parameters: "[]",
            body: $$"""Console.Write(ScriptMCP.Call("{{first}}"));""",
            functionType: "code",
            outputInstructions: "");

        Assert.Contains("Creation failed: direct circular dependency detected", secondRegister, StringComparison.Ordinal);
        Assert.Contains($"{second} <-> {first}", secondRegister, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateBodyRejectsDirectCircularDependency()
    {
        var first = UniqueName("test_cycle_update_first");
        var second = UniqueName("test_cycle_update_second");

        var firstRegister = _tools.CreateScript(
            name: first,
            description: "Calls the second function.",
            parameters: "[]",
            body: $$"""Console.Write(ScriptMCP.Call("{{second}}"));""",
            functionType: "code",
            outputInstructions: "");
        Assert.Contains("created successfully", firstRegister, StringComparison.OrdinalIgnoreCase);

        var secondRegister = _tools.CreateScript(
            name: second,
            description: "Does not call anything.",
            parameters: "[]",
            body: """Console.Write("ok");""",
            functionType: "code",
            outputInstructions: "");
        Assert.Contains("created successfully", secondRegister, StringComparison.OrdinalIgnoreCase);

        var update = _tools.UpdateScript(
            second,
            "body",
            $$"""Console.Write(ScriptMCP.Call("{{first}}"));""");

        Assert.Contains("Update failed: direct circular dependency detected", update, StringComparison.Ordinal);
        Assert.Contains($"{second} <-> {first}", update, StringComparison.Ordinal);

        var inspect = _tools.InspectScript(second);
        Assert.Contains("Depends on:  (none)", inspect, StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyDetectedForScriptMcpCall()
    {
        var target = UniqueName("test_dep_target_call");
        var caller = UniqueName("test_dep_caller_call");

        _tools.CreateScript(target, "target", "[]", "Console.Write(\"ok\");", "code", "");
        _tools.CreateScript(
            caller,
            "caller",
            "[]",
            $$"""Console.Write(ScriptMCP.Call("{{target}}", "{}"));""",
            "code",
            "");

        var inspect = _tools.InspectScript(caller);
        Assert.Contains($"Depends on:  {target}", inspect, StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyDetectedForScriptMcpProc()
    {
        var target = UniqueName("test_dep_target_proc");
        var caller = UniqueName("test_dep_caller_proc");

        _tools.CreateScript(target, "target", "[]", "Console.Write(\"ok\");", "code", "");
        _tools.CreateScript(
            caller,
            "caller",
            "[]",
            $$"""var p = ScriptMCP.Proc("{{target}}", "{}"); p.WaitForExit(); Console.Write(p.StandardOutput.ReadToEnd());""",
            "code",
            "");

        var inspect = _tools.InspectScript(caller);
        Assert.Contains($"Depends on:  {target}", inspect, StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyNotDetectedForMereNameMentionInComment()
    {
        var target = UniqueName("test_dep_target_comment");
        var caller = UniqueName("test_dep_caller_comment");

        _tools.CreateScript(target, "target", "[]", "Console.Write(\"ok\");", "code", "");
        _tools.CreateScript(
            caller,
            "caller",
            "[]",
            $"// This script is similar to {target} but does its own thing\nConsole.Write(\"hello\");",
            "code",
            "");

        var inspect = _tools.InspectScript(caller);
        Assert.Contains("Depends on:  (none)", inspect, StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyNotDetectedForMereNameMentionInStringLiteral()
    {
        var target = UniqueName("test_dep_target_string");
        var caller = UniqueName("test_dep_caller_string");

        _tools.CreateScript(target, "target", "[]", "Console.Write(\"ok\");", "code", "");
        _tools.CreateScript(
            caller,
            "caller",
            "[]",
            $"Console.Write(\"See {target} for reference\");",
            "code",
            "");

        var inspect = _tools.InspectScript(caller);
        Assert.Contains("Depends on:  (none)", inspect, StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyDetectedForLoadDirectiveMatchingKnownScriptBasename()
    {
        var target = UniqueName("test_dep_target_load");
        var caller = UniqueName("test_dep_caller_load");

        _tools.CreateScript(target, "target", "[]", "Console.Write(\"ok\");", "code", "");

        // Write a file whose basename matches the target script name so #load resolves at compile time.
        var loadFile = Path.Combine(Path.GetTempPath(), $"{target}.cs");
        File.WriteAllText(loadFile, "// placeholder\n");
        try
        {
            _tools.CreateScript(
                caller,
                "caller",
                "[]",
                $"#load \"{loadFile.Replace("\\", "\\\\")}\"\nConsole.Write(\"hello\");",
                "code",
                "");

            var inspect = _tools.InspectScript(caller);
            Assert.Contains($"Depends on:  {target}", inspect, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(loadFile)) File.Delete(loadFile);
        }
    }

    [Fact]
    public void UpdateWithInvalidFieldReturnsError()
    {
        var name = UniqueName("test_update_error");
        _tools.CreateScript(
            name: name,
            description: "No-op",
            parameters: "[]",
            body: "Console.Write(\"ok\");",
            functionType: "code",
            outputInstructions: "");

        var result = _tools.UpdateScript(name, "not_a_field", "x");
        Assert.Contains("Update failed:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void CallScriptAppendsOutputInstructionsSuffix()
    {
        var name = UniqueName("test_output_instructions");
        _tools.CreateScript(
            name: name,
            description: "Has output instructions.",
            parameters: "[]",
            body: "Console.Write(\"payload\");",
            functionType: "code",
            outputInstructions: "render as markdown table");

        var output = _tools.CallScript(name, "{}");
        Assert.Contains("payload", output, StringComparison.Ordinal);
        Assert.Contains("[Output Instructions]: render as markdown table", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CallProcessRejectsUnknownOutputMode()
    {
        var result = _tools.CallProcess("anything", "{}", "BogusMode");
        Assert.Equal("Error: invalid output_mode. Supported values: Default, WriteNew, WriteAppend, WriteRewrite.", result);
    }

    [Fact]
    public void ReadScheduledTaskReturnsMostRecentOutputPreferringAppendWhenNewer()
    {
        ResetOutputDirectory();
        var name = UniqueName("test_read_scheduled");

        var timestampPath = ScriptTools.GetScheduledTaskOutputPath(name, new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(timestampPath, "timestamp-result");
        File.SetLastWriteTimeUtc(timestampPath, new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc));

        var appendPath = ScriptTools.GetScheduledTaskAppendOutputPath(name);
        File.WriteAllText(appendPath, "append-result");
        File.SetLastWriteTimeUtc(appendPath, new DateTime(2026, 01, 02, 0, 0, 0, DateTimeKind.Utc));

        var result = _tools.ReadScheduledTask(name);
        Assert.Equal("append-result", result);
    }

    [Fact]
    public void ReadScheduledTaskReturnsNotFoundMessageWhenNoFilesExist()
    {
        ResetOutputDirectory();
        var name = UniqueName("test_read_missing");

        var result = _tools.ReadScheduledTask(name);
        Assert.Equal("(empty)", result);
    }

    [Fact]
    public void GetDatabaseReturnsCurrentSavePath()
    {
        Assert.Equal(ScriptTools.SavePath, _tools.GetDatabase());
    }

    [Fact]
    public void SetDatabaseRejectsCreatingMissingDatabaseWithoutConfirmation()
    {
        var originalPath = ScriptTools.SavePath;
        var databaseName = UniqueName("missing-db");
        var expectedPath = GetDefaultDatabasePath(databaseName);

        try
        {
            if (File.Exists(expectedPath))
                File.Delete(expectedPath);

            var result = _tools.SetDatabase(databaseName);

            Assert.Contains("Database does not exist:", result, StringComparison.Ordinal);
            Assert.Contains(expectedPath, result, StringComparison.Ordinal);
            Assert.Equal(originalPath, ScriptTools.SavePath);
            Assert.False(File.Exists(expectedPath));
        }
        finally
        {
            RestoreAndDeleteDatabase(originalPath, expectedPath);
        }
    }

    [Fact]
    public void SetDatabaseCreatesAndSwitchesToNamedDatabaseWhenConfirmed()
    {
        var originalPath = ScriptTools.SavePath;
        var databaseName = UniqueName("created-db");
        var expectedPath = GetDefaultDatabasePath(databaseName);

        try
        {
            if (File.Exists(expectedPath))
                File.Delete(expectedPath);

            var result = _tools.SetDatabase(databaseName, create: true);

            Assert.Contains("Switched database from:", result, StringComparison.Ordinal);
            Assert.Contains(expectedPath, result, StringComparison.Ordinal);
            Assert.Equal(expectedPath, ScriptTools.SavePath);
            Assert.True(File.Exists(expectedPath));
            Assert.Equal(expectedPath, _tools.GetDatabase());
        }
        finally
        {
            RestoreAndDeleteDatabase(originalPath, expectedPath);
        }
    }

    [Fact]
    public void DeleteDatabaseRequiresConfirmationBeforeDeleting()
    {
        var originalPath = ScriptTools.SavePath;
        var databaseName = UniqueName("delete-db");
        var databasePath = GetDefaultDatabasePath(databaseName);

        try
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);

            Assert.Contains("Switched database from:", _tools.SetDatabase(databaseName, create: true), StringComparison.Ordinal);
            Assert.True(File.Exists(databasePath));

            var result = _tools.DeleteDatabase(databaseName);

            Assert.Contains("Delete this database?", result, StringComparison.Ordinal);
            Assert.Contains(databasePath, result, StringComparison.Ordinal);
            Assert.Contains("Say yes or no.", result, StringComparison.Ordinal);
            Assert.True(File.Exists(databasePath));
            Assert.Equal(databasePath, ScriptTools.SavePath);
        }
        finally
        {
            RestoreAndDeleteDatabase(originalPath, databasePath);
        }
    }

    [Fact]
    public void DeleteDatabaseDeletesActiveDatabaseAndSwitchesToDefault()
    {
        var originalPath = ScriptTools.SavePath;
        var databaseName = UniqueName("active-delete-db");
        var databasePath = GetDefaultDatabasePath(databaseName);
        var defaultPath = GetDefaultDatabasePath(McpConstants.DefaultDatabaseFileName);

        try
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);

            Assert.Contains("Switched database from:", _tools.SetDatabase(databaseName, create: true), StringComparison.Ordinal);
            Assert.Equal(databasePath, ScriptTools.SavePath);

            var result = _tools.DeleteDatabase(databaseName, confirm: true);

            Assert.Contains($"Deleted database: {databasePath}", result, StringComparison.Ordinal);
            Assert.Contains($"Active database: {defaultPath}", result, StringComparison.Ordinal);
            Assert.False(File.Exists(databasePath));
            Assert.Equal(defaultPath, ScriptTools.SavePath);
            Assert.True(File.Exists(defaultPath));
        }
        finally
        {
            RestoreAndDeleteDatabase(originalPath, databasePath);
        }
    }

    [Fact]
    public void DeleteDatabaseRejectsDeletingDefaultDatabase()
    {
        var defaultPath = GetDefaultDatabasePath(McpConstants.DefaultDatabaseFileName);
        var originalPath = ScriptTools.SavePath;

        var result = _tools.DeleteDatabase(defaultPath);

        Assert.Equal("Error: the default database cannot be deleted.", result);
        Assert.Equal(originalPath, ScriptTools.SavePath);
    }

    [Fact]
    public void DeleteDatabaseChecksExistenceBeforePromptingForConfirmation()
    {
        var originalPath = ScriptTools.SavePath;
        var databaseName = UniqueName("missing-delete-db");
        var databasePath = GetDefaultDatabasePath(databaseName);

        try
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);

            var result = _tools.DeleteDatabase(databaseName);

            Assert.Equal($"Error: database not found: {databasePath}", result);
            Assert.DoesNotContain("Say yes or no.", result, StringComparison.Ordinal);
            Assert.Equal(originalPath, ScriptTools.SavePath);
        }
        finally
        {
            RestoreAndDeleteDatabase(originalPath, databasePath);
        }
    }

    private static string UniqueName(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private static string GetDefaultDatabasePath(string pathOrName)
    {
        var trimmed = pathOrName.Trim();
        if (!trimmed.Contains(Path.DirectorySeparatorChar) &&
            !trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            if (!trimmed.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                trimmed += ".db";
            return Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScriptMCP",
                trimmed));
        }

        return Path.GetFullPath(trimmed);
    }

    private static void ResetScriptToolsInitialization()
    {
        var initializedField = typeof(ScriptTools).GetField("_initialized", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        initializedField!.SetValue(null, false);
    }

    private void RestoreAndDeleteDatabase(string originalPath, string databasePath)
    {
        if (!string.Equals(ScriptTools.SavePath, originalPath, StringComparison.OrdinalIgnoreCase))
            _tools.SetDatabase(originalPath);

        ScriptTools.SavePath = originalPath;

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (!File.Exists(databasePath))
            return;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Delete(databasePath);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
        }
    }

    private void ResetOutputDirectory()
    {
        if (Directory.Exists(_fixture.OutputDirectory))
            Directory.Delete(_fixture.OutputDirectory, recursive: true);
    }

    // ── #r and #load directive tests ─────────────────────────────────────────

    private string BuildTestHelperDll()
    {
        var dllDir = Path.Combine(_fixture.TestDataDirectory, "dlls");
        Directory.CreateDirectory(dllDir);
        var dllPath = Path.Combine(dllDir, "DirectiveTestHelper.dll");

        if (File.Exists(dllPath)) return dllPath;

        var source = """
            namespace DirectiveTestHelper;
            public static class Greeter
            {
                public static string Hello(string name) => $"Hello from DLL, {name}!";
            }
            """;

        var syntaxTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);
        var refs = new List<Microsoft.CodeAnalysis.MetadataReference>();
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        refs.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")));
        refs.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Private.CoreLib.dll")));

        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "DirectiveTestHelper",
            new[] { syntaxTree },
            refs,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);
        Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics));
        File.WriteAllBytes(dllPath, ms.ToArray());
        return dllPath;
    }

    [Fact]
    public void RDirective_ReferencesExternalDll()
    {
        var dllPath = BuildTestHelperDll();
        var name = UniqueName("test_r_directive");

        var body = $"""
            #r "{dllPath.Replace("\\", "\\\\")}"

            Console.Write(DirectiveTestHelper.Greeter.Hello("World"));
            """;

        var result = _tools.CreateScript(
            name: name,
            description: "Tests #r directive",
            body: body,
            functionType: "code",
            parameters: "[]",
            outputInstructions: "");

        Assert.Contains("created successfully", result, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name);
        Assert.Equal("Hello from DLL, World!", callResult);
    }

    [Fact]
    public void LoadDirective_IncludesFileSource()
    {
        var helperPath = Path.Combine(_fixture.TestDataDirectory, "load_helper.cs");
        File.WriteAllText(helperPath, "public static class LoadHelper { public static string LoadedGreeting() => \"Hello from #load!\"; }");

        var name = UniqueName("test_load_directive");

        var body = $"""
            #load "{helperPath.Replace("\\", "\\\\")}"

            Console.Write(LoadHelper.LoadedGreeting());
            """;

        var result = _tools.CreateScript(
            name: name,
            description: "Tests #load directive",
            body: body,
            functionType: "code",
            parameters: "[]",
            outputInstructions: "");

        Assert.Contains("created successfully", result, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name);
        Assert.Equal("Hello from #load!", callResult);
    }

    [Fact]
    public void LoadDirective_ExtensionlessNameLoadsScriptFromDatabase()
    {
        var helperName = UniqueName("test_load_db_helper");
        var consumerName = UniqueName("test_load_db_consumer");

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_fixture.DatabasePath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO scripts (name, description, parameters, script_type, body, compiled_assembly, output_instructions, dependencies, code_format)
                VALUES (@name, @description, @parameters, 'code', @body, NULL, NULL, '', 'top_level')";
            cmd.Parameters.AddWithValue("@name", helperName);
            cmd.Parameters.AddWithValue("@description", "Shared helper loaded from the database.");
            cmd.Parameters.AddWithValue("@parameters", "[]");
            cmd.Parameters.AddWithValue("@body", """
public static class DbLoadHelper
{
    public static string LoadedGreeting() => "Hello from db #load!";
}
""");
            cmd.ExecuteNonQuery();
        }

        var body = $$"""
            #load "{{helperName}}"

            Console.Write(DbLoadHelper.LoadedGreeting());
            """;

        var result = _tools.CreateScript(
            name: consumerName,
            description: "Tests extensionless database #load.",
            body: body,
            functionType: "code",
            parameters: "[]",
            outputInstructions: "");

        Assert.Contains("created successfully", result, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(consumerName);
        Assert.Equal("Hello from db #load!", callResult);
    }

    [Fact]
    public void LoadDirective_ExtensionlessMissingScriptMentionsActiveDatabase()
    {
        var name = UniqueName("test_load_missing_db");
        var missingScript = UniqueName("missing_db_script");

        var body = $$"""
            #load "{{missingScript}}"

            Console.Write("should not get here");
            """;

        var result = _tools.CreateScript(
            name: name,
            description: "Tests missing extensionless database #load.",
            body: body,
            functionType: "code",
            parameters: "[]",
            outputInstructions: "");

        Assert.Contains($"script '{missingScript}' was not found in the active database", result, StringComparison.Ordinal);
        Assert.Contains("Extensionless #load targets are resolved from the database", result, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadDirective_DetectsCircularReference()
    {
        var fileA = Path.Combine(_fixture.TestDataDirectory, "circular_a.cs");
        var fileB = Path.Combine(_fixture.TestDataDirectory, "circular_b.cs");
        File.WriteAllText(fileA, $"#load \"{fileB.Replace("\\", "\\\\")}\"\n");
        File.WriteAllText(fileB, $"#load \"{fileA.Replace("\\", "\\\\")}\"\n");

        var name = UniqueName("test_circular");

        var body = $"""
            #load "{fileA.Replace("\\", "\\\\")}"

            Console.Write("should not get here");
            """;

        var result = _tools.CreateScript(
            name: name,
            description: "Tests circular #load detection",
            body: body,
            functionType: "code",
            parameters: "[]",
            outputInstructions: "");

        Assert.Contains("circular reference detected", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadDirective_ErrorsOnMissingFile()
    {
        var name = UniqueName("test_load_missing");

        var body = """
            #load "nonexistent_file_12345.cs"

            Console.Write("should not get here");
            """;

        var result = _tools.CreateScript(
            name: name,
            description: "Tests #load missing file",
            body: body,
            functionType: "code",
            parameters: "[]",
            outputInstructions: "");

        Assert.Contains("file not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RDirective_RejectsNuGetReference()
    {
        var name = UniqueName("test_nuget_reject");

        var body = """
            #r "nuget: Newtonsoft.Json, 13.0.3"

            Console.Write("should not get here");
            """;

        var result = _tools.CreateScript(
            name: name,
            description: "Tests nuget rejection",
            body: body,
            functionType: "code",
            parameters: "[]",
            outputInstructions: "");

        Assert.Contains("nuget", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not supported", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RDirective_ErrorsOnMissingDll()
    {
        var name = UniqueName("test_r_missing");

        var body = """
            #r "nonexistent_library_12345.dll"

            Console.Write("should not get here");
            """;

        var result = _tools.CreateScript(
            name: name,
            description: "Tests #r missing dll",
            body: body,
            functionType: "code",
            parameters: "[]",
            outputInstructions: "");

        Assert.Contains("file not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RDirective_WorksWithForwardSlashes()
    {
        var dllPath = BuildTestHelperDll();
        var forwardSlashPath = dllPath.Replace("\\", "/");
        var name = UniqueName("test_r_fwd_slash");

        var body = $"""
            #r "{forwardSlashPath}"

            Console.Write(DirectiveTestHelper.Greeter.Hello("Forward"));
            """;

        var result = _tools.CreateScript(
            name: name,
            description: "Tests #r with forward slashes",
            body: body,
            functionType: "code",
            parameters: "[]",
            outputInstructions: "");

        Assert.Contains("created successfully", result, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name);
        Assert.Equal("Hello from DLL, Forward!", callResult);
    }

    [Fact]
    public void RAndLoadDirectives_WorkTogether()
    {
        var dllPath = BuildTestHelperDll();
        var helperPath = Path.Combine(_fixture.TestDataDirectory, "combo_helper.cs");
        File.WriteAllText(helperPath, "public static class ComboHelper { public static string Format(string s) => $\"[{s}]\"; }");

        var name = UniqueName("test_r_and_load");

        var body = $"""
            #r "{dllPath.Replace("\\", "\\\\")}"
            #load "{helperPath.Replace("\\", "\\\\")}"

            var greeting = DirectiveTestHelper.Greeter.Hello("Combo");
            Console.Write(ComboHelper.Format(greeting));
            """;

        var result = _tools.CreateScript(
            name: name,
            description: "Tests #r and #load together",
            body: body,
            functionType: "code",
            parameters: "[]",
            outputInstructions: "");

        Assert.Contains("created successfully", result, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name);
        Assert.Equal("[Hello from DLL, Combo!]", callResult);
    }

    [Fact]
    public void RAndLoadDirectives_WorkWithCrLfLineEndings()
    {
        var dllPath = BuildTestHelperDll();
        var name = UniqueName("test_r_crlf");

        // Simulate MCP transport which may use \r\n
        var body = "#r \"" + dllPath.Replace("\\", "\\\\") + "\"\r\n\r\nConsole.Write(DirectiveTestHelper.Greeter.Hello(\"CRLF\"));";

        var result = _tools.CreateScript(
            name: name,
            description: "Tests #r with CRLF line endings",
            body: body,
            functionType: "code",
            parameters: "[]",
            outputInstructions: "");

        Assert.Contains("created successfully", result, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name);
        Assert.Equal("Hello from DLL, CRLF!", callResult);
    }

    // ── Export/Load metadata round-trip tests ────────────────────────────────

    [Fact]
    public void ExportScriptIncludesMetadataHeader()
    {
        var name = UniqueName("test_export_meta");
        var exportPath = Path.Combine(_fixture.TestDataDirectory, $"{name}.cs");

        Assert.Contains("created successfully", _tools.CreateScript(
            name: name,
            description: "A test script with metadata",
            parameters: """[{"name":"x","type":"int","description":"value"}]""",
            body: "Console.Write(x);",
            functionType: "code",
            outputInstructions: "return exactly"), StringComparison.OrdinalIgnoreCase);

        _tools.ExportScript(name, exportPath);
        var content = File.ReadAllText(exportPath);

        Assert.Contains("// @scriptmcp version:", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("// @scriptmcp name: " + name, content, StringComparison.Ordinal);
        Assert.Contains("// @scriptmcp description: A test script with metadata", content, StringComparison.Ordinal);
        Assert.Contains("// @scriptmcp type: code", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("// @scriptmcp parameters:", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("// @scriptmcp output_instructions: return exactly", content, StringComparison.Ordinal);
        Assert.Contains("Console.Write(x);", content, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadScriptParsesMetadataHeaderFromExportedFile()
    {
        var originalName = UniqueName("test_roundtrip_orig");
        var exportPath = Path.Combine(_fixture.TestDataDirectory, $"{originalName}.cs");

        Assert.Contains("created successfully", _tools.CreateScript(
            name: originalName,
            description: "Round-trip description",
            parameters: """[{"name":"msg","type":"string","description":"message"}]""",
            body: "Console.Write(msg);",
            functionType: "code",
            outputInstructions: "return exactly"), StringComparison.OrdinalIgnoreCase);

        _tools.ExportScript(originalName, exportPath);
        _tools.DeleteScript(originalName, forced: true);

        var loadedName = UniqueName("test_roundtrip_loaded");
        var result = _tools.LoadScript(exportPath, name: loadedName);
        Assert.Contains("created", result, StringComparison.OrdinalIgnoreCase);

        var inspection = _tools.InspectScript(loadedName);
        Assert.Contains("Round-trip description", inspection, StringComparison.Ordinal);
        Assert.Contains("msg (string): message", inspection, StringComparison.Ordinal);
        Assert.Contains("Output Instructions: return exactly", inspection, StringComparison.Ordinal);

        var callResult = _tools.CallScript(loadedName, """{"msg":"hello"}""");
        Assert.StartsWith("hello", callResult);
    }

    [Fact]
    public void LoadScriptUsesHeaderNameWhenNoNameParameterProvided()
    {
        var headerName = UniqueName("test_header_name");
        var sourcePath = Path.Combine(_fixture.TestDataDirectory, "arbitrary_filename.cs");

        var fileContent = $"""
            // @scriptmcp name: {headerName}
            // @scriptmcp description: Created from header name
            // @scriptmcp type: code
            // @scriptmcp parameters: []

            Console.Write("from-header");
            """;
        File.WriteAllText(sourcePath, fileContent);

        var result = _tools.LoadScript(sourcePath);
        Assert.Contains("created", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(headerName, result, StringComparison.Ordinal);

        var callResult = _tools.CallScript(headerName, "{}");
        Assert.Equal("from-header", callResult);
    }

    [Fact]
    public void LoadScriptExplicitParamsOverrideHeader()
    {
        var name = UniqueName("test_override");
        var sourcePath = Path.Combine(_fixture.TestDataDirectory, $"{name}.cs");

        var fileContent = $"""
            // @scriptmcp name: {name}
            // @scriptmcp description: Header description
            // @scriptmcp type: code
            // @scriptmcp parameters: []

            Console.Write("override-test");
            """;
        File.WriteAllText(sourcePath, fileContent);

        var result = _tools.LoadScript(sourcePath, name: name, description: "Explicit description");
        Assert.Contains("created", result, StringComparison.OrdinalIgnoreCase);

        var inspection = _tools.InspectScript(name);
        Assert.Contains("Explicit description", inspection, StringComparison.Ordinal);
        Assert.DoesNotContain("Header description", inspection, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadScriptWithoutHeaderWorksAsPlainBody()
    {
        var name = UniqueName("test_no_header");
        var sourcePath = Path.Combine(_fixture.TestDataDirectory, $"{name}.cs");
        File.WriteAllText(sourcePath, "Console.Write(\"plain\");");

        var result = _tools.LoadScript(sourcePath, name: name);
        Assert.Contains("created", result, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name, "{}");
        Assert.Equal("plain", callResult);
    }

    [Fact]
    public void InstructionsScriptRoundTripsWithoutCommentPrefix()
    {
        var name = UniqueName("test_instr_roundtrip");
        var exportPath = Path.Combine(_fixture.TestDataDirectory, $"{name}.txt");

        Assert.Contains("created successfully", _tools.CreateScript(
            name: name,
            description: "Instruction round-trip",
            parameters: "[]",
            body: "Follow these steps carefully.",
            functionType: "instructions",
            outputInstructions: ""), StringComparison.OrdinalIgnoreCase);

        _tools.ExportScript(name, exportPath);
        var content = File.ReadAllText(exportPath);

        Assert.Contains("@scriptmcp name: " + name, content, StringComparison.Ordinal);
        Assert.DoesNotContain("//", content, StringComparison.Ordinal);

        _tools.DeleteScript(name, forced: true);

        var result = _tools.LoadScript(exportPath);
        Assert.Contains("created", result, StringComparison.OrdinalIgnoreCase);

        var inspection = _tools.InspectScript(name);
        Assert.Contains("Instruction round-trip", inspection, StringComparison.Ordinal);
        Assert.Contains("Type:        instructions", inspection, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadScriptPreservesBodyWithScriptmcpMentionInCode()
    {
        var name = UniqueName("test_false_header");
        var sourcePath = Path.Combine(_fixture.TestDataDirectory, $"{name}.cs");

        // A comment that mentions @scriptmcp but is not a valid header key — must not be stripped
        File.WriteAllText(sourcePath, """
            // @scriptmcp was created by Bill
            Console.Write("safe");
            """);

        var result = _tools.LoadScript(sourcePath, name: name);
        Assert.Contains("created", result, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name, "{}");
        Assert.Equal("safe", callResult);
    }

    [Fact]
    public void LoadScriptHandlesEmptyFile()
    {
        var name = UniqueName("test_empty_file");
        var sourcePath = Path.Combine(_fixture.TestDataDirectory, $"{name}.txt");
        File.WriteAllText(sourcePath, "");

        var result = _tools.LoadScript(sourcePath, name: name, scriptType: "instructions");
        Assert.Contains("created", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadScriptHandlesLeadingBlankLinesBeforeCode()
    {
        var name = UniqueName("test_leading_blanks");
        var sourcePath = Path.Combine(_fixture.TestDataDirectory, $"{name}.cs");
        File.WriteAllText(sourcePath, "\n\n\nConsole.Write(\"blanks\");");

        var result = _tools.LoadScript(sourcePath, name: name);
        Assert.Contains("created", result, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name, "{}");
        Assert.Equal("blanks", callResult);
    }

    [Fact]
    public void LoadScriptHandlesHeaderWithNoBlankLineSeparator()
    {
        var name = UniqueName("test_no_blank_sep");
        var sourcePath = Path.Combine(_fixture.TestDataDirectory, $"{name}.cs");

        File.WriteAllText(sourcePath, $"""
            // @scriptmcp name: {name}
            // @scriptmcp description: No blank separator
            // @scriptmcp type: code
            // @scriptmcp parameters: []
            Console.Write("no-sep");
            """);

        var result = _tools.LoadScript(sourcePath, name: name);
        Assert.Contains("created", result, StringComparison.OrdinalIgnoreCase);

        var callResult = _tools.CallScript(name, "{}");
        Assert.Equal("no-sep", callResult);

        var inspection = _tools.InspectScript(name);
        Assert.Contains("No blank separator", inspection, StringComparison.Ordinal);
    }

    // ── SQLite pragma verification tests ─────────────────────────────────────

    [Fact]
    public void FreshDatabaseHasWalModeAndAutoVacuumIncremental()
    {
        var tempDir = Path.Combine(_fixture.TestDataDirectory, "pragma_test");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "pragma_verify.db");

        // Ensure the DB does not exist
        if (File.Exists(dbPath)) File.Delete(dbPath);

        var originalPath = ScriptTools.SavePath;
        try
        {
            // Point ScriptTools at the fresh DB — EnsureDatabase will create it
            _tools.SetDatabase(dbPath, create: true);

            // Now open a raw connection and check stored pragmas
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            string GetPragma(string name)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA {name}";
                return cmd.ExecuteScalar()?.ToString() ?? "(null)";
            }

            Assert.Equal("wal", GetPragma("journal_mode"));
            Assert.Equal("2", GetPragma("auto_vacuum")); // 2 = INCREMENTAL
            Assert.Equal("ok", GetPragma("quick_check"));
        }
        finally
        {
            RestoreAndDeleteDatabase(originalPath, dbPath);
        }
    }
}
