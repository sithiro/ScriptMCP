#!/usr/bin/env bash
set -euo pipefail

# ScriptMCP Installer
# Downloads a specific version of the ScriptMCP MCP server and creates a .mcp.json
# pointing to it so Claude Code / Codex can discover it automatically.
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/sithiro/ScriptMCP/main/install.sh | bash -s -- <version>
#   ./install.sh 1.0.0

VERSION="${1:-}"
if [ -z "$VERSION" ]; then
  echo "Usage: install.sh <version>"
  echo "  e.g. install.sh 1.0.0"
  exit 1
fi

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

ASSET="scriptmcp-${RID}.mcpb"
URL="https://github.com/sithiro/ScriptMCP/releases/download/scriptmcp-v${VERSION}/${ASSET}"
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

# Determine the command path for .mcp.json
case "$RID" in
  win-x64) CMD_PATH="${INSTALL_DIR}/${BINARY}" ;;
  *)        CMD_PATH="./${INSTALL_DIR}/${BINARY}" ;;
esac

# Create .mcp.json
cat > .mcp.json <<MCPJSON
{
  "mcpServers": {
    "scriptmcp": {
      "command": "${CMD_PATH}",
      "args": []
    }
  }
}
MCPJSON

echo ""
echo "ScriptMCP v${VERSION} installed successfully!"
echo "  Binary:   ${INSTALL_DIR}/${BINARY}"
echo "  Config:   .mcp.json"
echo ""
echo "Run 'claude' or 'codex' from this directory to start using ScriptMCP."
