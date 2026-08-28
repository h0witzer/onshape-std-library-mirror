#!/usr/bin/env python3
"""
FeatureScript source linter for this repository.

Two rules, both already stated in AGENTS.md and enforced here so a violation is caught before it
reaches Onshape or a commit:

  RESERVED   A FeatureScript keyword used as an identifier - a declared variable, a parameter, a
             function name, or a map field read through dot access. This is a compile failure in
             the element that contains it, and when other elements import that element the
             failure surfaces only as every feature erroring at regeneration.

  NARRATION  A comment that describes development history rather than current behaviour: failed
             approaches, discovery stories, or dated findings outside the header STATUS block
             (AGENTS.md line 56 - documentation explains what functions DO, not what they used
             to do or what they DON'T do).

Two modes:

  python tools/fsLint.py FILE [FILE ...]   lint whole files
  python tools/fsLint.py --hook            read a Claude Code hook payload on stdin and lint the
                                           text the tool is about to write

Exit status is 0 when clean, 1 when a violation is found, and 2 on a usage error. In --hook mode
a violation is reported as a permission denial on stdout so the edit never lands.
"""

import json
import os
import re
import sys

# The parser's own expected-token list, plus the collisions confirmed in this repository.
RESERVED = [
    "annotation", "as", "box", "const", "else", "enum", "export", "false", "for", "function",
    "if", "import", "in", "is", "new", "operator", "precondition", "predicate", "return",
    "boolean",
    "returns", "switch", "throw", "true", "try", "type", "typeconvert", "undefined", "var",
    "while",
]

_WORDS = "|".join(RESERVED)

# Each pattern captures the offending identifier in group "word" and names the position it was
# found in, because the fix differs: a declaration gets renamed, a dot access gets rewritten as a
# bracket read.
RESERVED_PATTERNS = [
    (re.compile(r"\b(?:const|var)\s+(?P<word>%s)\b" % _WORDS),
     "declared as a variable", "rename it"),
    (re.compile(r"\b(?:function|predicate)\s+(?P<word>%s)\s*\(" % _WORDS),
     "declared as a function or predicate name", "rename it"),
    (re.compile(r"[(,]\s*(?P<word>%s)\s+is\b" % _WORDS),
     "declared as a parameter", "rename it"),
    (re.compile(r"^\s*(?P<word>%s)\s+is\b" % _WORDS),
     "declared with a type", "rename it"),
    (re.compile(r"\.(?P<word>%s)\b" % _WORDS),
     "read as a map field through dot access", "read it as map['field'] instead"),
    (re.compile(r"^\s*(?P<word>%s)\s*=[^=]" % _WORDS),
     "assigned to", "rename it"),
]

# Replacement names for the keywords that read as ordinary nouns and so get chosen by accident.
RESERVED_HINTS = {
    "box": "what evBox3d returns is a regionBox, samplingBox or boundingBox",
    "operator": "an operator parameter becomes refinementOperator",
    "type": "a type field becomes clashType, surfaceType or faceType",
    "new": "a new value becomes updatedValue or nextValue",
    "in": "an in count becomes inboundCount, and a loop variable gets the item's own name",
    "is": "an is flag becomes isPlanar or hasSeam",
    "boolean": "a boolean field becomes booleanResult or booleanType (it parses as a type name)",
}

# Phrases that appear when a comment narrates history instead of describing behaviour.
NARRATION_PATTERNS = [
    (re.compile(r"\breserved (word|keyword)\b", re.I), "explains a language constraint we worked around"),
    (re.compile(r"\bNOT named\b"), "justifies a name by what it is not"),
    (re.compile(r"\bdoes ?n[o']?t (work|compile|parse)\b", re.I), "describes what does not work"),
    (re.compile(r"\bdid ?n[o']?t (work|compile|parse)\b", re.I), "describes what did not work"),
    (re.compile(r"\bfail(s|ed)? to (compile|parse)\b", re.I), "describes a compile failure"),
    (re.compile(r"\bsyntax error\b", re.I), "describes an error instead of behaviour"),
    (re.compile(r"\bwhich presents as\b", re.I), "narrates a symptom we debugged"),
    (re.compile(r"\bturn(s|ed) out\b", re.I), "narrates a discovery"),
    (re.compile(r"\b(we|i) (tried|measured|found|discovered|learned|hit)\b", re.I), "narrates our process"),
    (re.compile(r"\bconfirmed live\b", re.I), "records a test event"),
    (re.compile(r"\bhit (live|again)\b", re.I), "records a test event"),
    (re.compile(r"\blive finding\b", re.I), "records a finding"),
    (re.compile(r"\bearlier (attempt|version)\b", re.I), "describes a superseded approach"),
    (re.compile(r"\bthis (failed|broke)\b", re.I), "describes a past failure"),
    (re.compile(r"\bwork ?around\b", re.I), "frames the code as a workaround"),
    (re.compile(r"\bgotcha\b", re.I), "narrates a trap rather than the behaviour"),
    (re.compile(r"\bbeware\b", re.I), "warns instead of describing"),
    (re.compile(r"\bfor (some|whatever) reason\b", re.I), "narrates confusion"),
    (re.compile(r"\bapparently\b", re.I), "narrates uncertainty about our own code"),
]

DATE = re.compile(r"\b20\d\d-\d\d-\d\d\b")

STRING = re.compile(r'"(?:[^"\\]|\\.)*"')


def ternary_chain_lines(statement_parts):
    """Line indices where a `?` shares nesting depth with an earlier un-separated `?`.

    FeatureScript's conditional operator does not chain like C's: in `a ? x : b ? y : z` the
    second conditional is not grouped as the else-branch, and when a branch is a map literal the
    literal itself ends up evaluated as a condition, which fails at run time as an execution
    error with no location. A nested conditional is safe only inside parentheses, so two `?` at
    the same depth in one statement is the thing to flag. A `,` or `;` at that depth separates
    independent conditionals (argument lists), so it clears the pending marker.
    """
    findings = []
    depth = 0
    pending = {}
    for line_index, code in statement_parts:
        for ch in code:
            if ch in "([{":
                depth += 1
            elif ch in ")]}":
                depth = max(0, depth - 1)
                pending = {d: l for d, l in pending.items() if d <= depth}
            elif ch in ",;":
                pending = {d: l for d, l in pending.items() if d < depth}
            elif ch == "?":
                if depth in pending:
                    findings.append(line_index)
                    pending = {}
                else:
                    pending[depth] = line_index
    return findings


def classify_lines(lines):
    """Yield (index, is_comment, code_text, comment_text) for each line.

    Block-comment state carries across lines. A line beginning with `*` counts as comment
    continuation too, so a fragment taken from the middle of a doc comment is still recognised
    without its opening delimiter. String literals are blanked out of the code text so a keyword
    inside a quoted map key or a message is never read as an identifier.
    """
    in_block = False
    for index, line in enumerate(lines):
        code_parts = []
        comment_parts = []
        rest = line
        stripped = line.lstrip()
        if not in_block and stripped.startswith("*") and not stripped.startswith("*/"):
            comment_parts.append(line)
            rest = ""
        while rest:
            if in_block:
                end = rest.find("*/")
                if end == -1:
                    comment_parts.append(rest)
                    rest = ""
                else:
                    comment_parts.append(rest[:end])
                    rest = rest[end + 2:]
                    in_block = False
                continue
            block = rest.find("/*")
            line_comment = rest.find("//")
            if line_comment != -1 and (block == -1 or line_comment < block):
                code_parts.append(rest[:line_comment])
                comment_parts.append(rest[line_comment + 2:])
                rest = ""
            elif block != -1:
                code_parts.append(rest[:block])
                rest = rest[block + 2:]
                in_block = True
            else:
                code_parts.append(rest)
                rest = ""
        code = STRING.sub('""', "".join(code_parts))
        comment = "".join(comment_parts)
        yield index, bool(comment.strip()), code, comment


def header_block_end(lines):
    """Index one past the file's leading comment block, where dated STATUS lines are allowed."""
    end = 0
    for index, is_comment, code, _comment in classify_lines(lines):
        if code.strip():
            break
        if is_comment or not lines[index].strip():
            end = index + 1
    return end


def lint(lines, path, fragment=False):
    findings = []
    header_end = 0 if fragment else header_block_end(lines)
    status_block = fragment and any("STATUS" in line for line in lines)
    statement = []
    for index, is_comment, code, comment in classify_lines(lines):
        stripped_code = code.strip()
        if stripped_code:
            statement.append((index, code))
        if stripped_code.endswith(";") or stripped_code.endswith("{") or stripped_code.endswith("}"):
            for chain_line in ternary_chain_lines(statement):
                findings.append((
                    chain_line, "TERNARY",
                    "two `?` at the same nesting depth in one statement - FeatureScript's "
                    "conditional does not chain like C's, and a map-literal branch in the wrong "
                    "slot is evaluated as a condition and fails at run time with no location. "
                    "Parenthesize the nested conditional or use if/else.",
                ))
            statement = []
    for index, is_comment, code, comment in classify_lines(lines):
        for pattern, position, fix in RESERVED_PATTERNS:
            for match in pattern.finditer(code):
                word = match.group("word")
                hint = RESERVED_HINTS.get(word)
                findings.append((
                    index, "RESERVED",
                    "`%s` is a FeatureScript keyword and is %s here - %s%s." % (
                        word, position, fix, " (%s)" % hint if hint else ""),
                ))
        if not is_comment:
            continue
        for pattern, why in NARRATION_PATTERNS:
            match = pattern.search(comment)
            if match:
                findings.append((
                    index, "NARRATION",
                    'comment %s ("%s") - state only how the code behaves now; the story belongs '
                    "in docs/specs or memory." % (why, match.group(0).strip()),
                ))
        if index >= header_end and not status_block:
            match = DATE.search(comment)
            if match:
                findings.append((
                    index, "NARRATION",
                    "comment carries the date %s outside the header STATUS block - dated findings "
                    "belong in docs/specs, not beside the code." % match.group(0),
                ))
    return [(path, line + 1, kind, message) for line, kind, message in findings]


def report(findings, stream=sys.stderr):
    for path, line, kind, message in findings:
        stream.write("%s:%d: %s: %s\n" % (path, line, kind, message))


def is_std_mirror(path):
    """True for the standard library content imported from Onshape, which sits in the repo root
    beside AGENTS.md. Those files are not ours to rewrite, so the hook leaves them alone."""
    directory = os.path.dirname(os.path.abspath(path))
    return os.path.exists(os.path.join(directory, "AGENTS.md"))


def hook_mode():
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError):
        return 0
    tool_input = payload.get("tool_input") or {}
    path = tool_input.get("file_path") or ""
    if os.path.splitext(path)[1].lower() != ".fs" or is_std_mirror(path):
        return 0

    # Only the text this call would introduce is linted, so pre-existing violations elsewhere in
    # the file never block an unrelated edit.
    texts = []
    if "content" in tool_input:
        texts.append(tool_input["content"])
    if "new_string" in tool_input:
        texts.append(tool_input["new_string"])
    for edit in tool_input.get("edits") or []:
        if isinstance(edit, dict) and "new_string" in edit:
            texts.append(edit["new_string"])

    findings = []
    whole_file = "content" in tool_input
    for text in texts:
        findings.extend(lint(text.splitlines(), os.path.basename(path), fragment=not whole_file))
    if not findings:
        return 0

    lines = ["Blocked by tools/fsLint.py - fix the text and retry the edit:"]
    for _path, line, kind, message in findings:
        lines.append("  [%s] line %d of the new text: %s" % (kind, line, message))
    json.dump({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": "\n".join(lines),
        }
    }, sys.stdout)
    return 0


def changed_mode():
    """Advisory pass over every .fs file git reports as changed.

    The PreToolUse hook only sees Write/Edit/MultiEdit, so a file rewritten through a shell
    command reaches disk unchecked. This runs at the end of a turn and reports what landed
    rather than blocking, since a legacy file may carry violations that predate the edit.
    """
    import subprocess

    try:
        tracked = subprocess.run(["git", "diff", "--name-only", "HEAD"],
                                 capture_output=True, text=True, timeout=15)
        untracked = subprocess.run(["git", "ls-files", "--others", "--exclude-standard"],
                                   capture_output=True, text=True, timeout=15)
    except (OSError, subprocess.SubprocessError):
        return 0

    paths = []
    for line in (tracked.stdout + untracked.stdout).splitlines():
        name = line.strip()
        if name.endswith(".fs") and os.path.exists(name) and not is_std_mirror(name):
            paths.append(name)

    findings = []
    for path in paths:
        with open(path, "r", encoding="utf-8", errors="replace") as handle:
            findings.extend(lint(handle.read().splitlines(), path))
    if not findings:
        return 0

    lines = ["tools/fsLint.py found %d violation(s) in changed FeatureScript:" % len(findings)]
    for path, line, kind, message in findings[:12]:
        lines.append("  %s:%d [%s] %s" % (path, line, kind, message))
    if len(findings) > 12:
        lines.append("  ... and %d more - run python tools/fsLint.py on the file for the rest."
                     % (len(findings) - 12))
    json.dump({"systemMessage": "\n".join(lines)}, sys.stdout)
    return 0


def main(argv):
    if "--hook" in argv:
        return hook_mode()
    if "--changed" in argv:
        return changed_mode()
    paths = [arg for arg in argv if not arg.startswith("-")]
    if not paths:
        sys.stderr.write("usage: fsLint.py FILE [FILE ...] | fsLint.py --hook\n")
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
