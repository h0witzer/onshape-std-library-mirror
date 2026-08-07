# Tippy Bucket Pivot — Implementation Game Plan

**Spec for `custom-features/tippyBucketPivot.fs`.**
Target: FeatureScript 3029.

## Status (2026-08-06)

Phase 1 written — build-order steps 1–6 of §10. **Not yet built in Onshape; no check has passed.**
Phase 2 (§7: partial-fill sweep and `TARGET_TRIP_FILL`) is not implemented.

This document has been updated to match the code. What changed from the original plan, and why:

- **Fill density comes from the fill solid's assigned material, full stop.** The `CUSTOM_DENSITY`
  option and the typed density field are gone. There is now nowhere in this feature to type a
  density — every number comes from an assigned material. One rule, one source of truth, and the
  density lives in the document rather than in a dialog where it drifts out of sync with what it
  describes. The fill is a single required pick (`MaxNumberOfPicks : 1`), matching "select a solid
  which is to represent the water"; if a fill ever needs to be multiple bodies it becomes a
  driven-query array exactly like `bucketParts`.
- **The interference check was wrong and flagged correct models.** Water modelled at its fill level
  *abuts* the bucket walls by design — that is what a correct fill solid looks like. `evCollision`
  reports abutting and volumetric overlap as distinct `ClashType`s, and the first draft treated any
  clash as double-counted mass. Now only `INTERFERE`, `TARGET_IN_TOOL` and `TOOL_IN_TARGET` are
  reported (`VOLUME_SHARING_CLASH_TYPES`); every `ABUT_*` type is ignored. See `clashtype.gen.fs`.
- **The tilt / stop-angle check was removed entirely, not fixed.** It could not fail. See §1.5.
- **`allowMassOverride` removed; a material is now always required.** The escape hatch contradicted
  the one-source-of-truth rule above. A mass override is still *used* when a part carries one, since
  someone set it deliberately and it beats density × volume on purchased parts, but it no longer
  excuses a missing material. The readout says how many parts it applied to.
- **`refreshMaterials` kept after all, and built with `isButton`** — see §2. Removing it was wrong: it
  is the only *guaranteed* way to make editing logic run. Building it as a boolean with
  `UIHint.OPPOSITE_DIRECTION_CIRCULAR` was also wrong; that is a toggle, not a button.
- **`clash['type']`, never `clash.type`.** `type` is a reserved word and will not parse as a
  dot-accessed field, and the failure cascades into a pile of misleading syntax errors in the functions
  below it. `boolean.fs:1826` is the precedent.
- Every non-obvious parameter now carries a `"Description"` annotation, so the dialog explains itself
  in Onshape instead of only in this document.

Purpose: take pivot placement away from the drafter. The drafter selects the bucket solids
(materials already assigned) plus one solid representing the water at trip fill. The feature
computes material-aware centers of mass for the empty and filled states, places a mate connector
at the pivot location that makes the bucket tip, and drops construction points showing both
centers of mass so the drafter can *see* why it works.

---

## 0. TL;DR

1. **Editing logic** reads `PropertyType.MATERIAL` per body and caches each body's density into a
   hidden field of a **driven-query array item** bound to that body. This is forced — `getProperty`
   cannot be called on the current context during regen (`properties.fs:71-76`).
2. **Regen** recomputes volume + centroid live from geometry (`evApproximateMassProperties`) and
   multiplies by the cached density. Geometry edits therefore stay correct with no cache refresh;
   only a *material reassignment* needs the dialog reopened.
3. **Physics:** the pivot height sits between the empty CoM and the filled CoM. Placement fraction
   defaults to the point where the empty bucket's hold-down torque equals the filled bucket's
   tip-over torque — a real optimum, closed form, no iteration.
4. **Throws** on any selected solid with no material. Also throws when the geometry cannot tip at
   all (water centroid at or below the empty CoM), which is the actual mistake the drafters keep
   making and the most valuable thing this feature will ever tell them.

---

## 1. Physics model

### 1.1 Frame

Everything reduces to a 2D problem in the plane the bucket rocks in.

| Symbol | Meaning | Source |
| --- | --- | --- |
| `upDirection` | unit vector opposite gravity | user reference, default `vector(0, 1, 0)` |
| `pivotAxisDirection` | unit vector along the pivot axis, horizontal | user reference or derived (§1.4) |
| `tippingDirection` | `cross(pivotAxisDirection, upDirection)`, normalized | derived |

`upDirection` and `pivotAxisDirection` must be perpendicular. Rather than rejecting a
near-perpendicular pick, remove the `upDirection` component from `pivotAxisDirection` and
renormalize; throw only if what remains is degenerate (axis within ~1° of vertical), because a
vertical pivot axis cannot produce tipping under gravity.

Scalar coordinates used below are the components of a point along `upDirection` ("height") and
along `tippingDirection` ("lateral").

### 1.2 States

Per bucket body `i`: cached `density_i`, live `volume_i` and `centroid_i`, so `mass_i = density_i * volume_i`.

```
emptyMass          m0 = Σ mass_i
emptyCenterOfMass  C0 = Σ (mass_i * centroid_i) / m0

fillMass           mw = fillDensity * fillVolume
fillCentroid       Cw

filledMass         m1 = m0 + mw
filledCenterOfMass C1 = (m0*C0 + mw*Cw) / m1
```

Heights: `u0 = dot(C0, upDirection)`, `u1 = dot(C1, upDirection)`, `uw = dot(Cw, upDirection)`.

**Feasibility.** `u1 > u0` ⟺ `uw > u0`. If the water centroid is not above the empty CoM, filling
*lowers* the CoM and the mechanism can never trip. Throw here with a diagnostic that names the
shortfall in millimeters and tells the drafter the two fixes: move ballast lower, or raise the
water volume. This is the design review the drafters are not getting from a human.

### 1.3 Pivot height — the optimum

Let `D = u1 - u0 > 0` and place the pivot at height

```
pivotHeight = u0 + pivotHeightFraction * D,   pivotHeightFraction ∈ (0, 1)
```

Below the pivot the CoM is a stable pendulum; above it, unstable. So `pivotHeightFraction` *is* the
trip threshold: empty → CoM below pivot → the bucket holds its rest attitude; at full fill → CoM
above pivot → the equilibrium vanishes and it goes over.

Two competing requirements, written as torque authority at small tilt `θ`:

```
empty hold-down torque   τ0 = m0 * g * (pivotHeight - u0) * sinθ = m0 * g * f * D * sinθ
filled tip-over torque   τ1 = m1 * g * (u1 - pivotHeight) * sinθ = m1 * g * (1-f) * D * sinθ
```

`τ0` rises with `f`, `τ1` falls with `f`, so `max min(τ0, τ1)` is at the crossing:

```
m0 * f = m1 * (1 - f)          ⟹      pivotHeightFraction* = m1 / (m0 + m1)
```

Sanity: `mw → 0` gives `f* → 1/2`; `mw >> m0` gives `f* → 1` with both authorities converging to
`m0 * g * D`, i.e. a bucket with no mass of its own cannot hold itself down. The math behaves.

Explain it to a drafter as: *the pivot sits where the empty bucket's righting torque exactly equals
the full bucket's tipping torque.* Nothing else to argue about.

Three threshold modes:

- `BALANCED` (default) — `f = m1 / (m0 + m1)`.
- `CUSTOM_FRACTION` — user supplies `f`. Lower `f` = trips on less water, weaker reset. Higher `f` =
  stronger reset, needs more water. Bounds `[0.05, 0.95]`.
- `TARGET_TRIP_FILL` — Phase 2, §7.

### 1.4 Pivot lateral and axial position

Lateral: put the pivot on the vertical line through the **empty** CoM, i.e. lateral coordinate
`= dot(C0, tippingDirection)`. For a symmetric two-chamber tippy that is the symmetry plane, so the
empty bucket hangs level, and the filled CoM's lateral offset — nonzero because the water sits on
one side — sets which way it goes over. Report the resulting tip direction.

Axial (along `pivotAxisDirection`): does not affect the physics. Default to the empty CoM's axial
coordinate; offer an optional reference (planar face / vertex / mate connector) to slide the mate
connector out to the trunnion bearing face where the drafter actually wants it. Implemented as
`dot(evApproximateCentroid(reference), pivotAxisDirection)` rather than `project(plane, point)` — one
call covers a vertex, a planar face and a mate connector point body alike, and it stays correct when
the reference plane's normal is not parallel to the axis.

Derived axis default: when no `pivotAxisDirection` reference is given, take the horizontal component
of `C1 - C0` and use `cross(upDirection, that)`. The CoM shift defines the rocking plane, so its
horizontal normal is the correct axis. If that shift is degenerate (fill laterally centered, < ~0.1 mm)
throw and require an explicit axis — there is no defensible guess.

### 1.5 The tilt check was removed because it cannot fail

The original plan had an optional check that rotated each state to a tilt angle and verified the torque
sign. It is not in the code. Working out what it would actually report shows it is vacuous:

The pivot is placed laterally on the vertical line through the empty CoM (§1.4), so the empty state's
offset from the pivot is `d₀ = -L₀·up` **exactly**, with no lateral component. Torque at tilt `θ` is
then `-m₀·g·L₀·sin(θ)`, which is restoring for every `θ` in `(0°, 180°)`. Always. And the filled state
sits above the pivot by `L₁` with lateral offset `s₁`, giving `m₁·g·(L₁·sin θ + s₁·cos θ)`, which is
overturning throughout `[0°, 90°]`. Also always.

Both outcomes are guaranteed by `u₀ < pivotHeight < u₁`, which §1.3 already enforces. So the check
could only ever confirm the placement rule that produced it — it would compute two numbers whose signs
are fixed by construction and warn on conditions that cannot occur. The torque *magnitudes* are
genuinely useful, but they are already in the readout as the two authority coefficients; the
angle-specific values are just those times `sin θ`.

Worth recording because it looked like a real check and is not. A non-vacuous version needs something
the pivot placement does not already determine: bearing friction, a hard stop at a known angle, or
partial fills (§7).

**Sign convention, kept here because it is easy to get backwards and the first draft did** — since
`tippingDirection = cross(pivotAxisDirection, upDirection)`, the triad
`(pivotAxisDirection, upDirection, tippingDirection)` is right-handed, so a positive rotation about the
axis carries `upDirection` toward `tippingDirection`. A CoM a distance `L` *below* the pivot therefore
has `lateralArm = -L·sin(θ)`. In `torque = mass · g · dot(tiltedCoM - pivotPoint, tippingDirection)`,
**positive torque drives the bucket further over and negative drives it back toward level.**

Note on `AGENTS.md` §"Why Not Dot Products?": there is no query or `ev*` function that composes
mass-weighted centroids, resolves a torque about an arbitrary axis, or solves for a threshold height.
`evApproximateMassProperties` is used for every per-body measurement and `rotationAround` / `project`
/ `line` for every rigid transform; the remaining arithmetic is the mass-average and torque algebra
itself, which has no library equivalent.

---

## 2. Architecture: the material constraint

`properties.fs:71-76` is explicit:

> This function cannot be called on the current context inside custom features. It can only be called
> from table functions, editing logic, manipulator change functions, and custom features that refer to
> a different context. […] features are regenerated before any user-set properties are applied.

So material reading happens in editing logic, and the result must survive into regen through the
feature definition. Two things could be cached — density, or finished mass properties — and the choice
matters:

**Cache density only, recompute geometry at regen.** Density is an intrinsic material property,
independent of shape. So a cache of densities stays valid through *any* upstream geometry edit; regen
recomputes `volume` and `centroid` live and multiplies. Caching finished masses/centroids instead would
go stale the moment anyone changed a wall thickness — silently, and in a mass-critical feature. Not
acceptable.

**Binding density to the right body.** Two mechanisms work; the choice is not about capability.

*Positional (plain `Query` list + `isAnything` cache).* `isAnything` (`feature.fs:526`) is a predicate
that always passes, so a hidden parameter can hold any array of maps. Editing logic walks
`qNthElement(selection, i)` and stores densities in index order; the feature body walks the same
`qNthElement(selection, i)` and reads index `i`. This is exactly what `custom-features/autoLayout.fs`
does (`:108` declares it, `:118-141` correlates by index, `:983-998` fills it). The one thing that
cannot go in the cache is a `Query` — autoLayout's own comment at `:992` records why: "Query objects
cannot survive round-tripping through definition (QueryType serializes to a raw integer)." Note the
distinction: a *declared* `is Query` parameter serializes fine; a Query smuggled into an `isAnything`
blob does not.

*By stored query (driven-query array).* An array parameter with `"Driven query"`
(`constrainedSurface.fs:119`, `faceBlend.fs:179`, `bsurf.fs:109`) presents as a single selection box,
auto-creates one item per pick, and gives each item its own declared single-pick `Query` — which
persists. Pairing is stored, not inferred from order.

**Why this feature uses the second one.** Positional pairing can silently mis-pair if the selection's
resolution order changes between editing logic running and a later regen. A count check catches
add/remove but not a pure reorder. The natural defence against reorder is a per-body fingerprint the
feature body can recompute — volume — and *that defence contradicts the whole point of caching density*:
density is cached precisely so it survives geometry edits, and a volume fingerprint false-alarms on
exactly the legitimate wall-thickness change the cache is meant to ride out. So positional pairing
forces a choice between surviving geometry edits and being reorder-safe. Stored pairing is both.

The concrete failure it avoids: two geometrically identical brackets, one steel and one aluminium. Same
volume, same bounding box, different density. If their order swaps, every check that could be written
passes and the pivot is wrong with a clean readout. For a layout feature a mis-pair is visible in the
result; for a pivot it is not, which is the only reason this feature pays the extra UI nesting.

**Why the query-stability functions do not apply here.** `setExternalDisambiguation`
(`feature.fs:684`) and the tracking queries (`startTracking`, `startTrackingIdentity`,
`feature.fs:560-628`) are the standard library's tools for keeping query resolution stable, and they are
used constantly in std — but both are *within-regen, context-side*:

- `setExternalDisambiguation` re-keys the identity of geometry a feature **creates** under an id ending
  in an `unstableIdComponent`, so that identity follows the source entity instead of a loop index.
  `forEachEntity` (`feature.fs:304-315`) is the canonical use.
- Tracking queries find the **descendants** of entities an operation split, transformed or booleaned,
  anchored to `lastOperationId(context)` at the moment tracking starts — an interval inside one regen.

Neither can bridge editing logic to regen, for three independent reasons: editing logic cannot mutate
the context at all (it only returns a modified definition), nothing either function produces lands in
the definition, and a `Query` cannot survive in an `isAnything` blob regardless (`autoLayout.fs:992`).
There is no "start tracking in editing logic, resolve at regen" — editing logic has no operation to
anchor to.

Where they *would* apply in this feature: nowhere at present, because every id it creates is a fixed
string (`id + "pivotMateConnector"`, `id + "emptyCenterOfMassPoint"`, …) with no unstable component.
If per-part diagnostics are ever added — a CoM point per bucket solid, which would be a `forEachEntity`
shaped loop — then `setExternalDisambiguation` becomes mandatory, and note that the doc for it names
`opPoint` explicitly as a sub-operation that otherwise does not track dependency.

`custom-features/measureCutListPascoe.fs:952-1011` is the in-repo precedent for reading properties in
editing logic and stashing them into `UIHint.ALWAYS_HIDDEN` array-item fields — worth reading before
starting, including its "(STATIC) — will not update automatically" labelling convention.

**Known limit, to be documented in the feature description, not hidden.** Editing logic does not run on
a plain regen. Reassigning a material with the dialog closed leaves the cache stale until the feature is
reopened. Mitigations: a `refreshMaterials` toggle; the material name echoed per array item so the stale
value is visible; and the mass summary reported via `reportFeatureInfo` on every regen so a wrong number
is in the drafter's face rather than buried.

---

## 3. Parameters

```
Feature Type Name : "Tippy Bucket Pivot"
Editing Logic Function : "tippyBucketPivotEditingLogic"
```

### Bucket solids (driven-query array)

```featurescript
annotation { "Name" : "Bucket solids", "Item name" : "Part", "Driven query" : "solidBody",
             "Item label template" : "#solidBody [#materialName]",
             "UIHint" : [UIHint.COLLAPSE_ARRAY_ITEMS, UIHint.PREVENT_ARRAY_REORDER] }
definition.bucketParts is array;
for (var bucketPart in definition.bucketParts)
{
    annotation { "Name" : "Solid", "Filter" : EntityType.BODY && BodyType.SOLID, "MaxNumberOfPicks" : 1 }
    bucketPart.solidBody is Query;

    annotation { "Name" : "Material name", "Default" : "", "UIHint" : UIHint.ALWAYS_HIDDEN }
    bucketPart.materialName is string;

    annotation { "Name" : "Density kg per cubic meter", "UIHint" : UIHint.ALWAYS_HIDDEN }
    isReal(bucketPart.densityKilogramsPerCubicMeter, DENSITY_READOUT_BOUNDS);

    annotation { "Name" : "Mass override kilograms", "UIHint" : UIHint.ALWAYS_HIDDEN }
    isReal(bucketPart.massOverrideKilograms, MASS_READOUT_BOUNDS);

    annotation { "Name" : "Material was read", "UIHint" : UIHint.ALWAYS_HIDDEN }
    bucketPart.materialWasRead is boolean;
}
```

Store density as a plain unitless real in kg/m³ (bounds `[0, 0, 1e6]`, covers osmium at 22590) and
re-apply units at regen. Avoids round-tripping a `ValueWithUnits` through a hidden parameter.

### Fill

```
fillSolid             Query, EntityType.BODY && BodyType.SOLID, exactly 1 pick, required
fillMaterialName      string, UIHint.READ_ONLY   (shown, so the density in use is visible)
cachedFillDensity     hidden real
fillMaterialWasRead   hidden boolean
```

Density always comes from the fill solid's assigned material; there is no typed-density path. Throws
when the fill has no material, the same as any bucket solid. The material name is `READ_ONLY` rather
than `ALWAYS_HIDDEN` so what got read is visible in the dialog without opening anything.

Document that the fill solid represents the water **at trip fill**, its top surface flat and
perpendicular to `upDirection`, modeled in the bucket's rest attitude — and that it is *expected* to
sit up against the walls (see the abutting note in the Status section).

### Orientation

```
upReference          Query, QueryFilterCompound.ALLOWS_DIRECTION || BodyType.MATE_CONNECTOR, 1 pick, optional
flipUp               boolean, UIHint.OPPOSITE_DIRECTION
pivotAxisReference   Query, same filter, 1 pick, optional (derived per §1.4 when empty)
axialLocationRef     Query, plane/face/vertex/mate connector, 1 pick, optional
```

Resolve directions with `extractDirection(context, query)` (`topologyUtils.fs:124`) — it tries
`evAxis` then `evPlane`, so edges, cylinders, planar faces and mate connectors all work from one
input. `grainDirection.fs:177` is the in-repo usage pattern.

Default up when nothing is picked is **`vector(0, 0, 1)`** — global up in Onshape is **+Z**, not +Y.
`defaultFeatures.fs:41-43` settles it: Top is the XY plane (normal Z), Front is XZ, Right is YZ. The
import option `yAxisIsUp` corroborates, remapping a Y-up source's Y onto Onshape's Z
(`geomOperations.fs:1062`). An earlier draft defaulted to +Y, which would have placed the pivot using
a horizontal axis as gravity — the feature would still have produced a mate connector, just a
meaningless one.

### Threshold

```
thresholdMode         enum { BALANCED, CUSTOM_FRACTION, TARGET_TRIP_FILL }   default BALANCED
pivotHeightFraction   isReal [0.05, ..., 0.95], if CUSTOM_FRACTION
targetTripFillFraction isReal [0.05, ..., 1.0], if TARGET_TRIP_FILL          (Phase 2)
```

### Output and diagnostics

```
mateConnectorOwner        Query, 1 pick, optional (default: highest-mass bucket solid)
createCenterOfMassPoints  boolean, default true
showDebugOverlay          boolean, default false
verifyMassAccuracy        boolean, default true      (§5 step 4; costs evVolume HIGH per body)
refreshMaterials          isButton(...)   (top level, not in a group; absent from the defaults map)
```

`refreshMaterials` sits at the top level next to the selections, not buried in a collapsed group —
it is the recovery action for the one staleness case this feature has, so it needs to be visible.

---

## 4. Editing logic

`tippyBucketPivotEditingLogic(context, id, oldDefinition, definition, isCreating, specifiedParameters)`

Re-read every bucket part on every invocation. `getProperty` is a metadata lookup with no geometry
cost, so selective refresh (on `isCreating`, changed `solidBody`, or `materialWasRead == false`) buys
nothing worth the branching.

**But unconditional re-reading is not a substitute for a refresh button, and the first draft was wrong
to drop it on that reasoning.** Editing logic runs only in response to dialog activity. It is not
guaranteed to fire when a dialog is merely opened and closed with nothing touched, so "reopen the
feature" is not a reliable refresh — whereas toggling a parameter unambiguously *is* dialog activity
and therefore unambiguously calls editing logic. `refreshMaterials` is that guaranteed trigger. A cache
with no invalidation signal needs a manual flush, and a material reassigned with the dialog closed is
exactly a change this feature has no way to observe.

Implemented with **`isButton`** (`feature.fs:1032`), which is an actual momentary button:

```featurescript
annotation { "Name" : "Re-read materials", "Description" : "..." }
isButton(definition.refreshMaterials);
```

The predicate is just `value is undefined`, so the parameter holds no state, there is nothing to revert,
and it must be **omitted from `defineFeature`'s defaults map** — giving it a value breaks the predicate.
The press arrives in editing logic as `clickedButton`, the **7th** parameter:

```featurescript
export function tippyBucketPivotEditingLogic(context is Context, id is Id, oldDefinition is map,
    definition is map, isCreating is boolean, specifiedParameters is map, clickedButton is string) returns map
```

tested as `clickedButton == "refreshMaterials"`, which here emits a console trace of every material read
— pressing the button is an act of verification, so it should say what it found.

A boolean with `UIHint.OPPOSITE_DIRECTION_CIRCULAR` is **not** a button and was wrong in an earlier
draft: it is a two-state toggle wearing a button's icon. In-repo examples of the real thing:
`custom-features/autoLayout.fs:111` with its signature at `:979`, and
`custom-features/betterThanBoolean.fs:113`.

```featurescript
try
{
    const partMaterial = getProperty(context, {
                "entity" : bucketPart.solidBody,
                "propertyType" : PropertyType.MATERIAL
            });
    bucketPart.materialName = partMaterial.name;
    bucketPart.densityKilogramsPerCubicMeter = partMaterial.density / (kilogram / meter ^ 3);
    bucketPart.materialWasRead = true;
}
catch (materialError)
{
    bucketPart.materialName = "";
    bucketPart.densityKilogramsPerCubicMeter = 0;
    bucketPart.materialWasRead = false;
}
```

Plain `try`/`catch`, not `try silent` — per `AGENTS.md:27`, a missing material must still report to the
console while the flag drives the regen-time throw.

Also read `PropertyType.MASS_OVERRIDE` into `massOverrideKilograms` (0 when absent). Note that
`getProperty` returns `undefined` for an unset property (`properties.fs:110`) as well as throwing for
some cases, so guard both.

Editing logic sets flags and caches only. **All throwing happens in the feature body**, where
`regenError(message, faultyParameters, entities)` (`error.fs:108`) can highlight the offending bodies
red in the graphics area. An error thrown from editing logic cannot do that, and "which part is missing
a material" is exactly what the drafter needs to see.

---

## 5. Regen algorithm

1. **Validate selections.** Reject an empty `bucketParts`; reject an empty `fillSolid`. Throw if the
   fill body also appears in `bucketParts` (`qIntersection` non-empty) — double-counted mass would be
   a silent, catastrophic wrong answer.
2. **Material gate.** Collect every bucket part with `materialWasRead == false` into a `qUnion`, and if
   non-empty:
   ```featurescript
   throw regenError("Assign a material to every bucket solid. Mass-critical placement cannot proceed "
                    ~ "with undefined material.", ["bucketParts"], faultyBodies);
   ```
   Same treatment for a material present but zero density (allowed since `V2539_ALLOW_ZERO_DENSITY`,
   and useless here), and the same gate again for the fill solid. No mass-override escape hatch: a
   material is required unconditionally.
3. **Per body mass properties.**
   ```featurescript
   const bodyMassProperties = evApproximateMassProperties(context, {
               "entities" : bucketPart.solidBody,
               "density" : bucketPart.densityKilogramsPerCubicMeter * kilogram / meter ^ 3
           });
   ```
   Returns `mass`, `centroid` (world coordinates when `referenceFrame` is omitted), `volume`,
   `inertia` — `evaluate.fs:93-143`. When a mass override applies, substitute the override for
   `mass` and keep the geometric `centroid`.
4. **Accuracy cross-check.** Independently compute `Σ density_i * evVolume(HIGH)` and compare with
   `Σ mass_i` from step 3. Report the discrepancy; warn past a tolerance. `evApproximateMassProperties`
   and `evApproximateCentroid` both carry an explicit approximation warning
   (`evaluate.fs:31-34`, `evaluate.fs:80-82`), and Onshape's own mass property uses `VolumeAccuracy.LOW`
   (`massProperty.fs:38`). For a mass-critical tool, publish the residual instead of pretending it is zero.
5. **Fill mass properties.** Same call over `fillSolid` with the cached fill density. Then warn only on
   *shared volume* with the bucket: run `evCollision(context, { "tools" : fillSolid, "targets" :
   bucketBodies })` (`evaluate.fs:216`) and count clashes whose `type` is one of `INTERFERE`,
   `TARGET_IN_TOOL`, `TOOL_IN_TARGET`. **Ignore every `ABUT_*` type** — water modelled at its fill
   level rests against the walls, so abutting is what a correct fill solid looks like and warning on it
   flags good models as bad. Warn rather than throw even for real overlap, and wrap the call so a
   collision failure degrades to a warning rather than killing the feature.
6. **Resolve frame** — §1.1, §1.4.
7. **Combine** — §1.2. Throw on `u1 <= u0` with the coaching message.
8. **Pivot point** — §1.3, §1.4. Assemble from the three scalar coordinates back into a world `Vector`.
9. **Emit geometry** — §6.
10. **Report** — `reportFeatureInfo` with empty mass, fill mass, both CoM heights, chosen fraction,
    pivot height, both torque authorities, tip direction, and the step-4 residual. Optionally surface a
    one-line summary in the feature list through `"Feature Name Template"`.

### Mass overrides — resolved

An earlier draft let a `MASS_OVERRIDE` stand in for a missing material behind an `allowMassOverride`
checkbox. That is gone: a material is required unconditionally, per the one-source-of-truth rule.

A mass override is still *read* and still *used* for a part's mass when one is present, because someone
set it deliberately and it beats density × volume on purchased and imported parts. The centroid always
comes from geometry. Parts where this happened are counted in the readout so it is never silent, and
those parts are excluded from the step-4 accuracy cross-check (their mass does not come from volume, so
comparing it against a volume measurement would be meaningless).

---

## 6. Output geometry

**Pivot mate connector** — `opMateConnector(context, id + "pivotMateConnector", { coordSystem, owner })`
(`geomOperations.fs:1150`).

```featurescript
coordSystem(pivotPoint, tippingDirection, pivotAxisDirection)   // coordSystem.fs:77 (origin, xAxis, zAxis)
```

**Z along the pivot axis** is the point of the whole feature: an Onshape revolute mate rotates about
the mate connector's Z, so the drafter drops a revolute mate on this connector and the tippy works.
X along `tippingDirection` makes the tip direction visible in the triad.

`owner` must be a bucket body or the connector will not travel into the assembly — default to the
highest-mass bucket solid, overridable.

**Center of mass points** — `opPoint(context, id + "...", { "point" : ... })`
(`geomOperations.fs:1446`), one each for empty CoM, filled CoM, and fill centroid, behind
`createCenterOfMassPoints`. Real geometry: persistent, selectable, measurable, so the drafter can
dimension to them and check the story themselves. Name them via `setProperty` / `PropertyType.NAME`
("CoM empty", "CoM filled", "CoM water") — `setProperty` *is* legal during regen, only `getProperty`
is not.

**Debug overlay** — behind `showDebugOverlay`: `debug` the empty CoM green, the filled CoM red, the
CoM travel segment, and the pivot axis. Transient (visible only with the feature rolled to or
selected), which is why the persistent points above are the default rather than the overlay.

---

## 7. Phase 2 — real trip volume

Everything above treats the supplied water solid as the fill *at trip*. The number a drafter actually
wants is "how much water tips it", which needs partial fills. First-order estimate from a fixed fill
centroid:

```
tripFillMassFraction ≈ m0 * (pivotHeight - u0) / (mw * (uw - pivotHeight))
```

This is optimistic — a bucket filling from the bottom has a partial-fill centroid well below the full
centroid — so report it explicitly labelled as a first-order estimate, or do it properly:

Slice the fill body with a horizontal plane at level `L`, take the lower piece, and get its real
volume and centroid. Bisect on `L` to solve `combinedCenterOfMassHeight(L) = pivotHeight`. About 12–15
iterations, each a split plus an `evApproximateMassProperties`, all under feature-scoped ids and
`opDeleteBodies`d afterwards so nothing reaches the parts list. `custom-features/sectionSlicerMain.fs:329`
is the precedent for measuring section pieces.

Same machinery inverted gives the `TARGET_TRIP_FILL` threshold mode: the drafter says "trip at 80% of
this volume", the sweep finds the fill level at that volume, and `pivotHeight` is set to the combined
CoM height there — that is exactly the marginal condition, no fraction to hand-tune. Arguably the mode
drafters should be using; it is Phase 2 only because it costs geometry operations.

Also Phase 2: warn when the fill body has no planar face perpendicular to `upDirection`, which usually
means the water was modeled in the wrong attitude.

---

## 8. Accuracy and staleness — state these in the feature description

1. `evApproximateMassProperties` is tessellation-based and Onshape may change the approximation, which
   would shift the pivot slightly. The step-4 residual quantifies it per regen. This is the same
   approximation behind Onshape's own mass properties dialog.
2. Materials are cached at dialog time. Reassign a material with the dialog closed and the feature is
   working from the old density until reopened or `refreshMaterials` is toggled. Unavoidable given
   `properties.fs:71`.
3. The §1.3 criterion assumes the rest attitude is a free equilibrium or a stop at/beyond it. Enable
   `checkStopAngle` for a hard-stop design.
4. Rigid-body statics only: no sloshing, no dynamics, no bearing friction, no surface tension at the
   drain lip. Friction in particular raises the real trip volume above the computed one, so the
   `BALANCED` default deliberately leaves tip authority in hand rather than sitting at the margin.

---

## 9. Verified API inventory

Everything below was checked against this mirror before being written into the plan.

| Function | Location | Note |
| --- | --- | --- |
| `getProperty` | `properties.fs:95` | **editing logic only**; `MATERIAL` → `Material{name, density}` |
| `setProperty` | `properties.fs:41` | legal during regen |
| `Material` / `material()` | `properties.fs:163-183` | `density.unit == DENSITY_UNITS` |
| `PropertyType` | `propertytype.gen.fs:12` | `MATERIAL`, `MASS_OVERRIDE`, `NAME` all present |
| `evApproximateMassProperties` | `evaluate.fs:93` | `{entities, density, referenceFrame?}` → `{mass, centroid, volume, inertia}` |
| `MassProperties` type | `evaluate.fs:61` | typecheck `canBeMassProperties` |
| `evVolume` | `evaluate.fs:1329` | `{entities, accuracy}`, `VolumeAccuracy.HIGH` for the cross-check |
| `evCollision` | `evaluate.fs:216` | `{tools, targets}` → array of clashes |
| `evAxis` | `evaluate.fs:172` | throws when the entity has no axis |
| `extractDirection` | `topologyUtils.fs:124` | `evAxis` then `evPlane`, returns `undefined` on failure |
| `opMateConnector` | `geomOperations.fs:1150` | `{coordSystem, owner, attachTo?}` |
| `opPoint` | `geomOperations.fs:1446` | `{point}` → `BodyType.POINT` |
| `coordSystem` | `coordSystem.fs:77` | `(origin, xAxis, zAxis)` |
| `line` | `curveGeometry.fs:53` | pivot axis for the debug overlay |
| `ClashType` | `clashtype.gen.fs:21` | `ABUT_*` = touching, `INTERFERE`/`*_IN_*` = shared volume |
| `roundToPrecision` | `math.fs:261` | `(value, decimalPlaces)`, for the readout |
| `newtonMillimeter` | `units.fs:315` | torque readout unit |
| `regenError` | `error.fs:108` | `(message, faultyParameters, entities)` — highlights bodies |
| `reportFeatureInfo` | `error.fs:302` | custom string overload |
| `setErrorEntities` | `error.fs:475` | alternative highlight channel |
| `VolumeAccuracy` | `volumeaccuracy.gen.fs:17` | `LOW` / `MEDIUM` / `HIGH` |

`evApproximateCentroid` (`evaluate.fs:39`) is deliberately **not** used for any mass calculation — it
ignores density, so it is wrong for a multi-material bucket, and `evApproximateMassProperties`
supersedes it. It is used in exactly one place: reading a point off the optional axial location
reference, where the value is cosmetic placement along the axis and never enters the physics.

`rotationAround` and `project` were in the plan for the tilt check and are no longer used anywhere
(§1.5).

Note there is no `opMateConnector`-free path and no exact mass-property evaluator in FeatureScript;
`defineComputedPartProperty` (`massProperty.fs:18`) can read materials outside regen but only returns a
property value per part, so it cannot place geometry. Worth remembering if a companion "verify tippy
mass" custom property is ever wanted.

---

## 10. Build order

1. Skeleton: header at `FeatureScript 3029`, precondition, driven-query array, editing logic that only
   caches densities. Verify in Onshape that the hidden per-item density actually round-trips to regen —
   this is the one assumption worth proving before any physics is written.
2. Empty-state mass properties + `reportFeatureInfo` readout. Confirm against Onshape's mass properties
   dialog on a real multi-material bucket.
3. Fill state, combine, feasibility throw, material-missing throw.
4. Frame resolution, `BALANCED` pivot placement, mate connector.
5. CoM points, debug overlay, torque readout.
6. Stop-angle check, accuracy residual, collision warning.
7. Phase 2 sweep and `TARGET_TRIP_FILL`.

Steps 1–2 are where this either works or does not; the rest is arithmetic on top of them.
