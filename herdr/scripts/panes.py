#!/usr/bin/env python3
"""The JSON reading the ensure hook needs, kept out of the shell script.

Three questions only: which pane in a tab is the one we care about, whether a
pane still has rtop running in it, and — for an invocation with no event behind
it — which tab is in front of the user. Answers are printed as plain text so the
caller can read them with `read` rather than another parser.
"""

import json
import sys


def in_scope(pane: dict, scope: str) -> bool:
    return not scope or scope in (pane.get("tab_id"), pane.get("workspace_id"))


def is_sidebar(pane: dict) -> bool:
    """A herdr-sidebar pane, by the heartbeat token its TUI stamps on itself.

    The label and terminal title are accepted too, so a sidebar that has not
    stamped itself yet is still recognised as the column to hang rtop under.
    """
    tokens = pane.get("tokens") or {}

    return (
        any(name.startswith("herdr-sidebar") for name in tokens)
        or pane.get("label") in ("Sidebar", "Explorer")
        or pane.get("terminal_title_stripped") == "herdr-sidebar"
    )


def find(kind: str, scope: str, label: str, panes: list) -> str:
    """Tab-separated `<pane_id> <cwd>` for the wanted pane, "" when there is none."""
    if kind == "rtop":
        matches = [pane for pane in panes if in_scope(pane, scope) and pane.get("label") == label]
    elif kind == "sidebar":
        matches = [pane for pane in panes if in_scope(pane, scope) and is_sidebar(pane)]
    else:
        # The pane to split when there is no sidebar: the focused one in scope,
        # else any of them — a brand-new tab may not have a focused pane yet.
        candidates = [pane for pane in panes if in_scope(pane, scope)]
        matches = [pane for pane in candidates if pane.get("focused")] or candidates

    if not matches:
        return ""

    return "{}\t{}".format(matches[0].get("pane_id", ""), matches[0].get("cwd", ""))


def read_stdin() -> dict:
    try:
        return json.load(sys.stdin)
    except ValueError:
        return {}


def main() -> int:
    mode = sys.argv[1] if len(sys.argv) > 1 else ""

    if mode == "focused-tab":
        # The fallback scope for a run with no event behind it: the manual
        # "dock rtop here" action, which means the tab in front of you.
        panes = read_stdin().get("result", {}).get("panes", [])
        focused = [pane for pane in panes if pane.get("focused")]
        print(focused[0].get("tab_id", "") if focused else "")
        return 0

    if mode == "find":
        kind, scope, label = sys.argv[2], sys.argv[3], sys.argv[4]
        panes = read_stdin().get("result", {}).get("panes", [])
        print(find(kind, scope, label, panes))
        return 0

    if mode == "running":
        # Whether the pane's foreground is rtop itself, as opposed to the shell
        # left behind by one that exited or died with a restarted server.
        info = read_stdin().get("result", {}).get("process_info", {})
        processes = info.get("foreground_processes", [])
        print("yes" if any(p.get("name") == sys.argv[2] for p in processes) else "no")
        return 0

    print("usage: panes.py focused-tab | find <rtop|sidebar|any> <scope> <label> | running <name>",
          file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
