using ScriptMCP.Library;

namespace ScriptMCP.Tests;

public sealed class McpConstantsTests
{
    [Fact]
    public void ResolveSavePathUsesDbArgumentWhenProvided()
    {
        var originalPath = ScriptTools.SavePath;
        try
        {
            var expected = Path.GetFullPath(Path.Combine("ScriptMCP.Tests", ".testdata", "resolve-save-path.db"));
            McpConstants.ResolveSavePath([McpConstants.DatabaseArgumentName, expected]);
            Assert.Equal(expected, ScriptTools.SavePath);
        }
        finally
        {
            ScriptTools.SavePath = originalPath;
        }
    }

    [Fact]
    public void ResolveSavePathUsesDefaultDataDirectoryWhenDbArgumentIsRelative()
    {
        var originalPath = ScriptTools.SavePath;
        try
        {
            McpConstants.ResolveSavePath([McpConstants.DatabaseArgumentName, "test.db"]);
            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScriptMCP",
                "test.db");

            Assert.Equal(Path.GetFullPath(expected), ScriptTools.SavePath);
        }
        finally
        {
            ScriptTools.SavePath = originalPath;
        }
    }
}
