#!/bin/bash
# Environment setup script for NoMercy MediaServer dev container
# Configures shell aliases, paths, and imports Claude CLI preferences

set -e

echo "Setting up development environment..."
echo "======================================"

sudo mkdir -p /home/vscode/.local/share/claude
sudo chown -R vscode:vscode /home/vscode/.local

# Install Claude CLI
echo ""
echo "[1/5] Installing Claude CLI..."
curl -fsSL https://claude.ai/install.sh | bash

# Add Claude CLI to PATH for current session
export PATH="${HOME}/.local/bin:${PATH}"

# These helpers are untracked local files, so a fresh clone does not have them.
for helper in ./.claude/ralph-once.sh ./.claude/afk-ralph.sh; do
    if [ -f "$helper" ]; then
        chmod +x "$helper"
    fi
done

# Check if there's an exported config to import
if [ -d ".devcontainer/config" ]; then
    echo "Found exported configuration, importing..."
    bash .devcontainer/config-setup.sh import
else
    # Default setup without exported config
    # Add Claude CLI alias to bashrc if not already present
    if ! grep -q "alias clauded=" ~/.bashrc; then
        echo 'alias clauded="claude --dangerously-skip-permissions"' >> ~/.bashrc
        echo "✓ Added 'clauded' alias to ~/.bashrc"
    else
        echo "✓ Alias 'clauded' already configured"
    fi

    # Ensure .local/bin is in PATH
    if ! grep -q ".local/bin" ~/.bashrc; then
        echo 'export PATH="${HOME}/.local/bin:${PATH}"' >> ~/.bashrc
        echo "✓ Added ~/.local/bin to PATH"
    else
        echo "✓ PATH already configured"
    fi
fi

echo ""
echo "Development environment ready!"
echo ""
echo "Available commands:"
echo "  dotnet build              # Build the solution"
echo "  dotnet test              # Run all tests"
echo "  dotnet run -p src/NoMercy.Server  # Run the server"
echo "  claude --help            # Show Claude CLI help"
echo "  clauded <prompt>         # Run Claude with permissions skipped"
echo ""
echo "To export your current Claude preferences:"
echo "  bash .devcontainer/config-setup.sh export"
echo ""
