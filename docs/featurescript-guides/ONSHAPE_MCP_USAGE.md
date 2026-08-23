# Onshape FeatureScript MCP Usage

Onshape hosts a FeatureScript MCP server that lets an agent read, edit, and test features in
live Onshape documents. This repository is connected to it — and the connection is a metered
resource, not a workspace. **Every MCP tool call spends real Onshape API calls from the
account it is authenticated against**, and a single tool invocation can fan out into many API
calls behind the scenes. The community consensus matches our expectation: casual MCP use
burns through API budgets fast.

This repo got everything built and validated so far — sweeps, FFD, refinement kernels, sheet
metal features — **without the MCP at all**, working from the mirrored standard library and
FsDoc. That remains the normal way to work. The MCP exists for one job: a targeted live test
of work that is already finished locally.

## Connection details

| What | Value |
| --- | --- |
| Server name | `onshape-featurescript` |
| Endpoint | `https://fs-mcp.labs.onshape.app/mcp` (HTTP transport) |
| Config location | `.mcp.json` at the repository root (project scope) |
| Authentication | OAuth, per user — run `/mcp` in an interactive Claude Code session and complete the browser sign-in |
| Current account | The **API developer account**, temporarily. This is a deliberate choice to sandbox the budget cost; do not assume it is the main account, and do not switch accounts without being asked. |

First use in a session may prompt to approve the project-scope server — that approval is
per-user and expected. If the server shows as needing authentication, tell the user to run
`/mcp` interactively; agents cannot complete the OAuth flow themselves.

A session started before the server connected cannot see its tools (the tool roster is fixed
at session start). Workaround verified 2026-08-22: drive the connected CLI headlessly — the
VSCode extension bundles it at
`%USERPROFILE%\.vscode\extensions\anthropic.claude-code-<version>-win32-x64\resources\native-binary\claude.exe`
— with `claude.exe -p "<one tightly scoped instruction>" --allowedTools "Read,mcp__onshape-featurescript" --max-turns 10`
run from the repository directory. Scope the prompt to the exact calls intended, nothing more.
Pass code through a file the agent Reads, never inline in the prompt (shell quoting mangles
it), and give `--max-turns` headroom (10+): a run that exhausts its turns may already have
executed — and billed — its tool call without reporting the result.

Performance doctrine (learned 2026-08-22 the hard way): the eval response carries no timing
and the harness round-trip wall clock HIDES regen time inside write+compile+network — a
14.84 s regen looked like "fast" from outside. Never characterize performance from harness
round trips. The owner's UI profiler (FS profiler / feature compute time in their document)
is the only per-function truth; the loop is repo → owner pastes → owner reads profiler.

Payload-integrity rule (measured 2026-08-22): the headless sub-agent RETYPES the file into
the tool call. At ~65 KB it dropped the last third once in four runs; the signature is a
cascade of "Function X not found" notices for everything defined after the cut. Verify by
comparing the sent code length in the transcript against the file size. Keep harness payloads
well under ~50 KB, or hand big modules to the owner-paste loop instead.

Transcript locations: headless transcripts land under the project directory of the LAUNCH
working directory — a cwd that drifted into a subfolder files them under that subfolder's
munged path, so search `%USERPROFILE%\.claude\projects\` broadly by modification time.

Two response-recovery rules (learned 2026-08-22):

- **A `test_feature` response with ANY evaluation notice returns the notices INSTEAD of the
  console output** — even INFO-level notices from kernel calls the code caught in `try`
  (measured: 4 caught `evEdgeConvexity: BAD_GEOMETRY` INFO notices hid a completed run's
  entire console). Payloads must be notice-clean: guard against asking the kernel questions
  it will complain about (e.g. convexity of a sheet-boundary edge) rather than try-catching
  them.
- **Never re-run to recover a lost response.** Every headless run's full tool results are in
  its local transcript under `%USERPROFILE%\.claude\projects\<munged-repo-path>\*.jsonl` —
  when the sub-agent under-reports, read the newest transcript instead of paying ~13 calls
  for a repeat.

## Tool roster (verified live 2026-08-22)

| Tool | What it does | Regimen category |
| --- | --- | --- |
| `get_api_usage` | Report API usage and remaining allocation | Metered like everything else — checking the count costs calls (~1–2 per check, measured 2026-08-22). At most once per session; never poll it. |
| `set_api_allocation` | Choose which account's allocation to consume | Only when the user asks to switch accounts |
| `list_feature_studios` | List Feature Studios in a document workspace | Targeted read |
| `get_featurescript` | Read a Feature Studio's source | Targeted read |
| `put_featurescript` | Write source to a Feature Studio | The paste-and-report replacement |
| `create_feature_studio` | Create a new Feature Studio element | Targeted write |
| `test_feature` / `test_featurescript` | Execute a feature / snippet in a real Part Studio | The live test itself |
| `create_geometry` | Build geometry in a branched workspace | Targeted live test |
| `read_featurescript_notes` / `edit_featurescript_notes` | Server-side persistent notes | Rarely — this repo's specs and memory are the record |
| `search_featurescript_documentation` | Search FS documentation | **Never — the mirror and FsDoc answer this for free** |
| `logout` | Sign out of the MCP server | Only when the user asks |

## The regimen: offline by default, MCP only to test live

A session that never touches the MCP is the default, expected outcome. Reach for it only
when local work is done and the one remaining question is "does this behave correctly in a
real Onshape document?"

Where each kind of work belongs:

| Task | Where it happens |
| --- | --- |
| Function lookup — signatures, behavior, existence of an `op*`/`ev*` | Mirrored `.fs` files in the repo root, and FsDoc (`https://cad.onshape.com/FsDoc/`) |
| Drafting and reviewing feature code | Locally, validated against the mirror per `AGENTS.md` |
| Syntax, precondition, and type questions | Mirror + FsDoc lexical reference |
| Iterating on build errors | Locally — fix against the mirror, **then** one live re-test |
| Live regeneration test of a finished draft | MCP — targeted, minimal |
| Reading real document state that cannot be inferred locally | MCP — targeted, minimal |
| API usage / quota questions | One `get_api_usage` at most per session — the check itself is metered |

There are no free MCP interactions, and the cost per interaction is variable. Everything that
touches the service consumes API calls: tool invocations, the usage check, and even connection
handshakes and tool discovery/enumeration (measured 2026-08-22: one session of two scoped tool
calls plus schema discovery consumed ~21 calls). A headless agent pays the handshake on every
launch, so batch work into the fewest runs. When a burn solves a hard problem, bank the result
locally the same day — schemas, response shapes, and findings go into `docs/` and memory so
they are never purchased twice.

Measured costs (2026-08-22): **one `test_feature` run ≈ 13 API calls** (measured twice at 13).
A headless run that exhausts `--max-turns` after invoking the tool still bills the full ~13
with nothing reported — sizing the turn budget generously is a cost control, not a
convenience. Budget a fail-fix-rerun cycle at ~26 calls and make every payload worth it:
validate against the mirror first, and put the diagnostics for the *next* likely failure into
the payload's printouts so one run answers more than one question.

## Where each tool acts

`test_feature`, `test_featurescript`, and `create_geometry` run inside the MCP service's OWN
harness document (server-configured; responses expose only its microversion ids). They never
read or write any user document, and their results are invisible in Onshape — the console
text in the response is the only artifact, so bank it. Only `get_featurescript`,
`put_featurescript`, `create_feature_studio`, and `list_feature_studios` touch a user
document, and only when given its explicit document/workspace/element ids. Keeping a user
studio in sync with the repository is therefore a deliberate, separately-billed step — or a
free manual paste by the owner.

## Rules, stated plainly

1. **Exhaust the mirror first.** If a question can be answered by searching the mirrored
   standard library or FsDoc, answering it through the MCP is a wasted API call. Reference
   lookups through the MCP are never justified.
2. **Arrive with finished work.** Before the first MCP call, the code is fully drafted,
   validated against the mirror, and the exact tool calls needed are known. The MCP is where
   testing ends, not where development happens.
3. **One purpose per MCP session.** Decide what single question the live test answers, make
   the fewest calls that answer it, and stop. Batch related reads into one call when the
   tools allow it.
4. **No iteration loops through the MCP.** If a live test fails, bring the failure back,
   diagnose and fix locally against the mirror, then make one targeted re-test. Do not
   round-trip syntax fixes or trial-and-error changes through MCP tools.
5. **No polling.** Never loop an MCP call waiting for state to change.
6. **On rate or quota errors, stop and report.** Do not retry-loop against a budget error;
   surface it to the user and wait.
7. **A clean MCP response is evidence, not success.** The testing rules in `AGENTS.md` still
   stand: do not declare a feature working without the user's confirmation that the build
   passes their checks.

## Why this is written down

The MCP makes it *feel* free to ask Onshape things directly, and agents drift toward the
convenient path. The mirror is local, free, and version-pinned to the same library the MCP
would consult — there is no information advantage to burning API calls on lookups. Keep the
MCP for the one thing the mirror cannot do: run the code for real.
