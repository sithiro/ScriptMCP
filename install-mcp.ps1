# ScriptMCP - Register MCP Server
# Registers ScriptMCP with Claude Code, Codex, and/or Copilot.
# Run from inside the ScriptMCP installation folder.
#
# Usage:
#   .\install-mcp.ps1

$binary = "scriptmcp.exe"

# Verify binary exists
if (-not (Test-Path $binary)) {
    Write-Error "scriptmcp.exe not found in current directory. Run this from the ScriptMCP folder."
    exit 1
}

$binaryPath = (Resolve-Path $binary).Path -replace '\\', '/'

Write-Host ""
Write-Host "ScriptMCP MCP Server Registration"
Write-Host "  Binary: $binaryPath"
Write-Host ""

# Ask which agents to integrate with
Write-Host "Which agents would you like to integrate with?"
Write-Host "  1) Claude Code"
Write-Host "  2) Codex"
Write-Host "  3) Copilot (VS Code)"
Write-Host "  4) All detected"
Write-Host ""
$choice = Read-Host "Enter choice (1-4)"

$registered = $false

# Claude Code: claude mcp add -s user -t stdio scriptmcp -- <path>
if ($choice -eq '1' -or $choice -eq '4') {
    $claude = Get-Command claude -ErrorAction SilentlyContinue
    if ($claude) {
        Write-Host ""
        Write-Host "  > claude mcp add -s user -t stdio scriptmcp -- $binaryPath" -ForegroundColor DarkGray
        & claude.exe mcp add -s user -t stdio scriptmcp -- $binaryPath > $null 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  Claude Code: registered" -ForegroundColor Green
        } else {
            Write-Host "  Claude Code: failed" -ForegroundColor Red
        }
        $registered = $true
    } else {
        Write-Host "  Claude Code: not installed (skipped)" -ForegroundColor Yellow
    }
}

# Codex: codex mcp add scriptmcp -- <path>
if ($choice -eq '2' -or $choice -eq '4') {
    $codex = Get-Command codex -ErrorAction SilentlyContinue
    if ($codex) {
        Write-Host ""
        Write-Host "  > codex mcp add scriptmcp -- $binaryPath" -ForegroundColor DarkGray
        & codex.cmd mcp add scriptmcp -- $binaryPath > $null 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  Codex: registered" -ForegroundColor Green
        } else {
            Write-Host "  Codex: failed" -ForegroundColor Red
        }
        $registered = $true
    } else {
        Write-Host "  Codex: not installed (skipped)" -ForegroundColor Yellow
    }
}

# Copilot: code --add-mcp <json>
if ($choice -eq '3' -or $choice -eq '4') {
    $code = Get-Command code -ErrorAction SilentlyContinue
    if ($code) {
        $mcpJson = '{\"name\":\"scriptmcp\",\"command\":\"' + $binaryPath + '\",\"args\":[]}'
        Write-Host ""
        Write-Host "  > code --add-mcp $mcpJson" -ForegroundColor DarkGray
        & code.cmd --add-mcp $mcpJson > $null 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  Copilot: registered" -ForegroundColor Green
        } else {
            Write-Host "  Copilot: failed" -ForegroundColor Red
        }
        $registered = $true
    } else {
        Write-Host "  VS Code: not installed (skipped)" -ForegroundColor Yellow
    }
}

if (-not $registered) {
    Write-Host ""
    Write-Host "No agents were registered." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done!" -ForegroundColor Green
