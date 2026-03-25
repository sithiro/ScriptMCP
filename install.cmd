@echo off
setlocal enabledelayedexpansion

:: ScriptMCP Installer (Windows cmd)
:: Downloads the latest ScriptMCP MCP server and registers it with Claude Code, Codex, and/or Copilot.
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

set "BINARY_PATH=!INSTALL_DIR!/%BINARY%"

echo.
echo ScriptMCP v!VERSION! downloaded to '!INSTALL_DIR!'
echo.

:: Ask which agents to integrate with
echo Which agents would you like to integrate with?
echo   1) Claude Code
echo   2) Codex
echo   3) Copilot (VS Code)
echo   4) All detected
echo   5) None (create .mcp.json fallback)
echo.
set /p "CHOICE=Enter choice (1-5): "

set "REGISTERED=0"

:: Claude Code
if "!CHOICE!"=="1" goto :do_claude
if "!CHOICE!"=="4" goto :do_claude
goto :skip_claude
:do_claude
where claude >nul 2>nul
if !errorlevel! equ 0 (
    echo Registering with Claude Code...
    claude mcp add -s user -t stdio scriptmcp -- "!BINARY_PATH!"
    echo   Claude Code: registered
    set "REGISTERED=1"
) else (
    echo   Claude Code: not installed (skipped^)
)
:skip_claude

:: Codex
if "!CHOICE!"=="2" goto :do_codex
if "!CHOICE!"=="4" goto :do_codex
goto :skip_codex
:do_codex
where codex >nul 2>nul
if !errorlevel! equ 0 (
    echo Registering with Codex...
    codex mcp add scriptmcp -- "!BINARY_PATH!"
    echo   Codex: registered
    set "REGISTERED=1"
) else (
    echo   Codex: not installed (skipped^)
)
:skip_codex

:: Copilot (VS Code)
if "!CHOICE!"=="3" goto :do_copilot
if "!CHOICE!"=="4" goto :do_copilot
goto :skip_copilot
:do_copilot
where code >nul 2>nul
if !errorlevel! equ 0 (
    echo Registering with Copilot (VS Code^)...
    code --add-mcp "{\"name\":\"scriptmcp\",\"command\":\"!BINARY_PATH!\",\"args\":[]}"
    echo   Copilot: registered
    set "REGISTERED=1"
) else (
    echo   VS Code: not installed (skipped^)
)
:skip_copilot

:: Fallback: create .mcp.json if nothing was registered
if "!CHOICE!"=="5" goto :do_fallback
if "!REGISTERED!"=="0" goto :do_fallback
goto :done
:do_fallback
if "!REGISTERED!"=="0" if not "!CHOICE!"=="5" echo Selected agent not detected.
echo Creating .mcp.json in current directory...
(
echo {
echo   "mcpServers": {
echo     "scriptmcp": {
echo       "command": "!BINARY_PATH!",
echo       "args": []
echo     }
echo   }
echo }
) > .mcp.json
echo   Created .mcp.json

:done
echo.
echo Done!
