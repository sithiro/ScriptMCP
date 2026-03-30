using ScriptMCP.Library;

// Test #r directive
Console.WriteLine("=== Test #r directive ===");
var tools = new ScriptTools("test-directives.db");

var result1 = tools.CreateScript(
    name: "test_r",
    description: "Test #r directive",
    body: "#r \"" + Path.GetFullPath("test-artifacts/TestHelper.dll") + "\"\n\nConsole.Write(TestHelper.Greeter.Hello(\"ScriptMCP\"));",
    functionType: "code",
    parameters: "[]",
    outputInstructions: "");
Console.WriteLine($"Create: {result1}");

if (result1.Contains("successfully"))
{
    var callResult = tools.CallScript("test_r");
    Console.WriteLine($"Call: {callResult}");
}

// Test #load directive
Console.WriteLine("\n=== Test #load directive ===");
var helperPath = Path.GetFullPath("test-artifacts/helper.cs");
File.WriteAllText(helperPath, "static string GetGreeting() => \"Hello from #load!\";");

var result2 = tools.CreateScript(
    name: "test_load",
    description: "Test #load directive",
    body: "#load \"" + helperPath + "\"\n\nConsole.Write(GetGreeting());",
    functionType: "code",
    parameters: "[]",
    outputInstructions: "");
Console.WriteLine($"Create: {result2}");

if (result2.Contains("successfully"))
{
    var callResult = tools.CallScript("test_load");
    Console.WriteLine($"Call: {callResult}");
}

// Test #r "nuget:" rejection
Console.WriteLine("\n=== Test #r nuget rejection ===");
var result3 = tools.CreateScript(
    name: "test_nuget",
    description: "Test nuget rejection",
    body: "#r \"nuget: Newtonsoft.Json, 13.0.3\"\n\nConsole.Write(\"should not get here\");",
    functionType: "code",
    parameters: "[]",
    outputInstructions: "");
Console.WriteLine($"Create: {result3}");

// Cleanup
File.Delete(helperPath);
var dbPath = tools.GetDatabase();
if (File.Exists(dbPath)) File.Delete(dbPath);

Console.WriteLine("\nAll tests done.");
