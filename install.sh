#!/usr/bin/env bash
set -euo pipefail

# ScriptMCP Installer (Linux/macOS)
# Downloads the latest ScriptMCP MCP server and registers it with Claude Code and/or Codex.
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

BINARY_PATH="$(cd "$INSTALL_DIR" && pwd)/$BINARY"

echo ""
echo "ScriptMCP v${VERSION} downloaded to '${INSTALL_DIR}'"
echo ""

# Register with Claude Code
if command -v claude &>/dev/null; then
  read -r -p "Register with Claude Code? (y/n) " answer
  if [[ "$answer" =~ ^[yY] ]]; then
    echo "Registering with Claude Code..."
    claude mcp add -s user -t stdio scriptmcp -- "$BINARY_PATH"
    echo "  Claude Code: registered"
  fi
else
  echo "  Claude Code: not found (skipped)"
fi

# Register with Codex
if command -v codex &>/dev/null; then
  read -r -p "Register with Codex? (y/n) " answer
  if [[ "$answer" =~ ^[yY] ]]; then
    echo "Registering with Codex..."
    codex mcp add scriptmcp -- "$BINARY_PATH"
    echo "  Codex: registered"
  fi
else
  echo "  Codex: not found (skipped)"
fi

echo ""
echo "Done! Start 'claude' or 'codex' to use ScriptMCP."
