# ScriptMCP Installer (Windows PowerShell)
# Downloads the latest ScriptMCP MCP server and registers it with Claude Code, Codex, and/or Copilot.
#
# Usage:
#   powershell -c "irm https://sithiro.github.io/ScriptMCP/install.ps1 | iex"

$rid = "win-x64"
$binary = "scriptmcp.exe"
$repo = "sithiro/ScriptMCP"

# Get the latest release tag
Write-Host "Checking latest version..."
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -UseBasicParsing -ErrorAction Stop
    $tag = $release.tag_name
} catch {
    Write-Error "Failed to fetch latest release from GitHub."
    Write-Error $_.Exception.Message
    exit 1
}

# Extract version from tag (scriptmcp-v1.3.0 -> 1.3.0)
$version = $tag -replace '^scriptmcp-v', ''
$asset = "scriptmcp-$rid.mcpb"
$url = "https://github.com/$repo/releases/download/$tag/$asset"
$installDir = "ScriptMCP v$version"

Write-Host "Downloading ScriptMCP v$version for $rid..."
$tmpFile = [System.IO.Path]::GetTempFileName() + ".zip"

try {
    Invoke-WebRequest -Uri $url -OutFile $tmpFile -UseBasicParsing -ErrorAction Stop
} catch {
    Write-Error "Failed to download: $url"
    Write-Error $_.Exception.Message
    exit 1
}

Write-Host "Extracting to '$installDir'..."
if (Test-Path $installDir) {
    Remove-Item $installDir -Recurse -Force
}
New-Item -ItemType Directory -Path $installDir -Force | Out-Null

# Extract server/* from the mcpb (zip)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($tmpFile)
try {
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName -like "server/*" -and $entry.Name) {
            $destPath = Join-Path $installDir $entry.Name
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destPath, $true)
        }
    }
} finally {
    $zip.Dispose()
}

Remove-Item $tmpFile -Force -ErrorAction SilentlyContinue

$binaryPath = "$installDir/$binary"

Write-Host ""
Write-Host "ScriptMCP v$version downloaded to '$installDir'" -ForegroundColor Green
Write-Host ""

# Ask which agents to integrate with
Write-Host "Which agents would you like to integrate with?"
Write-Host "  1) Claude Code"
Write-Host "  2) Codex"
Write-Host "  3) Copilot (VS Code)"
Write-Host "  4) All detected"
Write-Host "  5) None (create .mcp.json fallback)"
Write-Host ""
$choice = Read-Host "Enter choice (1-5)"

$registered = $false

# Claude Code
if ($choice -eq '1' -or $choice -eq '4') {
    $claude = Get-Command claude -ErrorAction SilentlyContinue
    if ($claude) {
        Write-Host "Registering with Claude Code..."
        & claude.exe mcp add -s user -t stdio scriptmcp -- $binaryPath
        Write-Host "  Claude Code: registered" -ForegroundColor Green
        $registered = $true
    } else {
        Write-Host "  Claude Code: not installed (skipped)" -ForegroundColor Yellow
    }
}

# Codex
if ($choice -eq '2' -or $choice -eq '4') {
    $codex = Get-Command codex -ErrorAction SilentlyContinue
    if ($codex) {
        Write-Host "Registering with Codex..."
        & codex.cmd mcp add scriptmcp -- $binaryPath
        Write-Host "  Codex: registered" -ForegroundColor Green
        $registered = $true
    } else {
        Write-Host "  Codex: not installed (skipped)" -ForegroundColor Yellow
    }
}

# Copilot (VS Code)
if ($choice -eq '3' -or $choice -eq '4') {
    $code = Get-Command code -ErrorAction SilentlyContinue
    if ($code) {
        Write-Host "Registering with Copilot (VS Code)..."
        $mcpArg = "{`"name`":`"scriptmcp`",`"command`":`"$($binaryPath -replace '\\','/')`",`"args`":[]}"
        & code.cmd --add-mcp $mcpArg
        Write-Host "  Copilot: registered" -ForegroundColor Green
        $registered = $true
    } else {
        Write-Host "  VS Code: not installed (skipped)" -ForegroundColor Yellow
    }
}

# Fallback: create .mcp.json if nothing was registered
if ($choice -eq '5' -or -not $registered) {
    if (-not $registered) {
        Write-Host "Creating .mcp.json in current directory..."
    }
    $mcpJson = @"
{
  "mcpServers": {
    "scriptmcp": {
      "command": "$binaryPath",
      "args": []
    }
  }
}
"@
    [System.IO.File]::WriteAllText((Join-Path $PWD ".mcp.json"), $mcpJson)
    Write-Host "  Created .mcp.json" -ForegroundColor Green
}

Write-Host ""
Write-Host "Done!" -ForegroundColor Green
