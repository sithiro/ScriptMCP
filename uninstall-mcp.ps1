# ScriptMCP - Unregister MCP Server
# Removes ScriptMCP from Claude Code, Codex, and/or Copilot.
#
# Usage:
#   .\uninstall-mcp.ps1

Write-Host ""
Write-Host "ScriptMCP MCP Server Removal"
Write-Host ""

# Ask which agents to unregister from
Write-Host "Which agents would you like to unregister from?"
Write-Host "  1) Claude Code"
Write-Host "  2) Codex"
Write-Host "  3) Copilot (VS Code)"
Write-Host "  4) All detected"
Write-Host ""
$choice = Read-Host "Enter choice (1-4)"

# Claude Code
if ($choice -eq '1' -or $choice -eq '4') {
    $claude = Get-Command claude -ErrorAction SilentlyContinue
    if ($claude) {
        Write-Host "Removing from Claude Code..."
        Write-Host "  > claude mcp remove -s user scriptmcp" -ForegroundColor DarkGray
        & claude.exe mcp remove -s user scriptmcp
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  Claude Code: removed" -ForegroundColor Green
        } else {
            Write-Host "  Claude Code: failed" -ForegroundColor Red
        }
    } else {
        Write-Host "  Claude Code: not installed (skipped)" -ForegroundColor Yellow
    }
}

# Codex
if ($choice -eq '2' -or $choice -eq '4') {
    $codex = Get-Command codex -ErrorAction SilentlyContinue
    if ($codex) {
        Write-Host "Removing from Codex..."
        Write-Host "  > codex mcp remove scriptmcp" -ForegroundColor DarkGray
        & codex.cmd mcp remove scriptmcp
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  Codex: removed" -ForegroundColor Green
        } else {
            Write-Host "  Codex: failed" -ForegroundColor Red
        }
    } else {
        Write-Host "  Codex: not installed (skipped)" -ForegroundColor Yellow
    }
}

# Copilot (VS Code)
if ($choice -eq '3' -or $choice -eq '4') {
    $code = Get-Command code -ErrorAction SilentlyContinue
    if ($code) {
        Write-Host "Removing from Copilot (VS Code)..."
        Write-Host "  Copilot does not support CLI removal. Remove 'scriptmcp' manually:" -ForegroundColor Yellow
        Write-Host "    VS Code > Command Palette > MCP: List Servers > Remove scriptmcp" -ForegroundColor Yellow
    } else {
        Write-Host "  VS Code: not installed (skipped)" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Done!" -ForegroundColor Green
