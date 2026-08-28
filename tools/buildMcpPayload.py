"""
Build a MINIMAL self-contained FeatureScript payload for an MCP `test_feature` run.

Same idea as buildpayload.py, plus reachability pruning: start from the requested feature, walk
the call graph over the sweep modules' top-level declarations, and emit only what is reachable.
Everything the payload does not define is assumed to come from std or splineRefinementUtils
(cross-document imports resolve in the MCP scratch document; same-document ones never do).

Usage: python buildpayload2.py <feature name> [more...]
"""

import io, os, re, sys

CF = "c:/Github/Onshape Featurescript Repository/onshape-std-library-mirror/custom-features"

# The whole stack is two files now (tools/consolidateSweepStack.py). This tool survives the
# consolidation because the MCP harness runs in the server's OWN scratch document, where a
# same-document import of the utils tab cannot resolve - so a harness run still needs one
# self-contained, reachability-pruned file.
MODULES = [
    "solidSweepUtils.fs",
    "solidSweepTester.fs",
]

KEEP_IMPORTS = [
    'import(path : "onshape/std/common.fs", version : "3044.0");',
    # geometry.fs, not just common.fs: ProjectionType is re-exported by projectCurves.fs and
    # splitpart.fs alone, and an unresolved name in FeatureScript degrades to a missing OPERATION
    # rather than a compile error, so leaving it out shows up as geometry that silently fails to
    # draw.
    'import(path : "onshape/std/geometry.fs", version : "3044.0");',
    'import(path : "onshape/std/surfaceGeometry.fs", version : "3044.0");',
    'import(path : "onshape/std/curveGeometry.fs", version : "3044.0");',
    'import(path : "eca0e7b6ed29c5239f39f868/c6d53360a1b2036a47b2b076/9a2b77793cdc37bace6d915a", '
    'version : "a0777a349ec1b79fe71095ce");',
]

FEATURE_START = re.compile(r'^annotation \{ "Feature Type Name" : "([^"]+)" \}', re.M)
IDENT = re.compile(r'\b([A-Za-z_][A-Za-z0-9_]*)\b')


def skip_ahead(src, i):
    """Advance past a string literal or comment starting at i; else return i unchanged."""
    if src[i] == '"':
        i += 1
        while i < len(src) and src[i] != '"':
            i += 2 if src[i] == '\\' else 1
        return i + 1
    if src[i:i + 2] == '//':
        j = src.find('\n', i)
        return len(src) if j < 0 else j
    if src[i:i + 2] == '/*':
        j = src.find('*/', i)
        return len(src) if j < 0 else j + 2
    return i


def paren_match(src, open_index):
    """Index just past the ')' matching the '(' at open_index."""
    depth, i = 0, open_index
    while i < len(src):
        j = skip_ahead(src, i)
        if j != i:
            i = j
            continue
        if src[i] == '(':
            depth += 1
        elif src[i] == ')':
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return len(src)


def brace_match(src, start):
    depth, i, seen = 0, start, False
    while i < len(src):
        j = skip_ahead(src, i)
        if j != i:
            i = j
            continue
        ch = src[i]
        if ch == '{':
            depth += 1
            seen = True
        elif ch == '}':
            depth -= 1
            if seen and depth == 0:
                k = src.find(';', i)
                return (k + 1) if 0 <= k <= i + 3 else i + 1
        i += 1
    return len(src)


def minify(src):
    """Drop comments, blank lines and leading indentation, preserving string literals exactly.
    FeatureScript is not whitespace sensitive, so this changes nothing but the token cost of
    shipping the payload through a tool call. Character-scanned rather than regexed, because a
    string containing // or /* would otherwise be mangled."""
    out, i = [], 0
    while i < len(src):
        ch = src[i]
        if ch == '"':                                   # copy the literal verbatim
            j = i + 1
            while j < len(src) and src[j] != '"':
                j += 2 if src[j] == '\\' else 1
            out.append(src[i:j + 1])
            i = j + 1
        elif src[i:i + 2] == '//':
            j = src.find('\n', i)
            i = len(src) if j < 0 else j
        elif src[i:i + 2] == '/*':
            j = src.find('*/', i)
            i = len(src) if j < 0 else j + 2
        else:
            out.append(ch)
            i += 1
    lines = ("".join(out)).split("\n")
    return "\n".join(l.strip() for l in lines if l.strip()) + "\n"


def doc_start(src, at):
    """Include an immediately preceding /** */ block or // comment run."""
    head = src[:at]
    m = re.search(r'(/\*\*(?:(?!\*/)[\s\S])*\*/\s*)$', head)
    if m:
        return m.start(1)
    # \Z, not $: with re.M a `$` matches at EVERY line end, so this found the FIRST // run in
    # the file and swallowed everything from there down to the declaration - 2682 lines for a
    # twenty-line corrector. Only a run that ENDS where the declaration begins is its comment.
    m = re.search(r'((?:^[ \t]*//[^\n]*\n)+)\Z', head, re.M)
    return m.start(1) if m else at


def collect(path):
    """Top-level declarations of one module: list of (kind, name, text)."""
    src = io.open(path, encoding="utf-8").read()
    decls, features = [], {}

    for m in FEATURE_START.finditer(src):
        # Brace matching from the annotation would close on the annotation's OWN braces, so find
        # the defineFeature call that follows it and match its PARENTHESES instead.
        nm = re.compile(r'export const (\w+)\s*=\s*defineFeature\s*\(').search(src, m.end())
        if not nm:
            continue
        end = paren_match(src, nm.end() - 1)
        semi = src.find(';', end)
        features[nm.group(1)] = src[m.start():(semi + 1 if 0 <= semi <= end + 3 else end)]

    for m in re.finditer(r'^(?:export )?function (\w+)\(', src, re.M):
        end = brace_match(src, m.start())
        decls.append(("function", m.group(1), src[doc_start(src, m.start()):end]))

    # Enums are brace-matched like functions. They were missed entirely until 2026-08-23, when a
    # payload came out referring to SweepSurfaceClass without carrying it - the reachability walk
    # was right, the collector simply never saw the declaration. Types and predicates have no
    # instances in these modules yet; add them here the same way when they appear.
    for m in re.finditer(r'^(?:export )?enum (\w+)', src, re.M):
        end = brace_match(src, m.start())
        decls.append(("enum", m.group(1), src[doc_start(src, m.start()):end]))

    # Constants are taken WITHOUT the doc-comment lookback: a const is a line or two, and
    # letting doc_start reach backwards here swallowed whole files (3167 lines for a 1e-4).
    for m in re.finditer(r'^(?:export )?const ([A-Za-z_]\w*)\s*=', src, re.M):
        end = src.find(';', m.start())
        if end < 0:
            continue
        text = src[m.start():end + 1]
        if 'defineFeature' in text or text.count('\n') > 6:
            continue                                    # a feature, or a big literal map
        decls.append(("const", m.group(1), text))

    return decls, features


def main():
    wanted = sys.argv[1:]
    if not wanted:
        print(__doc__)
        return 1

    table, order, features = {}, [], {}
    for name in MODULES:
        p = os.path.join(CF, name)
        if not os.path.exists(p):
            continue
        decls, feats = collect(p)
        features.update(feats)
        for kind, dname, text in decls:
            table.setdefault(dname, []).append(text)
            order.append((name, dname, kind))
        print("  %-34s %4d decl(s), %d feature(s)" % (name, len(decls), len(feats)))

    missing = [w for w in wanted if w not in features]
    if missing:
        print("\n  !! not found: %s\n     available: %s"
              % (", ".join(missing), ", ".join(sorted(features))))
        return 1

    # Reachability from the requested features. Identifiers are read from CODE only - a prose
    # mention of another function in a doc comment is not a call, and counting them pulled in
    # most of the stack (2633 lines against 1174 for the same test).
    def code_idents(text):
        stripped = re.sub(r'/\*[\s\S]*?\*/', ' ', text)
        stripped = re.sub(r'//[^\n]*', ' ', stripped)
        return IDENT.findall(stripped)

    seen, queue = set(), []
    for w in wanted:
        queue.extend(code_idents(features[w]))
    while queue:
        nm = queue.pop()
        if nm in seen or nm not in table:
            continue
        seen.add(nm)
        for text in table[nm]:
            queue.extend(code_idents(text))

    # Enums, then constants, then functions: a module's own text may define any of them below
    # the code that reads them, and the merged payload must not depend on top-level resolution
    # order.
    emitted, out, current = set(), [], None
    for wanted_kind in ("enum", "const", "function"):
        for mod, dname, kind in order:
            if kind != wanted_kind or dname not in seen or dname in emitted:
                continue
            emitted.add(dname)
            label = "%s %ss" % (mod, wanted_kind)
            if label != current:
                out.append("\n// ===================== %s =====================" % label)
                current = label
            out.extend(t.strip("\n") for t in table[dname])

    parts = ["FeatureScript 3044;"] + KEEP_IMPORTS + out + [""]
    parts += [features[w].strip("\n") for w in wanted]
    payload = "\n".join(parts) + "\n"
    io.open("payload.fs", "w", encoding="utf-8", newline="\n").write(payload)
    minified = minify(payload)
    io.open("payload.min.fs", "w", encoding="utf-8", newline="\n").write(minified)
    print("  payload.min.fs: %d lines, %.1f KB"
          % (minified.count("\n"), len(minified.encode("utf-8")) / 1024.0))
    print("\n  reachable: %d of %d declarations" % (len(emitted), len(table)))
    print("  payload.fs: %d lines, %.1f KB" % (payload.count("\n"), len(payload.encode("utf-8")) / 1024.0))
    unresolved = sorted(n for n in seen if n not in table)
    return 0


sys.exit(main())
