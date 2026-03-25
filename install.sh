#!/usr/bin/env bash
set -euo pipefail

# ScriptMCP Installer (Linux/macOS)
# Downloads the latest ScriptMCP MCP server and registers it with Claude Code, Codex, and/or Copilot.
#
# Usage:
#   curl -fsSL https://sithiro.github.io/ScriptMCP/install.sh | bash

REPO="sithiro/ScriptMCP"

# Detect platform
OS="$(uname -s)"
ARCH="$(uname -m)"

case "$OS" in
  Linux*)   RID="linux-x64" ; BINARY="scriptmcp" ;;
  Darwin*)
    case "$ARCH" in
      arm64|aarch64) RID="osx-arm64" ;;
      *)             RID="osx-x64" ;;
    esac
    BINARY="scriptmcp"
    ;;
  MINGW*|MSYS*|CYGWIN*) RID="win-x64" ; BINARY="scriptmcp.exe" ;;
  *)
    echo "Unsupported OS: $OS"
    exit 1
    ;;
esac

# Get the latest release tag
echo "Checking latest version..."
TAG=$(curl -fsSL "https://api.github.com/repos/${REPO}/releases/latest" | grep -o '"tag_name":\s*"[^"]*"' | head -1 | cut -d'"' -f4)
if [ -z "$TAG" ]; then
  echo "Failed to fetch latest release from GitHub."
  exit 1
fi

# Extract version from tag (scriptmcp-v1.3.0 -> 1.3.0)
VERSION="${TAG#scriptmcp-v}"
ASSET="scriptmcp-${RID}.mcpb"
URL="https://github.com/${REPO}/releases/download/${TAG}/${ASSET}"
INSTALL_DIR="ScriptMCP v${VERSION}"

echo "Downloading ScriptMCP v${VERSION} for ${RID}..."
TMPFILE="$(mktemp)"
trap 'rm -f "$TMPFILE"' EXIT

if command -v curl &>/dev/null; then
  curl -fSL "$URL" -o "$TMPFILE"
elif command -v wget &>/dev/null; then
  wget -q "$URL" -O "$TMPFILE"
else
  echo "Error: curl or wget is required"
  exit 1
fi

echo "Extracting to '${INSTALL_DIR}'..."
mkdir -p "$INSTALL_DIR"
unzip -o -q "$TMPFILE" "server/*" -d "$INSTALL_DIR"

# Move binary from server/ subfolder to install dir root
mv "$INSTALL_DIR/server/$BINARY" "$INSTALL_DIR/"
rm -rf "$INSTALL_DIR/server" "$INSTALL_DIR/manifest.json" 2>/dev/null || true

# Make executable on Linux/macOS
if [ "$RID" != "win-x64" ]; then
  chmod +x "$INSTALL_DIR/$BINARY"
fi

BINARY_PATH="${INSTALL_DIR}/${BINARY}"

echo ""
echo "ScriptMCP v${VERSION} downloaded to '${INSTALL_DIR}'"
echo ""

# Ask which agents to integrate with
echo "Which agents would you like to integrate with?"
echo "  1) Claude Code"
echo "  2) Codex"
echo "  3) Copilot (VS Code)"
echo "  4) All detected"
echo "  5) None (create .mcp.json fallback)"
echo ""
read -r -p "Enter choice (1-5): " choice

REGISTERED=false

# Claude Code
if [ "$choice" = "1" ] || [ "$choice" = "4" ]; then
  if command -v claude &>/dev/null; then
    echo "Registering with Claude Code..."
    claude mcp add -s user -t stdio scriptmcp -- "$BINARY_PATH"
    echo "  Claude Code: registered"
    REGISTERED=true
  else
    echo "  Claude Code: not installed (skipped)"
  fi
fi

# Codex
if [ "$choice" = "2" ] || [ "$choice" = "4" ]; then
  if command -v codex &>/dev/null; then
    echo "Registering with Codex..."
    codex mcp add scriptmcp -- "$BINARY_PATH"
    echo "  Codex: registered"
    REGISTERED=true
  else
    echo "  Codex: not installed (skipped)"
  fi
fi

# Copilot (VS Code)
if [ "$choice" = "3" ] || [ "$choice" = "4" ]; then
  if command -v code &>/dev/null; then
    echo "Registering with Copilot (VS Code)..."
    code --add-mcp "{\"name\":\"scriptmcp\",\"command\":\"$BINARY_PATH\",\"args\":[]}"
    echo "  Copilot: registered"
    REGISTERED=true
  else
    echo "  VS Code: not installed (skipped)"
  fi
fi

# Fallback: create .mcp.json if nothing was registered
if [ "$choice" = "5" ] || [ "$REGISTERED" = false ]; then
  if [ "$REGISTERED" = false ] && [ "$choice" != "5" ]; then
    echo "Selected agent not detected."
  fi
  echo "Creating .mcp.json in current directory..."
  cat > .mcp.json <<MCPJSON
{
  "mcpServers": {
    "scriptmcp": {
      "command": "${BINARY_PATH}",
      "args": []
    }
  }
}
MCPJSON
  echo "  Created .mcp.json"
fi

echo ""
echo "Done!"
