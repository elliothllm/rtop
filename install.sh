#!/usr/bin/env bash
#
# Installs rtop as a global `rtop` command, and makes sure the shell can actually find it.
#
#   ./install.sh              install or update
#   ./install.sh --uninstall  remove the tool and any symlink this script made
#   ./install.sh --no-link    install only; never create a symlink or touch a shell profile
#   ./install.sh --add-to-path  append the tools directory to ~/.zshrc if nothing else worked
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project="$root/src/Rtop/Rtop.csproj"
package_output="$root/artifacts/package"
package_id="Rtop"
command_name="rtop"

# Where `dotnet tool install --global` puts its shims.
tools_dir="${DOTNET_CLI_HOME:-$HOME}/.dotnet/tools"
shim="$tools_dir/$command_name"

# Candidate directories for a symlink, most preferred first. Only ones that are both writable and
# already on PATH are used, so nothing here changes what the shell searches.
link_candidates=("/opt/homebrew/bin" "/usr/local/bin" "$HOME/.local/bin" "$HOME/bin")

mode="install"
allow_link=1
allow_profile=0

for argument in "$@"; do
  case "$argument" in
    --uninstall) mode="uninstall" ;;
    --no-link) allow_link=0 ;;
    --add-to-path) allow_profile=1 ;;
    -h|--help) sed -n '2,9p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "install.sh: unknown option '$argument'" >&2; exit 2 ;;
  esac
done

say() { printf '\n\033[1m%s\033[0m\n' "$1"; }
note() { printf '  %s\n' "$1"; }

on_path() {
  case ":${PATH}:" in *":$1:"*) return 0 ;; *) return 1 ;; esac
}

# A symlink in a PATH directory that points into our tools directory is ours to remove.
our_symlink() {
  [ -L "$1" ] && [[ "$(readlink "$1")" == "$tools_dir/"* ]]
}

remove_symlinks() {
  for directory in "${link_candidates[@]}"; do
    local link="$directory/$command_name"
    if our_symlink "$link"; then
      rm -f "$link"
      note "removed symlink $link"
    fi
  done
}

if [ "$mode" = "uninstall" ]; then
  say "Uninstalling $command_name"
  remove_symlinks
  if dotnet tool uninstall --global "$package_id" >/dev/null 2>&1; then
    note "removed the global tool"
  else
    note "the global tool was not installed"
  fi
  note "config at ~/.config/rtop/config.json was left alone"
  echo
  exit 0
fi

# --- Preflight ---------------------------------------------------------------

if [ "$(uname -s)" != "Darwin" ]; then
  echo "install.sh: rtop's discovery uses macOS ps and lsof; this is $(uname -s)." >&2
  exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "install.sh: the .NET SDK is not on PATH. Install it from https://dot.net and retry." >&2
  exit 1
fi

# --- Build and install -------------------------------------------------------

say "Packing"
rm -rf "$package_output"
dotnet pack "$project" --configuration Release --output "$package_output" --nologo --verbosity quiet
note "$(basename "$(ls "$package_output"/*.nupkg | head -1)")"

say "Installing"
# Uninstall first rather than using `tool update`: the package version does not change between
# local builds, so an update would decide it already had the newest one and keep the old binary.
dotnet tool uninstall --global "$package_id" >/dev/null 2>&1 || true
dotnet tool install --global "$package_id" --add-source "$package_output" --no-cache --verbosity quiet >/dev/null
note "installed to $shim"

if [ ! -x "$shim" ]; then
  echo "install.sh: expected a shim at $shim but there is none." >&2
  exit 1
fi

# --- Make it reachable -------------------------------------------------------

say "Making it reachable"
linked=""

if on_path "$tools_dir"; then
  note "$tools_dir is already on PATH"
elif [ "$allow_link" = "1" ]; then
  for directory in "${link_candidates[@]}"; do
    if [ -d "$directory" ] && [ -w "$directory" ] && on_path "$directory"; then
      ln -sf "$shim" "$directory/$command_name"
      linked="$directory/$command_name"
      note "symlinked $linked -> $shim"
      break
    fi
  done
fi

if ! on_path "$tools_dir" && [ -z "$linked" ]; then
  line="export PATH=\"\$PATH:$tools_dir\""

  if [ "$allow_profile" = "1" ]; then
    profile="$HOME/.zshrc"
    if ! grep -qF "$tools_dir" "$profile" 2>/dev/null; then
      {
        echo ""
        echo "# added by rtop install.sh"
        echo "$line"
      } >> "$profile"
      note "appended to $profile:"
      note "  $line"
      note "open a new shell, or run: source $profile"
    else
      note "$profile already mentions $tools_dir"
    fi
  else
    note "nothing on PATH is writable, so add this to ~/.zshrc yourself:"
    note "  $line"
    note "or re-run with --add-to-path to have this script append it"
  fi
fi

# --- Verify ------------------------------------------------------------------

say "Verifying"
version="$("$shim" --version)"
note "$shim reports $version"

if resolved="$(command -v "$command_name" 2>/dev/null)"; then
  note "\`$command_name\` resolves to $resolved"
  echo
  printf '\033[32mDone.\033[0m Run \033[1m%s\033[0m from inside any git repository.\n\n' "$command_name"
else
  echo
  printf '\033[33mInstalled, but this shell cannot find it yet.\033[0m Open a new shell and try again.\n\n'
fi
