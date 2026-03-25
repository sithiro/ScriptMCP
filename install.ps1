# ScriptMCP Installer (Windows PowerShell)
# Downloads the latest ScriptMCP MCP server.
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

$binaryPath = (Resolve-Path (Join-Path $installDir $binary)).Path -replace '\\', '/'

Write-Host ""
Write-Host "ScriptMCP v$version installed to '$installDir'" -ForegroundColor Green
Write-Host ""
Write-Host "Register with your agent:"
Write-Host ""
Write-Host "  claude mcp add -s user -t stdio scriptmcp -- `"$binaryPath`"" -ForegroundColor Cyan
Write-Host "  codex mcp add scriptmcp -- `"$binaryPath`"" -ForegroundColor Cyan
Write-Host "  code --add-mcp '{`"name`":`"scriptmcp`",`"command`":`"$binaryPath`",`"args`":[]}'" -ForegroundColor Cyan
Write-Host ""
Write-Host "To unregister:"
Write-Host ""
Write-Host "  claude mcp remove -s user scriptmcp" -ForegroundColor DarkGray
Write-Host "  codex mcp remove scriptmcp" -ForegroundColor DarkGray
