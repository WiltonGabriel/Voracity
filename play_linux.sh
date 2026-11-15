#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export DOTNET_ROLL_FORWARD=Major

if ! command -v godot4-mono >/dev/null 2>&1; then
  if [ -x "$HOME/.local/bin/godot4-mono" ]; then
    export PATH="$HOME/.local/bin:$PATH"
  else
    echo "Erro: 'godot4-mono' não encontrado no PATH nem em ~/.local/bin." >&2
    exit 1
  fi
fi

echo "Compilando Voracity.Client (.NET)..."
dotnet build "$SCRIPT_DIR/src/Voracity.Client/Voracity.Client.csproj" --nologo

echo "Iniciando Voracity (Godot 4 Vulkan)..."
exec godot4-mono --path "$SCRIPT_DIR/src/Voracity.Client" "$@"
