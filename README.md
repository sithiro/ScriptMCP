# ScriptMCP

## Prebuilt Console App

ScriptMCP.Console is published as a self-contained, single-file executable for:
- Windows x64
- Linux x64
- macOS x64
- macOS arm64

### Install And Configure (Recommended)

1. Download the release zip for your OS and extract it to a location of your choice.
2. Add an MCP server config to your AI agent that targets the executable.
   - `type` must be `stdio`.

### Claude Code Example (.mcp.json)

Create a `.mcp.json` in your project directory:

```json
{
  "servers": {
    "scriptmcp": {
      "type": "stdio",
      "command": "C:\\Tools\\ScriptMCP.Console\\ScriptMCP.Console.exe",
      "args": []
    }
  }
}
```

macOS/Linux example:

```json
{
  "servers": {
    "scriptmcp": {
      "type": "stdio",
      "command": "/opt/scriptmcp/ScriptMCP.Console",
      "args": []
    }
  }
}
```

### tools.db Location

The database is created on first run at the OS-specific “Local Application Data” path:

- Windows: `%LOCALAPPDATA%\\ScriptMCP\\tools.db`
- macOS: `~/Library/Application Support/ScriptMCP/tools.db`
- Linux: `~/.local/share/ScriptMCP/tools.db`
