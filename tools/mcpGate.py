#!/usr/bin/env python3
"""PreToolUse gate for the Onshape FeatureScript MCP server.

Every call to that server spends the account's metered Onshape API allocation. This repository
already drives Onshape over a logged-in browser session instead - `profiler-tools/` and
`drawing-tools/` - which costs nothing and, for the round-trip diagnostics this gate cares about,
reports strictly more: `eval-fs.mjs` prints notices AND console together, where the MCP returns
notices INSTEAD of console and so throws away every println from a run that failed.

The gate denies the MCP tools that have a browser equivalent and names that equivalent. Account
queries pass through, because checking what the allocation has left must never itself be blocked.

  python tools/mcpGate.py --hook    read a Claude Code hook payload on stdin, allow or deny

Set ONSHAPE_MCP_OK=1 in the environment to pass everything through, for the case where the browser
route genuinely cannot do the job. Exit status is 0 whatever the decision; the decision travels as
JSON on stdout, so a gate that crashes never blocks the session.
"""

import json
import os
import sys

# Each blocked tool names what to run instead. The browser scripts live in profiler-tools/ and
# take their inputs from environment variables; profiler-tools/README.md carries the full table.
REPLACEMENTS = {
    "test_featurescript": (
        "SNIPPET='<expression>' node profiler-tools/eval-fs.mjs\n"
        "        Evaluates against a real Part Studio and prints notices AND console together."
    ),
    "test_feature": (
        "node profiler-tools/push.mjs, then node profiler-tools/run-tests.mjs <featureType>...\n"
        "        Pushes the local file, re-inserts the features to force a rebuild, and prints\n"
        "        each verdict with its printlns. For a bare expression use eval-fs.mjs instead."
    ),
    "put_featurescript": "node profiler-tools/push.mjs [utils|tester]",
    "get_featurescript": "node profiler-tools/sync.mjs   (read-only diff of tab against local file)",
    "list_feature_studios": "node profiler-tools/elements.mjs   (tab inventory and microversion)",
    "create_feature_studio": "node profiler-tools/new-partstudio.mjs   (NAME=... for a Part Studio)",
    "create_geometry": "node profiler-tools/insert-tests.mjs <featureType>...",
    "read_featurescript_notes": "node profiler-tools/notices.mjs",
    "edit_featurescript_notes": "node profiler-tools/push.mjs",
    "search_featurescript_documentation": (
        "Grep the mirrored standard library in this repository - it is the same content,\n"
        "        offline and free. Start from the repo root .fs files and docs/."
    ),
}

# Account and session management. Never blocked: get_api_usage is how the damage gets measured.
ALLOWED = {"get_api_usage", "whoami", "logout", "set_api_allocation"}

SERVER_PREFIX = "mcp__onshape-featurescript__"


def short_name(tool_name):
    """The bare tool name, with the MCP server prefix stripped."""
    if tool_name.startswith(SERVER_PREFIX):
        return tool_name[len(SERVER_PREFIX):]
    return tool_name


def decide(tool_name):
    """Return the denial reason for this tool, or None to let the call through."""
    if os.environ.get("ONSHAPE_MCP_OK") == "1":
        return None
    if not tool_name.startswith(SERVER_PREFIX):
        return None

    name = short_name(tool_name)
    if name in ALLOWED:
        return None

    replacement = REPLACEMENTS.get(name)
    lines = [
        "Blocked by tools/mcpGate.py: %s spends the metered Onshape API allocation." % name,
        "",
        "This repository drives Onshape over a logged-in browser session instead, which is",
        "unmetered and reports more. Use:",
        "",
        "    %s" % (replacement if replacement else
                   "the browser tooling in profiler-tools/ - see its README.md for the table"),
        "",
        "Run these with the Bash or PowerShell tool from the repository root. Sessions are",
        "short-lived, so expect an assisted login; ask the user to complete it rather than",
        "falling back to the MCP.",
        "",
        "If the browser route genuinely cannot do this, say so and ask the user to set",
        "ONSHAPE_MCP_OK=1 - do not work around this gate any other way.",
    ]
    return "\n".join(lines)


def hook_mode():
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError):
        return 0

    reason = decide(payload.get("tool_name") or "")
    if reason is None:
        return 0

    json.dump({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": reason,
        }
    }, sys.stdout)
    return 0


def main(argv):
    if len(argv) == 2 and argv[1] == "--hook":
        return hook_mode()
    sys.stderr.write(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
