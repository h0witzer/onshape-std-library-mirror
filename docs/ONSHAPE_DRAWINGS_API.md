# Onshape Drawings API — browser-session driver

Tooling in [`drawing-tools/`](../drawing-tools/) creates and annotates Onshape **drawings**
through the REST API, driven from a logged-in browser session. It is the drawing counterpart to
the std-library mirror updater: same Playwright session-cookie approach, same reason — session
calls are not metered against the Onshape API allocation.

The FeatureScript MCP server cannot do any of this. It exposes Feature Studio I/O and three
FeatureScript execution harnesses; there is no drawings surface, and FeatureScript itself runs
only in Part Studio context with no drawing API at all.

## Scripts

| Script | Purpose |
| --- | --- |
| `lib.mjs` | Session open, `apiGet` / `apiPost`, modify-status polling |
| `drawing.mjs` | `createDrawing`, `modify`, `exportDrawingJson`, `deleteElement` |
| `build.mjs` | Builds the "big Enclosure" drawing end to end |
| `inspect.mjs` | Dumps document elements, assembly structure, BOM |
| `measure.mjs` | Ground-truth model bounding boxes in mm |
| `calibrate.mjs` | Same view at several scales, to measure scale behaviour |
| `find-templates.mjs` | Locates stock drawing templates |
| `list-drawings.mjs` | Lists drawing elements; deletes by `DELETE_EIDS` |
| `shot.mjs` | Screenshots a drawing — verification by eye, not by HTTP 200 |

Run `npm install` once in `drawing-tools/`, then `node build.mjs`. A headed Chromium opens and
waits for a hand login; the session is cached in the gitignored `.auth/`.

## Hard-won facts

These cost real debugging time. They are not in Onshape's documentation.

### Session cookies work for GET *and* POST — but the CSRF token must be exact

Every POST returns **401** without an `x-xsrf-token` header carrying the `XSRF-TOKEN` cookie
value. The token is base64 and ends in `==`; extracting it with `cookie.split('=')[1]` truncates
the padding and yields the same blanket 401 as sending nothing. Keep everything after the first
`=`:

```js
const raw = document.cookie.split('; ').find((c) => c.startsWith('XSRF-TOKEN=')) ?? '';
const xsrf = raw.slice(raw.indexOf('=') + 1);
```

A wrong token fails identically on every endpoint, including read-only POSTs, which makes it look
like writes are blocked wholesale. They are not.

### Sheet coordinates are millimetres on a metric sheet

An A1 sheet spans **841 × 594**, origin bottom-left. Confirm the extent on any drawing by reading
the border `Onshape::Line` coordinates out of the `DRAWING_JSON` export. An inch-based template
uses inches instead — the unit follows the sheet, so never assume.

### Create the drawing from a metric template, not a "custom graphics area"

`POST /drawings/d/{did}/w/{wid}/create` accepts `size: "A1"` only via the custom-graphics-area
form (`border` + `titleblock` + `size`); the plain form ignores `size` silently. But that sheet is
millimetre-*sized* while keeping an **inch-based style**, which poisons everything downstream:

* View scale is interpreted against inches, so a visual 3:1 needs `numerator: 76.2`, and the title
  block then reports "76.2:1".
* Anything sized by style rather than an explicit value renders 25.4× too small. A default 0.12
  unit text height becomes 0.12 mm — invisible. Tables and dimensions vanish.
* **Dimensions have no `textHeight` field** (`additionalProperties: false`), so there is no
  override. This alone makes the custom-graphics-area route unusable for a dimensioned drawing.

Creating from Onshape's public metric template fixes all three at once — `numerator: 3` becomes a
true 3:1 and the title block agrees:

```js
templateDocumentId:  '4dc2b3d1d578c4f74825b0c6',
templateWorkspaceId: 'd77a252cbd4776ca33bb5086',
templateElementId:   '5e3bbff9d8780844bd634954', // ISO_A1.dwt
```

`templateElementId` must be the **BLOB** `.dwt` element. Passing the sibling drawing element
returns `400 … is not a BLOB element`. Onshape's own documented template document
(`cbe6e776694549b5ba1a3e88`) contains ANSI sizes only — no ISO.

Verify the scale actually landed by reading `viewToPaperMatrix.items[0]` from the export: it is
model metres → paper millimetres, so a true 3:1 reads **3000**.

### There is no sheet or style request

The `/modify` endpoint's request types are exactly: create/edit view, create/edit annotation,
delete entity, export. Sheet size, sheet scale, drawing units and text styles are **not**
settable — element units return 405/404 on every route. Whatever the template gives you is what
you have.

### View geometry arrives in view-local axes

`GET /drawings/d/{did}/w/{wid}/e/{eid}/views/{viewId}/jsongeometry` returns entities with
`uniqueId` and `deterministicId`, coordinates in **metres**. The axes are the view's own frame,
not model XYZ: in a front view the on-paper vertical is **axis 1**, with axis 2 running into the
page. Using axis 2 there produces plausible-looking wrong dimensions (it reports the depth).

Dimensions become associative — `isDangling: false` — when each point carries `uniqueId`,
`viewId` and a `snapPointType` alongside its coordinate.

### Dimensions need an explicit unit

The drawing unit enum is separate from the sheet style, so pass
`unit: { unit: 'Millimeter', isUnitOverridden: true }` or values report in inches.

### BOM tables

A native Onshape BOM cannot carry arbitrary columns, so a drawing reproducing a SolidWorks BOM
with a custom column (e.g. "thread size") needs `Onshape::Table::GeneralTable` with literal cells.
Set `formatting.tableColumnWidth` / `tableRowHeight` and a per-cell `textHeight` — cells do accept
`textHeight` even though the table itself does not.

Such a table is **not linked to the model**; it will not update when the assembly changes.

## Current limitations

* **Balloons have no leaders.** `Onshape::Callout` supports an associative `leaderPosition`, but
  most ballooned items (screws, gasket, USB-C cable) are not in the model, so leaders would only
  be attachable for a minority of items.
* **The title block's TITLE field cannot be filled**, so the drawing title is a positioned note.
* **Cover-removed interior views** are not reproducible: per-view component suppression is not in
  the API. The interior view here is a view of the base part alone, which reads similarly.
