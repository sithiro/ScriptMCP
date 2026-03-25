# ScriptMCP Installer (Windows PowerShell)
# Downloads the latest ScriptMCP MCP server and registers it with Claude Code, Codex, and/or Copilot.
#
# Usage:
#   powershell -c "irm https://sithiro.github.io/ScriptMCP/install.ps1 | iex"

$rid = "win-x64"
$binary = "scriptmcp.exe"
$repo = "sithiro/ScriptMCP"
$baseUrl = "https://raw.githubusercontent.com/$repo/main"

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

# Download install-mcp.ps1 and uninstall-mcp.ps1 into the ScriptMCP folder
Write-Host "Downloading helper scripts..."
try {
    Invoke-WebRequest -Uri "$baseUrl/install-mcp.ps1" -OutFile (Join-Path $installDir "install-mcp.ps1") -UseBasicParsing -ErrorAction Stop
    Invoke-WebRequest -Uri "$baseUrl/uninstall-mcp.ps1" -OutFile (Join-Path $installDir "uninstall-mcp.ps1") -UseBasicParsing -ErrorAction Stop
} catch {
    Write-Warning "Could not download helper scripts: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "ScriptMCP v$version downloaded to '$installDir'" -ForegroundColor Green
Write-Host ""

# Run install-mcp.ps1 from the ScriptMCP folder
$installMcp = Join-Path $installDir "install-mcp.ps1"
if (Test-Path $installMcp) {
    Push-Location $installDir
    . ".\install-mcp.ps1"
    Pop-Location
} else {
    Write-Host "install-mcp.ps1 not found. Register manually by running:" -ForegroundColor Yellow
    Write-Host "  cd '$installDir'" -ForegroundColor Yellow
    Write-Host "  .\install-mcp.ps1" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "To unregister later, run:" -ForegroundColor DarkGray
Write-Host "  cd '$installDir'" -ForegroundColor DarkGray
Write-Host "  .\uninstall-mcp.ps1" -ForegroundColor DarkGray
