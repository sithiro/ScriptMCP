@echo off
setlocal enabledelayedexpansion

:: ScriptMCP Installer (Windows cmd)
:: Downloads the latest ScriptMCP MCP server and registers it with Claude Code and/or Codex.
::
:: Usage:
::   install.cmd

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

:: Get absolute path to binary
pushd "!INSTALL_DIR!"
set "BINARY_PATH=!CD!\%BINARY%"
popd

echo.
echo ScriptMCP v!VERSION! downloaded to '!INSTALL_DIR!'
echo.

:: Register with Claude Code
where claude >nul 2>nul
if !errorlevel! equ 0 (
    set /p "CLAUDE_ANSWER=Register with Claude Code? (y/n) "
    if /i "!CLAUDE_ANSWER!"=="y" (
        echo Registering with Claude Code...
        claude mcp add -s user -t stdio scriptmcp -- "!BINARY_PATH!"
        echo   Claude Code: registered
    )
) else (
    echo   Claude Code: not found (skipped^)
)

:: Register with Codex
where codex >nul 2>nul
if !errorlevel! equ 0 (
    set /p "CODEX_ANSWER=Register with Codex? (y/n) "
    if /i "!CODEX_ANSWER!"=="y" (
        echo Registering with Codex...
        codex mcp add scriptmcp -- "!BINARY_PATH!"
        echo   Codex: registered
    )
) else (
    echo   Codex: not found (skipped^)
)

echo.
echo Done! Start 'claude' or 'codex' to use ScriptMCP.
