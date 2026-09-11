#!/usr/bin/env bash
# ensure-rtop.sh — the [[events]] hook body: make sure the tab this event is
# about has an rtop pane, without ever taking focus.
#
# rtop goes underneath the herdr-sidebar column, so the hook has to wait for the
# sidebar's own tab.created hook to dock that column first — there is no
# ordering between two plugins listening to the same event. With no sidebar
# installed it takes a column of its own on the right instead.
#
# Lifecycle events can arrive more than once for the same tab, and the hook is
# also a manual action, so everything here is idempotent and quiet: an rtop pane
# that is already there and running means there is nothing to do.
set -uo pipefail

herdr_bin="${HERDR_BIN_PATH:-herdr}"
command -v "$herdr_bin" >/dev/null 2>&1 || herdr_bin="$HOME/.local/bin/herdr"
command -v "$herdr_bin" >/dev/null 2>&1 || exit 0
command -v python3 >/dev/null 2>&1 || { echo "rtop plugin: python3 not found" >&2; exit 0; }

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
panes_py="$script_dir/panes.py"
tab=$'\t'

# --- settings ---------------------------------------------------------------
# Overridable from config.env in the plugin's config dir, which install.sh
# seeds with a commented copy of these defaults.

# The pane's label, and so how the hook recognises its own work later.
label="rtop"

# rtop's share of the sidebar column's height (or of the tab's width, when
# there is no sidebar to sit under).
share="0.35"

# How often rtop rescans. A pane you only glance at does not need the 3s
# default, and every instance costs a ps and two lsof spawns each time round.
refresh="15"

# A pane this short has no room for a log, so the default is the list alone.
args="--no-log"

# How long to wait for the sidebar column before docking on the right instead.
sidebar_wait="20"

# trace="1" to find out why a run did nothing: the trace goes to stderr, which
# `herdr plugin log list --plugin rtop` keeps.
trace=""

config_dir="${HERDR_PLUGIN_CONFIG_DIR:-$HOME/.config/herdr/plugins/config/rtop}"
[ -f "$config_dir/config.env" ] && . "$config_dir/config.env"
[ "$trace" = "1" ] && set -x

# The pane gets its own interactive shell and so its own PATH; this only has to
# establish that rtop is installed at all, hence the dotnet tools dir as well.
if ! command -v rtop >/dev/null 2>&1 && [ ! -x "$HOME/.dotnet/tools/rtop" ]; then
  echo "rtop plugin: rtop is not installed — run ./install.sh in the rtop repo" >&2
  exit 0
fi

# --- one at a time ----------------------------------------------------------
# Two tabs created at once would otherwise each read a snapshot taken before the
# other split, and both would dock into the same column.

lock_dir="${TMPDIR:-/tmp}/herdr-rtop-ensure.lock"
if ! mkdir "$lock_dir" 2>/dev/null; then
  now="$(date +%s)"
  born="$(stat -f %m "$lock_dir" 2>/dev/null || stat -c %Y "$lock_dir" 2>/dev/null || echo "$now")"

  # Break the lock of a run that died without cleaning up. The bound allows for
  # the sidebar wait, so a legitimately slow run is never broken into.
  if [ $((now - born)) -gt $((sidebar_wait + 40)) ]; then
    rm -rf "$lock_dir" 2>/dev/null
    mkdir "$lock_dir" 2>/dev/null || exit 0
  else
    # A created tab is a one-off: miss this event and it never gets a pane, so
    # wait for the lock rather than skipping.
    acquired="false"
    for _ in $(seq 1 $((sidebar_wait + 40))); do
      sleep 1
      if mkdir "$lock_dir" 2>/dev/null; then
        acquired="true"
        break
      fi
    done
    [ "$acquired" = "true" ] || exit 0
  fi
fi
trap 'rmdir "$lock_dir" 2>/dev/null' EXIT

# --- which tab this is about --------------------------------------------------
# herdr sets HERDR_TAB_ID to the tab the event belongs to, which is not the same
# as the focused tab: a tab created in a workspace the user is not looking at
# would otherwise have its rtop docked into whatever they are looking at. The
# fallback is for the manual action, which means the tab in front of you.

panes="$("$herdr_bin" pane list 2>/dev/null || true)"
[ -n "$panes" ] || exit 0

scope="${HERDR_TAB_ID:-}"
if [ -z "$scope" ]; then
  scope="$(printf '%s' "$panes" | python3 "$panes_py" focused-tab 2>/dev/null || true)"
fi
[ -n "$scope" ] || exit 0

# Already done? An rtop pane whose foreground really is rtop is left alone. One
# that is only a label — a pane restored with the session, whose process did not
# come back with it — is refilled in place, so the layout survives a restart.
existing="$(printf '%s' "$panes" | python3 "$panes_py" find rtop "$scope" "$label" 2>/dev/null || true)"
if [ -n "$existing" ]; then
  existing_id="${existing%%"$tab"*}"
  alive="$("$herdr_bin" pane process-info --pane "$existing_id" 2>/dev/null |
    python3 "$panes_py" running rtop 2>/dev/null || echo yes)"
  [ "$alive" = "yes" ] && exit 0
  "$herdr_bin" pane run "$existing_id" "rtop --refresh $refresh $args"
  exit 0
fi

# --- where it goes ------------------------------------------------------------

# Under the sidebar when there is one. Its hook races ours on the same event, so
# poll for the column rather than assuming it has been docked yet.
sidebar=""
if "$herdr_bin" plugin list --json 2>/dev/null | grep -q '"plugin_id":"herdr-sidebar"'; then
  deadline=$(($(date +%s) + sidebar_wait))
  while :; do
    sidebar="$(printf '%s' "$panes" | python3 "$panes_py" find sidebar "$scope" "$label" 2>/dev/null || true)"
    [ -n "$sidebar" ] && break
    [ "$(date +%s)" -ge "$deadline" ] && break
    sleep 0.5
    panes="$("$herdr_bin" pane list 2>/dev/null || true)"
    [ -n "$panes" ] || exit 0
  done
fi

if [ -n "$sidebar" ]; then
  target="$sidebar"
  direction="down"
else
  # No sidebar: rtop takes a column on the right of the tab's own pane.
  target="$(printf '%s' "$panes" | python3 "$panes_py" find any "$scope" "$label" 2>/dev/null || true)"
  direction="right"
fi
[ -n "$target" ] || exit 0

target_id="${target%%"$tab"*}"
target_cwd="${target#*"$tab"}"

# --ratio is the share kept by the pane being split, so rtop gets the remainder.
ratio="$(python3 -c "print(max(0.05, min(0.95, 1 - float('$share'))))" 2>/dev/null || echo 0.65)"

split_args=(pane split "$target_id" --direction "$direction" --ratio "$ratio" --no-focus)
[ -n "$target_cwd" ] && split_args+=(--cwd "$target_cwd")

new_pane="$("$herdr_bin" "${split_args[@]}" 2>/dev/null |
  sed -n 's/.*"pane_id":"\([^"]*\)".*/\1/p' | head -n1)"
[ -n "$new_pane" ] || exit 0

"$herdr_bin" pane run "$new_pane" "rtop --refresh $refresh $args"
"$herdr_bin" pane rename "$new_pane" "$label" >/dev/null 2>&1 || true
exit 0
