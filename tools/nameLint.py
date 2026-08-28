#!/usr/bin/env python3
"""
Work-item naming linter for this repository.

One rule, stated in AGENTS.md and enforced here because prose is where it keeps getting broken:

  CODENAME   A unit of work referred to by an invented code rather than by what it is:
             a tier-and-number coordinate, a coded queue entry, or a bare tag. A code is
             meaningless to every reader who was not present when it was coined, it survives into
             later sessions as a referent nobody can resolve, and it hides the thing it names.
             Write what the work IS - name the job, not a slot in a list.

Codes are cheap to write and expensive to read. The cost lands on whoever picks the document up
next, which is why this is mechanical rather than advisory.

Domain notation that LOOKS like a code is allowed and listed in ALLOWED below: geometric and
parametric continuity (G1, C2), NURBS Book algorithm numbers (A2.3), FeatureScript version tags
(V647), and axis or parameter names (U0, V1). Section references (section 6.10, 11.14.1) are
never flagged - a section number is a location in a document, not the name of a job.

Two modes:

  python tools/nameLint.py FILE [FILE ...]   lint whole files
  python tools/nameLint.py --hook            read a Claude Code hook payload on stdin and lint the
                                             text the tool is about to write

Exit status is 0 when clean, 1 when a violation is found, and 2 on a usage error. In --hook mode a
violation is reported as a permission denial on stdout so the edit never lands.
"""

import json
import os
import re
import sys

LINTED_SUFFIXES = (".md", ".fs", ".py", ".mjs")

# Notation that is domain vocabulary rather than an invented label for a job.
ALLOWED = re.compile(
    r"^(?:"
    r"[GC][0-9]|"            # geometric / parametric continuity
    r"A[0-9]+(?:\.[0-9]+)?|" # NURBS Book algorithm numbers
    r"V[0-9]{2,}|"           # FeatureScript version tags
    r"[UVXYZ][0-9]|"         # axis and parameter names
    r"[0-9]+D"               # dimension counts, 2D / 3D
    r")$",
    re.IGNORECASE,
)

RULES = [
    # A label of the form <number><letter> used as an item reference. nameLint: allow
    (re.compile(r"\bitems?\s+[0-9]+[a-z]\b", re.IGNORECASE),
     "a work item referred to by a code",
     "name the job in place of the label"),
    # A tier-and-number coordinate standing in for a name. nameLint: allow
    (re.compile(r"\btiers?\s+[0-9]+\s+items?\s+[0-9]+", re.IGNORECASE),
     "a work item addressed by tier and number",
     "name the work, and let the tier say only when it happens"),
    # A list entry headed by a <number><letter> label. nameLint: allow
    (re.compile(r"^\s{0,4}[0-9]+[a-z][.)]\s+\S"),
     "a list entry labelled with a code",
     "head the entry with what it is; keep ordering in the list itself"),
    # Classic scheme tags used as standalone words. nameLint: allow
    (re.compile(r"\b(?:M|OQ|WP|TSK|EP|FR|REQ)[0-9]+\b"),
     "a bare-tag code scheme",
     "replace it with the name of the thing"),
]

SECTION_REFERENCE = re.compile(r"(?:§|\bsection\s+)[0-9]+(?:\.[0-9]+)*", re.IGNORECASE)

# A line may quote a code deliberately - the rule's own documentation has to show the bad form,
# and a dated record may need to reproduce a label that was already published. Such a line carries
# this marker, which makes the exception visible to the next reader instead of silent.
SUPPRESSION = re.compile(r"nameLint:\s*allow", re.IGNORECASE)


def lint(lines, path, fragment=False):
    """Return [(path, line_number, kind, message)] for `lines`."""
    findings = []
    for index, raw in enumerate(lines, start=1):
        if SUPPRESSION.search(raw):
            continue
        line = SECTION_REFERENCE.sub("", raw)
        for pattern, what, remedy in RULES:
            for match in pattern.finditer(line):
                token = match.group(0).strip()
                bare = token.rstrip(".)").split()[-1]
                if ALLOWED.match(bare):
                    continue
                findings.append((
                    path, index, "CODENAME",
                    "%s - %s. %s." % (token, what, remedy),
                ))
                break
    return findings


def is_std_mirror(path):
    """The repository root holds the mirrored standard library, which this rule does not govern."""
    normalized = path.replace("\\", "/")
    directory = os.path.dirname(normalized)
    return directory in ("", ".") or normalized.startswith("./") and "/" not in normalized[2:]


def report(findings):
    for path, line, kind, message in findings:
        sys.stderr.write("%s:%d: %s: %s\n" % (path, line, kind, message))


def hook_mode():
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError):
        return 0
    tool_input = payload.get("tool_input") or {}
    path = tool_input.get("file_path") or ""
    if os.path.splitext(path)[1].lower() not in LINTED_SUFFIXES:
        return 0
    if os.path.splitext(path)[1].lower() == ".fs" and is_std_mirror(path):
        return 0

    texts = []
    if "content" in tool_input:
        texts.append(tool_input["content"])
    if "new_string" in tool_input:
        texts.append(tool_input["new_string"])
    for edit in tool_input.get("edits") or []:
        if isinstance(edit, dict) and "new_string" in edit:
            texts.append(edit["new_string"])

    findings = []
    for text in texts:
        findings.extend(lint(text.splitlines(), os.path.basename(path)))
    if not findings:
        return 0

    lines = ["Blocked by tools/nameLint.py - name the work and retry the edit:"]
    for _path, line, kind, message in findings[:12]:
        lines.append("  [%s] line %d of the new text: %s" % (kind, line, message))
    json.dump({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": "\n".join(lines),
        }
    }, sys.stdout)
    return 0


def main(argv):
    if "--hook" in argv:
        return hook_mode()
    paths = [arg for arg in argv if not arg.startswith("-")]
    if not paths:
        sys.stderr.write("usage: nameLint.py FILE [FILE ...] | nameLint.py --hook\n")
        return 2
    findings = []
    for path in paths:
        with open(path, "r", encoding="utf-8", errors="replace") as handle:
            findings.extend(lint(handle.read().splitlines(), path))
    report(findings)
    sys.stderr.write("%d violation(s) in %d file(s)\n" % (len(findings), len(paths)))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
