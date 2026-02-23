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

### Claude Code

#### Via CLI (recommended)

Use the `claude mcp add` command to register ScriptMCP as a user-level MCP server:

```bash
claude mcp add -s user scriptmcp -- "C:\Tools\ScriptMCP 1.0.4\scriptmcp.exe"
```

macOS/Linux:

```bash
claude mcp add -s user scriptmcp -- /opt/ScriptMCP\ 1.0.4/scriptmcp
```

The `-s user` flag makes ScriptMCP available across all your projects. To scope it to a single project, use `-s project` instead.

To remove it:

```bash
claude mcp remove -s user scriptmcp
```

#### Via .mcp.json

Alternatively, create a `.mcp.json` in your project directory:

```json
{
  "mcpServers": {
    "scriptmcp": {
      "type": "stdio",
      "command": "C:\\Tools\\ScriptMCP 1.0.4\\scriptmcp.exe",
      "args": []
    }
  }
}
```

macOS/Linux example:

```json
{
  "mcpServers": {
    "scriptmcp": {
      "type": "stdio",
      "command": "/opt/ScriptMCP 1.0.4/scriptmcp",
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
