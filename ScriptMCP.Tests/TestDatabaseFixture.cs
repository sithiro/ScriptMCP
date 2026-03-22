using ScriptMCP.Library;

namespace ScriptMCP.Tests;

public sealed class TestDatabaseFixture : IDisposable
{
    public string TestDataDirectory { get; }
    public string DatabasePath { get; }
    public string OutputDirectory => ScriptTools.GetScheduledTaskOutputDirectory();

    public TestDatabaseFixture()
    {
        var baseDir = AppContext.BaseDirectory;
        TestDataDirectory = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".testdata"));
        if (Directory.Exists(TestDataDirectory))
            Directory.Delete(TestDataDirectory, recursive: true);
        Directory.CreateDirectory(TestDataDirectory);

        DatabasePath = Path.Combine(TestDataDirectory, "scriptmcp.tests.db");
        if (File.Exists(DatabasePath))
            File.Delete(DatabasePath);

        ScriptTools.SavePath = DatabasePath;
    }

    public void Dispose()
    {
        // Best effort cleanup: keep file if locked for diagnostics.
        try
        {
            if (Directory.Exists(TestDataDirectory))
                Directory.Delete(TestDataDirectory, recursive: true);
        }
        catch
        {
        }
    }
}

[CollectionDefinition("ScriptTools tests", DisableParallelization = true)]
public sealed class ScriptToolsCollection : ICollectionFixture<TestDatabaseFixture>
{
}
