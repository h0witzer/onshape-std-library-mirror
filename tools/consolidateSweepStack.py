"""
HISTORICAL, ran once on 2026-08-23: its seventeen inputs no longer exist, so it will not run
again. Kept because it is the record of how the split was computed, and because the same rule is
what any future re-split should use.

Collapse the solid sweep's seventeen source files into the three the stack is meant to have:

    custom-features/solidSweepUtils.fs    every library declaration, one file, no test code
    custom-features/solidSweepTester.fs   every test feature and every fixture it needs
    custom-features/solidSweep.fs         the feature itself (hand written, never touched here)

The split is computed, not hand-listed. A declaration belongs to the LIBRARY if it is reachable
from an exported declaration of a library module; everything else that a test feature reaches is
a FIXTURE and moves to the tester. Anything reachable from neither is dead and is reported.

Two consequences worth knowing:

  * A library helper that a fixture calls has to be exported once it lives in another element, so
    the script exports them and says which ones it promoted.
  * Emission order is enums, then constants, then functions. Functions hoist; a top-level const
    that reads another const does not, and the source modules do not agree on where they put
    them.

Usage: python tools/consolidateSweepStack.py [--write]
Without --write it reports the split and touches nothing.
"""

import io, os, re, sys

CF = "c:/Github/Onshape Featurescript Repository/onshape-std-library-mirror/custom-features"

# Library modules, in dependency order - the merged file keeps this order inside each kind.
LIB_MODULES = [
    ("bernsteinPolynomialUtils.fs", "Bernstein coefficient arithmetic (spec 6.0)"),
    ("swMotionSpline.fs", "Motion (spec 4)"),
    ("swEnvelopeMath.fs", "Envelope function layer (spec 6.1-6.2)"),
    ("swAnalyticContact.fs", "Closed-form contact for analytic faces (spec 6.5)"),
    ("swFunnelSolver.fs", "Funnel solver, masks and certified census (spec 6.3-6.4, 6.7-6.8)"),
    ("swOrientation.fs", "Orientation and the fold certificate (spec 6.6)"),
    ("swDegeneracy.fs", "Degeneracy detectors (spec 6.9)"),
    ("swSweepEmit.fs", "Extraction, caps, knit, assembly (spec 5 and 9)"),
    ("swEnvelopeFit.fs", "Fitting and certification (spec 7)"),
]

# Test-only modules. Everything in them is test code by definition.
TEST_MODULES = [
    ("swTestHarness.fs", "Verdict reporting, check tallies, shared fixtures"),
    ("bernsteinPolynomialUtilsTester.fs", "Bernstein arithmetic"),
    ("swMotionSplineTester.fs", "Motion"),
    ("swAnalyticContactTester.fs", "Analytic contact"),
    ("swOrientationTester.fs", "Orientation"),
    ("swDegeneracyTester.fs", "Degeneracy detectors"),
    ("swTrimLoopTester.fs", "Trim loops, live"),
]

# Retired: every probe question is answered and the answers are in spec section 13. The file also
# carried its own copies of three swSweepEmit helpers, which is the duplication this pass exists
# to end.
RETIRED_MODULES = ["swSweepProbes.fs"]

UTILS_PATH = os.path.join(CF, "solidSweepUtils.fs")
TESTER_PATH = os.path.join(CF, "solidSweepTester.fs")

VERSION = "3044"
# geometry.fs as well as common.fs: ProjectionType (used by the imprint) is re-exported only by
# projectCurves.fs and splitpart.fs, so common.fs alone does not resolve it. Found the hard way
# 2026-08-24 - a name that does not resolve degrades to a missing operation, not a compile error.
STD_IMPORTS = [
    'import(path : "onshape/std/common.fs", version : "3044.0");',
    'import(path : "onshape/std/geometry.fs", version : "3044.0");',
    'import(path : "onshape/std/surfaceGeometry.fs", version : "3044.0");',
    'import(path : "onshape/std/curveGeometry.fs", version : "3044.0");',
]
SPLINE_REFINEMENT_IMPORT = (
    'import(path : "eca0e7b6ed29c5239f39f868/c6d53360a1b2036a47b2b076/9a2b77793cdc37bace6d915a", '
    'version : "a0777a349ec1b79fe71095ce"); //splineRefinementUtils.fs'
)
# Filled in with the real element id once the utils tab exists in the document.
UTILS_IMPORT_PLACEHOLDER = '// EXPORT import, not a plain one: MotionFrameSource is used as a dialog parameter type in the
// motion tester below, and an enum behind a plain import is not exported into this element's
// namespace - the UI then refuses the parameter with "Enum used as parameter type must be
// exported". Same-document import, so only the PATH needs fixing on paste; Onshape rewrites
// the version to the tab's latest microversion on commit.
export import(path : "PASTE_THE_SOLIDSWEEPUTILS_TAB_ID_HERE", version : "0000000000000000000000ff"); //solidSweepUtils.fs'

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


def doc_start(src, at):
    """Include an immediately preceding /** */ block or // comment run."""
    head = src[:at]
    m = re.search(r'(/\*\*(?:(?!\*/)[\s\S])*\*/\s*)$', head)
    if m:
        return m.start(1)
    m = re.search(r'((?:^[ \t]*//[^\n]*\n)+)\Z', head, re.M)
    return m.start(1) if m else at


def code_idents(text):
    stripped = re.sub(r'/\*[\s\S]*?\*/', ' ', text)
    stripped = re.sub(r'//[^\n]*', ' ', stripped)
    return set(IDENT.findall(stripped))


def collect(path):
    """(declarations, features) of one module.

    A declaration is (kind, name, text, exported); a feature is (name, text). Feature bodies are
    cut out of the source before declarations are scanned, so a function declared inside nothing
    but a feature's own text is never mistaken for a top-level one."""
    src = io.open(path, encoding="utf-8").read()
    features, spans = [], []

    for m in FEATURE_START.finditer(src):
        nm = re.compile(r'export const (\w+)\s*=\s*defineFeature\s*\(').search(src, m.end())
        if not nm:
            continue
        end = paren_match(src, nm.end() - 1)
        semi = src.find(';', end)
        end = semi + 1 if 0 <= semi <= end + 3 else end
        start = doc_start(src, m.start())
        features.append((nm.group(1), src[start:end]))
        spans.append((start, end))

    # Blank the feature spans so the declaration scan cannot reach inside them.
    masked = list(src)
    for start, end in spans:
        for i in range(start, end):
            if masked[i] != '\n':
                masked[i] = ' '
    masked = "".join(masked)

    decls = []
    for m in re.finditer(r'^(export )?function (\w+)\(', masked, re.M):
        end = brace_match(masked, m.start())
        decls.append(("function", m.group(2), src[doc_start(src, m.start()):end], bool(m.group(1))))
    for m in re.finditer(r'^(export )?enum (\w+)', masked, re.M):
        end = brace_match(masked, m.start())
        decls.append(("enum", m.group(2), src[doc_start(src, m.start()):end], bool(m.group(1))))
    for m in re.finditer(r'^(export )?const ([A-Za-z_]\w*)\s*=', masked, re.M):
        end = masked.find(';', m.start())
        if end < 0:
            continue
        text = src[m.start():end + 1]
        if 'defineFeature' in text:
            continue
        decls.append(("const", m.group(2), src[doc_start(src, m.start()):end + 1], bool(m.group(1))))
    return decls, features


def main():
    write = "--write" in sys.argv

    table = {}          # name -> list of (kind, text, module, exported)
    order = []          # (module, name, kind) in source order
    features = []       # (module, name, text)
    lib_names, test_names = set(), set()

    for module, _ in LIB_MODULES + TEST_MODULES:
        path = os.path.join(CF, module)
        decls, feats = collect(path)
        is_lib = any(module == m for m, _ in LIB_MODULES)
        for kind, name, text, exported in decls:
            table.setdefault(name, []).append((kind, text, module, exported))
            order.append((module, name, kind))
            (lib_names if is_lib else test_names).add(name)
        for name, text in feats:
            features.append((module, name, text))
        print("  %-36s %3d decl(s), %d feature(s)" % (module, len(decls), len(feats)))

    def closure(seeds):
        seen, queue = set(), list(seeds)
        while queue:
            name = queue.pop()
            if name in seen or name not in table:
                continue
            seen.add(name)
            for _, text, _, _ in table[name]:
                queue.extend(code_idents(text))
        return seen

    lib_exports = [n for n in lib_names
                   if any(exported and module in [m for m, _ in LIB_MODULES]
                          for _, _, module, exported in table[n])]
    lib_set = closure(lib_exports) & lib_names

    feature_seeds = set()
    for _, _, text in features:
        feature_seeds |= code_idents(text)
    test_set = (closure(feature_seeds) | test_names) - lib_set

    # A library declaration reachable from neither is dead weight.
    dead = sorted(lib_names - lib_set - test_set)

    # Anything on the utils side that the tester side names has to be exported to cross the
    # element boundary.
    tester_text = "\n".join(t for _, _, t in features)
    for name in sorted(test_set):
        for _, text, _, _ in table.get(name, []):
            tester_text += "\n" + text
    tester_idents = code_idents(tester_text)
    promote = sorted(n for n in lib_set
                     if n in tester_idents and not all(e for _, _, _, e in table[n]))

    # Layering check: a library export must not reach into a test module.
    leaks = sorted(n for n in lib_set if n in test_names)

    print("\n  library declarations : %d" % len(lib_set))
    print("  test declarations    : %d" % len(test_set))
    print("  features             : %d" % len(features))
    print("  exports promoted     : %d %s" % (len(promote), promote if promote else ""))
    print("  layering leaks       : %d %s" % (len(leaks), leaks if leaks else ""))
    print("  DEAD (in neither)    : %d" % len(dead))
    for name in dead:
        print("      %s (%s)" % (name, ", ".join(sorted({m for _, _, m, _ in table[name]}))))

    def emit(names, modules, banner_of, header):
        # Enums and constants are small and few; grouping them under one banner each reads far
        # better than repeating a module banner for every two-line declaration. Functions keep
        # per-module banners, which is what makes an eleven-thousand-line file navigable.
        out = list(header)
        for kind in ("enum", "const", "function"):
            current = None
            present = any(k == kind and n in names and m in [x for x, _ in modules]
                          for m, n, k in order)
            if kind != "function" and present:
                out.append(chr(10) + "// ============================= " +
                           ("Enumerations" if kind == "enum" else "Constants and tolerances") +
                           " =============================" + chr(10))
            for module, name, decl_kind in order:
                if decl_kind != kind or name not in names:
                    continue
                if module not in [m for m, _ in modules]:
                    continue
                texts = [(t, e) for k, t, m, e in table[name] if m == module and k == kind]
                if not texts:
                    continue
                del table[name][:]  # emitted once, whichever kind it was found under
                if kind == "function" and module != current:
                    current = module
                    out.append("\n// ============================= %s =============================\n"
                               % banner_of[module])
                for text, exported in texts:
                    body = text.strip("\n")
                    if name in promote and not exported:
                        body = re.sub(r'^(function |const |enum )', r'export \1', body, count=1, flags=re.M)
                    out.append(body + "\n")
        return out

    if not write:
        print("\n  (dry run - pass --write to produce the two files)")
        return 0

    banner = {m: b for m, b in LIB_MODULES + TEST_MODULES}

    utils_header = [
        "FeatureScript %s;" % VERSION,
    ] + STD_IMPORTS + ["", SPLINE_REFINEMENT_IMPORT, "", UTILS_DOC, ""]
    utils_out = emit(lib_set, LIB_MODULES, banner, utils_header)

    tester_header = [
        "FeatureScript %s;" % VERSION,
    ] + STD_IMPORTS + ["", SPLINE_REFINEMENT_IMPORT, UTILS_IMPORT_PLACEHOLDER, "", TESTER_DOC, ""]
    tester_out = emit(test_set, LIB_MODULES + TEST_MODULES, banner, tester_header)
    tester_out.append("\n// ============================= Test features =============================\n")
    for module, name, text in features:
        tester_out.append(text.strip("\n") + "\n")

    io.open(UTILS_PATH, "w", encoding="utf-8", newline="\n").write("\n".join(utils_out))
    io.open(TESTER_PATH, "w", encoding="utf-8", newline="\n").write("\n".join(tester_out))
    for path in (UTILS_PATH, TESTER_PATH):
        text = io.open(path, encoding="utf-8").read()
        print("  wrote %-46s %5d lines, %5.1f KB" % (os.path.basename(path), text.count("\n"),
                                                     len(text.encode("utf-8")) / 1024.0))
    return 0


UTILS_DOC = '''/**
 * SOLID SWEEP - the whole library, one element (spec: docs/specs/SOLID_SWEEP_SPEC.md).
 *
 * Motion, the envelope function, the funnel solver, orientation, the degeneracy detectors,
 * extraction, fitting, and emission. No test code and no fixtures live here: those are in
 * solidSweepTester.fs, which imports this element.
 *
 * Units contract, uniform across the file: every spline stored or passed between these
 * functions is unit-stripped - control points are plain numbers with meters implied - and every
 * residual and tolerance is a plain number in meters. Units are attached only where a kernel
 * call demands them, at emission and at the ev-call boundary.
 *
 * Assembled by tools/consolidateSweepStack.py from the modules the stack was developed in; edit
 * this file directly from here on.
 */'''

TESTER_DOC = '''/**
 * SOLID SWEEP - the test suite, one element (spec: docs/specs/SOLID_SWEEP_SPEC.md section 14).
 *
 * Every self test, live test, and fixture for solidSweepUtils.fs. Selection-free by
 * construction: each feature builds whatever geometry it needs, so any of them can be inserted
 * into an empty Part Studio, or executed by the MCP harness, without picking anything.
 *
 * This file is meant to be CYCLED. A test earns its place by defending an invariant the current
 * work can still break; once a layer is finished and its numbers are recorded in the spec, its
 * test either becomes a regression check for the layer above or it goes.
 *
 * Assembled by tools/consolidateSweepStack.py; edit this file directly from here on.
 */'''

sys.exit(main())
