# ScriptMCP Installer (Windows PowerShell)
# Downloads the latest ScriptMCP MCP server and registers it with Claude Code and/or Codex.
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

$binaryPath = (Resolve-Path (Join-Path $installDir $binary)).Path

Write-Host ""
Write-Host "ScriptMCP v$version downloaded to '$installDir'" -ForegroundColor Green
Write-Host ""

# Register with Claude Code
$claude = Get-Command claude -ErrorAction SilentlyContinue
if ($claude) {
    $answer = Read-Host "Register with Claude Code? (y/n)"
    if ($answer -match '^[yY]') {
        Write-Host "Registering with Claude Code..."
        & claude mcp add -s user -t stdio scriptmcp -- $binaryPath
        Write-Host "  Claude Code: registered" -ForegroundColor Green
    }
} else {
    Write-Host "  Claude Code: not found (skipped)"
}

# Register with Codex
$codex = Get-Command codex -ErrorAction SilentlyContinue
if ($codex) {
    $answer = Read-Host "Register with Codex? (y/n)"
    if ($answer -match '^[yY]') {
        Write-Host "Registering with Codex..."
        & codex mcp add scriptmcp -- $binaryPath
        Write-Host "  Codex: registered" -ForegroundColor Green
    }
} else {
    Write-Host "  Codex: not found (skipped)"
}

Write-Host ""
Write-Host "Done! Start 'claude' or 'codex' to use ScriptMCP."
