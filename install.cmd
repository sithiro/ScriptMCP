@echo off
setlocal enabledelayedexpansion

:: ScriptMCP Installer (Windows cmd)
:: Downloads the latest ScriptMCP MCP server and creates a .mcp.json
:: so Claude Code / Codex discovers it automatically.
::
:: Usage:
::   install.cmd

echo.
echo  ███████╗ ██████╗██████╗ ██╗██████╗ ████████╗███╗   ███╗ ██████╗██████╗
echo  ██╔════╝██╔════╝██╔══██╗██║██╔══██╗╚══██╔══╝████╗ ████║██╔════╝██╔══██╗
echo  ███████╗██║     ██████╔╝██║██████╔╝   ██║   ██╔████╔██║██║     ██████╔╝
echo  ╚════██║██║     ██╔══██╗██║██╔═══╝    ██║   ██║╚██╔╝██║██║     ██╔═══╝
echo  ███████║╚██████╗██║  ██║██║██║        ██║   ██║ ╚═╝ ██║╚██████╗██║
echo  ╚══════╝ ╚═════╝╚═╝  ╚═╝╚═╝╚═╝        ╚═╝   ╚═╝     ╚═╝ ╚═════╝╚═╝
echo.

set "REPO=sithiro/ScriptMCP"
set "RID=win-x64"
set "BINARY=scriptmcp.exe"

echo Checking latest version...
curl -fsSL "https://api.github.com/repos/%REPO%/releases/latest" -o "%TEMP%\scriptmcp_release.json"
if errorlevel 1 (
    echo Failed to fetch latest release from GitHub.
    exit /b 1
)

:: Extract tag_name from JSON
for /f "tokens=2 delims=:, " %%a in ('findstr "tag_name" "%TEMP%\scriptmcp_release.json"') do set "TAG=%%~a"
del "%TEMP%\scriptmcp_release.json" 2>nul

:: Extract version from tag (scriptmcp-v1.3.0 -> 1.3.0)
set "VERSION=!TAG:scriptmcp-v=!"

set "ASSET=scriptmcp-%RID%.mcpb"
set "URL=https://github.com/%REPO%/releases/download/!TAG!/%ASSET%"
set "INSTALL_DIR=ScriptMCP v!VERSION!"

echo Downloading ScriptMCP v!VERSION! for %RID%...
curl -fSL "!URL!" -o "%TEMP%\scriptmcp.zip"
if errorlevel 1 (
    echo Download failed.
    exit /b 1
)

echo Extracting to '!INSTALL_DIR!'...
if exist "!INSTALL_DIR!" rmdir /s /q "!INSTALL_DIR!"
mkdir "!INSTALL_DIR!"
tar -xf "%TEMP%\scriptmcp.zip" -C "!INSTALL_DIR!" server/%BINARY%
move "!INSTALL_DIR!\server\%BINARY%" "!INSTALL_DIR!\" >nul
rmdir /s /q "!INSTALL_DIR!\server" 2>nul
del "%TEMP%\scriptmcp.zip" 2>nul

echo Creating .mcp.json...
(
echo {
echo   "mcpServers": {
echo     "scriptmcp": {
echo       "command": "!INSTALL_DIR!/%BINARY%",
echo       "args": []
echo     }
echo   }
echo }
) > .mcp.json

echo.
echo ScriptMCP v!VERSION! installed successfully!
echo   Binary:   !INSTALL_DIR!\%BINARY%
echo   Config:   .mcp.json
echo.
echo Run 'claude' or 'codex' from this directory to start using ScriptMCP.
