# Free-Form Deformation

User guide for `custom-features/freeFormDeformation.fs`.

Design and derivation live in
[docs/specs/FREE_FORM_DEFORMATION_SPEC.md](../../specs/FREE_FORM_DEFORMATION_SPEC.md).

## What it does

Embeds one or more surfaces in a 3D lattice of control points and deforms them by moving the
lattice. Moving a lattice point drags the surface with it, smoothly, with influence falling off
across the whole lattice.

Three things worth knowing up front:

- **You can select many lattice points at once** and drive them with a single handle — including a
  whole row or a whole plane, chosen by *Selection scope*.
- **The surface is refined before it is deformed**, so lattice resolution actually buys you
  something, and the feature tells you how accurate the result is.
- **Closed surfaces are prepared first** so a deformation cannot crease them. See
  [The seam](#the-seam) — this is why a revolve deforms at all.

---

## The lattice: U, V and N

The lattice has three directions, **U**, **V** and **N**, drawn in the graphics area as a cage:

| Direction | Colour |
| --- | --- |
| U | Magenta |
| V | Cyan |
| N | Yellow |

Magenta and cyan match Onshape's own U/V isoparametric colouring, and Edit Surface's control net.
Yellow is this repository's convention for the third direction, which Onshape has no equivalent for.

By default the lattice is axis-aligned and tightly bounds everything you selected. Turn on **Orient
lattice to a mate connector** and pick a mate connector to point it somewhere else — useful whenever
the shape's natural directions are not the global ones.

**Spans, not points.** *Lattice spans in U* = 2 gives you **3** points along U. Points = spans + 1.

**Flat inputs work.** If your selection is planar, the lattice would have zero thickness in one
direction; it is automatically given a real thickness and the surface sits in the middle of it, so
you can pull a flat face out of plane.

---

## Workflow

1. Select the **surfaces to deform**. Several faces share one lattice, so they deform coherently as
   a group.
2. If any face is not already a spline — a plane, cylinder, extrude, revolve — turn on **Approximate
   non-spline faces**. The feature will tell you which type it found if you forget.
3. Set the **lattice spans** in U, V and N.
4. Turn on **Edit lattice** (or just click a lattice point, which turns it on for you).
5. Choose a **Selection scope** and click lattice points to select them. Click again to deselect.
6. Drag the handle.

---

## Selection scope

Scope decides what one click brings in.

| Scope | Selects |
| --- | --- |
| Point | Just that point |
| Row along U | Every point sharing its V and N indices |
| Row along V | Every point sharing its U and N indices |
| Row along N | Every point sharing its U and V indices |
| Plane at constant U | Every point sharing its U index |
| Plane at constant V | Every point sharing its V index |
| Plane at constant N | Every point sharing its N index |

Scope applies to what you **change**, not to the whole selection — so with a plane scope, clicking a
point adds its whole plane and clicking a selected point removes its whole plane. Points you already
had selected stay put. Switch to Point scope to fine-tune a selection you built with a wider one.

---

## The handle

Selecting anything gives you a **full triad** — translation arrows *and* rotation rings — centred on
the selection and aligned to the lattice's own axes. There is no mode to choose: rotation is always
available, because a full triad does everything a plain one does.

**It works on any selection.** Select a whole row with *Row along U* and you can lean the entire row
out, not just slide it. Select a constant-N plane and you can tilt it. That is what the old
plane-manipulation feature existed for, now available at any scope and on a lattice that is not
forced down to one span in two directions.

Rotation is about the centre of whatever is selected, so a plane rotates about its own middle rather
than swinging around a world axis.

### Planarize selection

A button, in the **Edit lattice** group. Press it and every selected lattice point moves onto the
least-squares plane through them, along that plane's normal.

It is a **one-shot edit**, not a constraint. The points are written as ordinary offsets, exactly as
if you had dragged them there, and nothing keeps them coplanar afterwards — drag one and it leaves
the plane. Press it again whenever you want them flattened again.

Pressing it on a *row* does nothing, and that is correct: a row is already as planar as a line can
be, and the fitted plane simply contains it. It needs at least three points to do anything at all.

---

## Accuracy

Everything in this group is about how faithfully the deformed B-spline reproduces the deformation
you actually asked for. Defaults are sensible; read this when a result is not accurate enough, or is
heavier than you want.

**Continuity** — the degree the surface is elevated to before refinement. *Curvature continuous
(degree 3)* is the default and is what you want for anything visible. Drop to *Tangent continuous*
or *Inherit from the face* only if you know why. A tolerance cannot fix a degree that is too low: a
degree-1 surface converges in position while staying creased at every knot line forever.

**Automatic tolerance** — bounding box diagonal × 1e-4, so the default behaves the same on a small
bracket and a large hull. Turn it off to set a length.

**Maximum control points per direction** and **Maximum refinement passes** — the budget. The feature
refines until two successive levels agree inside the tolerance, or until one of these binds. If a cap
binds first, you get a **warning** naming the deviation actually achieved — not a silent stop.

### What the info message tells you

After a successful regeneration:

> Deformed 1 surface(s) through a 2x2x2 lattice. Refined to at most 25 control points per direction;
> certified worst deviation from the true deformed surface: 0.0021 mm (tolerance 0.0100 mm).

The **certified** number is measured by the kernel, not estimated: the feature evaluates the true
deformed shape at knot span midpoints and asks how far those points are from the surface it built.
Neither of the old FFD features could produce this number.

---

## The seam

Closed surfaces — anything from a revolve, and cylinders — have a **seam**, the line where the
surface comes back around and meets itself. You do not have to do anything about it, but it is worth
knowing what the feature does there, because it is the one place a deformation can produce something
the kernel refuses outright.

A B-spline is only as smooth as its knots allow. At a knot of multiplicity *m* in a degree-*d*
direction the surface is C^(d−m), so a knot at full multiplicity (*m* = *d*) leaves it C⁰ — free to
crease. Onshape hands back a revolve's closed direction in exactly that state: a circle stored as
rational Bézier arcs, degree 3 with the joins at multiplicity 3. Such a surface is smooth across
those joins only because its control points *happen* to be arranged for it — collinear, with matching
weight ratios. Nothing in the structure requires it.

That arrangement is exactly what a deformation destroys, because an FFD is a nonlinear map and does
not carry collinear points to collinear points. If the seam sits on one of those joins, it creases
the moment any lattice point moves, and the kernel rejects the body with
`PERIODIC_BSPLINESURFACE_NOT_SMOOTH`.

**The feature fixes this by removing the permission to crease, not by trying to restore the
arrangement.** Before deforming, it re-fits the closed direction onto a knot vector where every knot
is simple, so the surface is C^(d−1) *everywhere* — for any control points whatsoever. No
deformation can crease it, at the seam or anywhere else.

That re-fit is not an approximation of the face in the usual sense: **the surface is never
evaluated.** There are no sample points and no interpolation conditions. It projects the control net
onto the coarser spline space using the same knot-refinement algebra the rest of the module is built
on, which has two consequences you can rely on:

- **It is exact whenever exactness is possible.** If the knots really were removable, you get the
  original surface back with zero deviation reported.
- **When it isn't exact, the cost is measured and included** in the certified deviation reported
  after regeneration — not estimated, and not hidden.

For a revolve it is also cheap: 16–24 control points around the circle puts the deviation in the
micron range on a 100 mm part, which is well inside the default tolerance.

Two consequences worth knowing:

- **The seam edge may end up somewhere different** on the deformed body than on the original. The
  shape is unaffected; only the label moves.
- **A closed face cannot be emitted trimmed**, because the trim loops were measured against a
  parameter domain the re-fit shifts. You get a warning saying so. Use **Replace face** for closed
  faces — it carries the trim topologically and needs no parameter agreement.

---

## Result

| Result | What you get |
| --- | --- |
| **New surface body (untrimmed)** | The full B-spline surface the deformed control net describes, covering its whole parameter range. The default, and what both old features did. |
| **New surface body (trimmed)** *[experimental]* | A new body carrying the original face's own perimeter and holes. |
| **Replace face** | Swaps the surface underneath the original face, keeping its perimeter, holes and inner loops. |

Replace face is right for **one** face. With several selected you will get a warning, and you should
believe it: each face is retrimmed against neighbours that have not been deformed yet, so shared
edges will not agree.

**New surface body (trimmed) is the multi-face answer**, and the route toward deforming a whole
solid. Each face's boundary comes from its own parameter space, never from an intersection with a
neighbour, so faces have no coupling to get wrong — deform every face of a shell to its own trimmed
sheet, then knit the sheets. Two things to know about it:

- **It always approximates the face**, even one that is already a spline, and the info message says
  so. The trim curves and the surface have to come from the same read to be guaranteed to share a
  parameterization, and only the approximating read returns curves at all.
- **A closed (periodic) face comes back untrimmed**, with a warning. Two things change the parameter
  space the trim curves were measured against: normalizing a closed direction re-cuts its knot
  vector, and re-fitting one shifts the parameter-to-point map by the reported deviation (see
  [The seam](#the-seam) — that one is not optional). Rather than cut the wrong shape, the feature
  drops the trim and tells you. Use **Replace face** for those — it carries the trim topologically
  and needs no parameter agreement.

---

## Recipes

Carried over from the planes feature's guide, restated for scopes. All of these assume *Edit lattice*
is on.

### Taper

```
┌────┐        ┌───┐
│    │   →    │   │
│    │        │   │
└────┘        └───┘
```

Scope **Plane at constant N**, 3+ spans in N. Select the top plane, switch to Point scope, and pull
its corner points inward — or select two opposing rows in turn and translate them toward each other.

### Twist

```
┌────┐        ╱────╲
│    │   →    │    │
└────┘        ╲────╱
```

Scope **Plane at constant N**, 4+ spans in N. Rotate successive planes by progressively larger angles
about the N axis, using the triad's rotation rings.

### Bend

```
│    │        ╱
│    │   →    │
│    │        ╲
```

Scope **Plane at constant U**, 4+ spans in U. Translate the middle planes sideways, leaving the end
planes where they are.

### Wave

```
────────  →   ∿∿∿∿∿∿∿
```

Scope **Plane at constant U**, 6+ spans in U. Translate alternate planes up and down.

### Local bump

Scope **Point**, a lattice with enough spans to localize the influence (4+ in each direction). Select
one interior point and pull it along N.

---

## Troubleshooting

**No manipulators at all — not the points, not the triad.** Check for a regeneration error first. A
feature that throws draws **none** of its manipulators, even the ones it added before the throw, so a
failure late in the pipeline looks exactly like the handles being broken. Fix the error and they come
back.

**Lattice points do not appear.** They are drawn whenever the feature regenerates. If nothing is
there, the face selection is empty or unreadable — check for the regeneration error first.

**"This is a PLANE face, which has no B-spline definition to read."** Turn on *Approximate non-spline
faces*. This is not the feature being difficult: representing a plane as a B-spline is an
approximation, and it makes you say so rather than doing it silently.

**The deformation looks faceted or rough.** This was the old features' standing limitation and is
now a settings question. Check *Continuity* is not set to *Inherit from the face* on a low-degree
input, and check whether you got the budget warning — if a cap bound before the tolerance was met,
raise *Maximum control points per direction*.

**Cranking up lattice resolution stopped helping.** In the old features this was the ceiling: the
deformation could only ever be as detailed as the surface's existing control net. It should no
longer happen. If it does, you are almost certainly hitting the refinement budget — the warning will
say so.

**No rotation handle on the triad.** There is no mode to set — the full triad with rotation rings is
the only handle, and it appears as soon as anything is selected. If you see no handle at all, check
for a regeneration error first.

**"A closed (periodic) direction was NOT elevated to the requested continuity."** Expected, not a
fault. Elevation raises *every* knot to full multiplicity, leaving the surface C⁰ at every knot line
— including the seam, which then creases as soon as the deformation moves the control points, and
the kernel refuses the body outright. The seam is normally kept smooth by cutting it at a simple
knot, and after elevation there is no simple knot left to cut it at. A closed direction from a
revolve is already an exact circle, so nothing is lost geometrically, and refinement still supplies
all the detail. Nothing to do.

**`PERIODIC_BSPLINESURFACE_NOT_SMOOTH` or `BSPLINESURFACE_NOT_G1` on a revolve or cylinder.** Fixed —
see [The seam](#the-seam) for what was happening and why. Both codes were the same defect seen by two
different kernel checks. If you still see either, the diagnostic to send is *Enable diagnostics →
Print refinement details*, which prints every stage's knot multiplicities.

**"A closed (periodic) direction was re-fitted onto a smoother knot vector."** Expected on any
revolve or cylinder, and not a fault — see [The seam](#the-seam). The cost is included in the
certified deviation, and it is usually zero.

**My offsets disappeared after I lowered the span counts.** They did not — they are out of range for
the smaller lattice and are being ignored, which the warning says. Raise the span counts back and
they return.

**Deforming several faces gives gaps at their shared edges with Replace face.** Expected, and warned
about. See [Result](#result).

---

## References

- Sederberg, T.W. and Parry, S.R., *Free-Form Deformation of Solid Geometric Models*, SIGGRAPH 1986
- `whitepaper-references/"Free-Form Deformation of Parametric CAD Geometry.pdf"`
- [docs/specs/FREE_FORM_DEFORMATION_SPEC.md](../../specs/FREE_FORM_DEFORMATION_SPEC.md) — design and derivation
- [docs/specs/SPLINE_REFINEMENT_UTILITY_SPEC.md](../../specs/SPLINE_REFINEMENT_UTILITY_SPEC.md) — the refinement module this is built on
