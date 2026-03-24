# ScriptMCP Installer (Windows PowerShell)
# Downloads a specific version of the ScriptMCP MCP server and creates a .mcp.json
# pointing to it so Claude Code / Codex can discover it automatically.
#
# Usage:
#   irm https://raw.githubusercontent.com/sithiro/ScriptMCP/main/install.ps1 | iex   (prompts for version)
#   .\install.ps1 -Version 1.0.0

param(
    [Parameter(Position = 0)]
    [string]$Version
)

if (-not $Version) {
    $Version = Read-Host "Enter ScriptMCP version to install (e.g. 1.0.0)"
    if (-not $Version) {
        Write-Error "Version is required."
        exit 1
    }
}

$rid = "win-x64"
$binary = "scriptmcp.exe"
$asset = "scriptmcp-$rid.mcpb"
$url = "https://github.com/sithiro/ScriptMCP/releases/download/v$Version/$asset"
$installDir = "ScriptMCP v$Version"

Write-Host "Downloading ScriptMCP v$Version for $rid..."
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

# Create .mcp.json
$mcpJson = @{
    mcpServers = @{
        scriptmcp = @{
            command = "$installDir\$binary"
            args = @()
        }
    }
} | ConvertTo-Json -Depth 4

Set-Content -Path ".mcp.json" -Value $mcpJson -Encoding UTF8

Write-Host ""
Write-Host "ScriptMCP v$Version installed successfully!" -ForegroundColor Green
Write-Host "  Binary:   $installDir\$binary"
Write-Host "  Config:   .mcp.json"
Write-Host ""
Write-Host "Run 'claude' or 'codex' from this directory to start using ScriptMCP."
