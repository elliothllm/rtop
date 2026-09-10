#!/usr/bin/env bash
# install.sh — link this directory into herdr as a plugin, so every new tab
# opens with an rtop pane under the sidebar.
#
# Linking rather than copying: the plugin runs from this checkout, so `git pull`
# is all it takes to update it.
#
#   ./herdr/install.sh              link the plugin and reload herdr
#   ./herdr/install.sh --uninstall  unlink it again
set -euo pipefail

plugin_dir="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
plugin_id="rtop"

red() { printf '\033[31m%s\033[0m\n' "$1"; }
green() { printf '\033[32m%s\033[0m\n' "$1"; }
dim() { printf '\033[2m%s\033[0m\n' "$1"; }

herdr_bin="${HERDR_BIN_PATH:-herdr}"
if ! command -v "$herdr_bin" >/dev/null 2>&1; then
  red "herdr is not on PATH."
  echo "This plugin only does anything inside herdr: https://herdr.dev"
  exit 1
fi

if [ "${1:-}" = "--uninstall" ]; then
  "$herdr_bin" plugin unlink "$plugin_id" >/dev/null 2>&1 ||
    dim "Nothing to unlink: no '$plugin_id' plugin is registered."
  "$herdr_bin" server reload-config >/dev/null 2>&1 || true
  green "Unlinked. Existing rtop panes are left alone — close them yourself."
  exit 0
fi

if [ "$(uname -s)" != "Darwin" ]; then
  red "rtop is macOS-only: discovery is built on ps and lsof."
  exit 1
fi

# The pane runs `rtop`, so it has to resolve in an ordinary interactive shell —
# not merely here, where this script may have inherited a richer PATH.
if ! command -v rtop >/dev/null 2>&1 && [ ! -x "$HOME/.dotnet/tools/rtop" ]; then
  red "rtop is not installed."
  echo "Run ./install.sh in the repository root first, then come back here."
  exit 1
fi

chmod +x "$plugin_dir/scripts/ensure-rtop.sh" "$plugin_dir/scripts/panes.py"

echo "Linking $plugin_dir"
"$herdr_bin" plugin link "$plugin_dir" >/dev/null

# Seed the settings file with the defaults, commented out, so the knobs are
# discoverable without reading the hook. An existing file is never touched.
config_dir="$("$herdr_bin" plugin config-dir "$plugin_id" 2>/dev/null || true)"
if [ -n "$config_dir" ] && [ ! -f "$config_dir/config.env" ]; then
  mkdir -p "$config_dir"
  cat > "$config_dir/config.env" <<'EOF'
# Settings for the rtop herdr plugin. Uncomment to change; sourced by the hook.

# rtop's share of the sidebar column's height.
# share="0.35"

# How often rtop rescans, in seconds. A pane you only glance at does not need
# the 3s default, and each instance costs a ps and two lsof spawns every time.
# refresh="15"

# Arguments for the pane's rtop. Drop --no-log to get the log pane back, at the
# cost of re-reading the selected log once a second.
# args="--no-log"

# How long to wait for the sidebar column before docking on the right instead.
# sidebar_wait="20"

# Why did a hook run do nothing? Set this and read the trace back with
#   herdr plugin log list --plugin rtop
# trace="1"
EOF
  dim "Wrote $config_dir/config.env"
fi

"$herdr_bin" server reload-config >/dev/null 2>&1 ||
  dim "Could not reload the herdr server; restart it to pick the plugin up."

green "Installed."
echo
echo "  New tabs will open with rtop under the sidebar."
echo "  For a tab that already exists, run the 'Dock rtop in this tab' plugin action:"
echo "    $herdr_bin plugin action invoke rtop-dock --plugin $plugin_id"
echo
dim "Settings: $config_dir/config.env    Logs: $herdr_bin plugin log list --plugin $plugin_id"
