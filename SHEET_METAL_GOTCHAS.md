# Sheet Metal Feature Development Gotchas

This document catalogs non-obvious requirements and behaviors when developing sheet metal features in FeatureScript for Onshape.

## Critical Requirements

### 1. Context Naming - Function Name Must Be "sheetMetalStart"

**Issue**: Custom sheet metal features show "Unknown" as the context name in the flat pattern context window.

**Root Cause**: The flat pattern context window in Onshape only inherits the feature name if the main feature function is explicitly named `sheetMetalStart`.

**Solution**: Name your main feature function exactly `sheetMetalStart` (not `sheetMetalStartCustom`, `mySheetMetalStart`, etc.)

```featurescript
// ✅ CORRECT - Context will be named properly
export function sheetMetalStart(context is Context, id is Id, definition is map)
{
    // your implementation
}

// ❌ WRONG - Context name will show as "Unknown"
export function customSheetMetal(context is Context, id is Id, definition is map)
{
    // your implementation
}
```

**Impact**: This is a hard-coded requirement in Onshape's system and cannot be worked around through ID naming, attributes, or other approaches.

---

### 2. Surface Body Visibility - Must Use defineSheetMetalFeature

**Issue**: Surface bodies (the sheet metal definition bodies) remain visible in the viewport even when properly annotated with sheet metal attributes.

**Root Cause**: Surface bodies will not be automatically hidden unless:
1. The feature is defined using `defineSheetMetalFeature` instead of `defineFeature`, OR
2. Another sheet metal feature interacts with the bodies, forcing a sheet metal update

**Solution**: Use `defineSheetMetalFeature` to define your feature:

```featurescript
// ✅ CORRECT - Surface bodies will be hidden automatically
defineSheetMetalFeature(sheetMetalStart, {
    // feature definition
});

// ❌ WRONG - Surface bodies remain visible
defineFeature(sheetMetalStart, {
    // feature definition
});
```

**Workaround**: If you cannot use `defineSheetMetalFeature`, surface bodies will become hidden after any subsequent sheet metal operation (like move face) interacts with them, as this forces a sheet metal geometry update.

**Impact**: Visible surface bodies are confusing to users and make the model appear broken, even though the sheet metal functionality works correctly.

---

### 3. ID Pattern for Operations vs Queries

**Issue**: Using the wrong ID for queries causes double surfaces/parts and tracking issues.

**Pattern**: SheetMetalStart uses `id + "extractSurface"` for the operation but references bodies/faces/edges with just `id`:

```featurescript
// Operation uses sub-ID
var surfaceId = id + "extractSurface";
opExtractSurface(context, surfaceId, {
    "faces" : facesToExtract
});

// Annotation and queries use BASE ID (not surfaceId!)
annotateSmSurfaceBodies(context, id, {
    "surfaceBodies" : qCreatedBy(id, EntityType.BODY),  // NOT surfaceId!
    // ...
});

updateSheetMetalGeometry(context, id, {
    "entities" : qUnion([
        qCreatedBy(id, EntityType.FACE),   // NOT surfaceId!
        qCreatedBy(id, EntityType.EDGE)
    ])
});
```

**Why**: The operation ID is used internally for the geometry operation, but Onshape's sheet metal system tracks entities based on the base feature ID for context management.

---

## Best Practices

### Follow the Canonical Pattern

When implementing sheet metal features, follow the exact pattern from `sheetMetalStart.fs`:

1. Use `defineSheetMetalFeature` for feature definition
2. Name main function exactly `sheetMetalStart` for proper context naming
3. Extract surfaces with `id + "extractSurface"` (or similar sub-ID)
4. Annotate and finalize using base `id` with `qCreatedBy(id, ...)`
5. Use `annotateSmSurfaceBodies` for setting MODEL, WALL attributes
6. Use `updateSheetMetalGeometry` to finalize and create 3D representations

### Don't Take Shortcuts

- Attempting to simplify or optimize away from the canonical pattern often leads to broken behavior
- The pattern exists because of specific requirements in Onshape's sheet metal engine
- What looks like unnecessary complexity is often required for correct operation

### Test Thoroughly

When developing sheet metal features, verify:
- [ ] Context name appears correctly (not "Unknown")
- [ ] Surface bodies are hidden from viewport
- [ ] Sheet metal attributes are properly set
- [ ] Flat pattern generates correctly
- [ ] Multiple instances don't interfere with each other
- [ ] Subsequent operations work correctly on the sheet metal bodies

---

## Common Pitfalls

### Pitfall: Assuming `defineFeature` is sufficient
**Problem**: Surface bodies remain visible  
**Solution**: Use `defineSheetMetalFeature`

### Pitfall: Using creative function names
**Problem**: Context shows as "Unknown"  
**Solution**: Name function exactly `sheetMetalStart`

### Pitfall: Using operation sub-IDs in queries
**Problem**: Double surfaces, tracking issues  
**Solution**: Use base `id` for `qCreatedBy` queries

### Pitfall: Deleting surface bodies after creation
**Problem**: Sheet metal context is destroyed  
**Solution**: Surface bodies must remain (they are the sheet metal definition)

---

### 4. opBoolean UNION with SM Definition Bodies Requires Exact Coincidence

**Issue**: `opBoolean UNION` with `allowSheets: true` throws `BOOLEAN_INVALID` or silently no-ops even when a tab surface body appears to be on the correct face.

**Root Cause**: The SM definition (master surface) body lives on exactly one face of the rendered 3D wall — either the inner or the outer face, never the midplane. If a derived tab body is positioned on the *opposite* face from the SM definition face, it is offset by the full wall thickness and the UNION kernel cannot merge two surfaces that are not geometrically coincident.

**For planar SM walls** — align via rigid translation:

1. Call `getSMDefinitionEntities` on the union scope to get the definition faces.
2. Evaluate `evPlane` on each definition face to collect their planes.
3. For each tab body, find the nearest definition plane by Euclidean distance.
4. If the body's face normal is antiparallel to the definition plane normal, call `opFlipOrientation` to reverse the surface direction.
5. Translate along the wall normal by `dot(wallOrigin - bodyOrigin, wallNormal)` to achieve exact coincidence.

```featurescript
// Snap a surface body onto the nearest SM definition plane
const snapTranslationVector = dot(nearestDefinitionPlane.origin - bodyFacePlane.origin,
        nearestDefinitionPlane.normal) * nearestDefinitionPlane.normal;
opTransform(context, snapId, {
            "bodies"    : currentBody,
            "transform" : transform(snapTranslationVector)
        });
```

**For non-planar SM walls** (cylindrical, conical, freeform) — rigid translation onto the definition surface is insufficient because the definition geometry is curved. Alternative approaches include:

- **`opWrap`**: Project the tab surface bodies onto the curved SM definition face. Suitable when the tab geometry can be wrapped conformally onto the target surface.
- **Surface offsetting**: Use `opOffsetFace` or surface-based operations to bring the tab body into contact with the definition surface before the boolean.

The `smTabApply.fs` implementation uses the planar snap approach. The tag Part Studio contract is topology-agnostic, so non-planar support can be added without changes to the tagging side.

**Use `evFaceTangentPlane` for orientation detection** when the SM wall may not be planar: `evFaceTangentPlane(context, { "face": face, "parameter": vector(0.5, 0.5) })` evaluates for any surface geometry (planar, cylindrical, conical). `evPlane` throws on non-planar faces.

**Impact**: Without coincidence between the tab surface and the SM definition face, the UNION silently no-ops or throws, and the tab is never merged into the SM wall.

---

### 5. opBoolean UNION on SM Definition Body — Body-Level startTracking Resolves to Empty After the UNION

**Issue**: After calling `opBoolean UNION` to merge tab surfaces into the SM definition body, any `startTracking` query that was anchored to the SM body (via `qOwnerBody(...)`) resolves to an empty query.

**Root Cause**: `opBoolean UNION` internally restructures the SM definition body's face topology. Body-level tracking (`startTracking(context, qOwnerBody(definitionEntities))`) tracks the body container, which is recreated internally even though `qCreatedBy` returns 0 results — the tracking anchor is lost.

**Solution**: Use **face-level** tracking on the SM definition *faces* instead of the body, and derive the body from that query after each operation:

```featurescript
// Track definition faces — these survive body-level restructuring
const persistentUnionDefinitionEntities = qUnion([
            unionDefinitionEntitiesQuery,
            startTracking(context, unionDefinitionEntitiesQuery)
        ]);

// After any opBoolean UNION, derive the live body from face tracking
const smBodyPostUnion = qOwnerBody(persistentUnionDefinitionEntities);
```

**Pattern**: This is the `unionEntityPersistantQuery` pattern from `sheetMetalTab.fs`. Always use it when the SM definition body will be mutated by boolean operations.

---

### 6. Operation History Non-Contiguous Parent ID Violation with opThicken

**Issue**: `opThicken` throws: *"Parent Id X used at two non-contiguous points in operation history (Cannot have Y between X.thicken and X.thickenForDeRip)"*.

**Root Cause**: Onshape requires that all operations sharing the same **parent** ID be contiguous in the operation history. When a loop assigns IDs like `id + "flip" + unstableIdComponent(N)` and `id + "thicken" + unstableIdComponent(N)`, the parent `id.flip` and `id.thicken` each receive multiple children across loop iterations, and those children interleave: `flip.*0`, `thicken.*0`, `flip.*1`, `thicken.*1` — making the parent `id.flip` non-contiguous.

A second version of this problem arises when a Phase 5 loop uses `id + unstableIdComponent(N)` as body sub-IDs, and a later per-location loop also uses `id + unstableIdComponent(N)` as location sub-IDs — both loops consume `id.*0`, `id.*1`, etc., creating non-contiguous parents across phases.

**Solution**: Give each loop iteration its own unique parent namespace and place **all** of that iteration's operations underneath it:

```featurescript
// Each body gets its own parent sub-ID so flip and thicken are siblings under it
const bodySubId = id + "outerSubtractBody" + unstableIdComponent(bodyIndex);
opFlipOrientation(context, bodySubId + "flip", { "bodies" : currentBody });
opThicken(context, bodySubId + "thicken", { ... });

// Per-location loop uses a separate string prefix to avoid collision with Phase 5
const locationId = id + unstableIdComponent(locationIndex);
opThicken(context, locationId + "thickenForDeRip", { ... });
```

The intermediate `"outerSubtractBody"` string between `id` and the unstable component prevents namespace collision with the per-location `id + unstableIdComponent(N)` IDs.

---

### 7. getSMDefinitionEntities Returns Stale Entity IDs After Boolean Operations

**Issue**: Calling `getSMDefinitionEntities` before `opBoolean UNION` and then using the result in Phase 9 after the UNION produces either empty results or errors because the entity IDs were invalidated when the SM topology changed.

**Root Cause**: Entity IDs in Onshape are transient references to topological entities. When `opBoolean UNION` or `deripEdges` restructures the SM definition body, the face/edge entity IDs from the pre-mutation call are no longer valid.

**Solution**: Call `getSMDefinitionEntities` **fresh** after all mutation phases have completed:

```featurescript
// WRONG — call before UNION, use after
const outerScopeDefFaces = getSMDefinitionEntities(context, outerScope); // stale after union
// ... opBoolean UNION and deripEdges ...
// Using outerScopeDefFaces here will fail or return wrong results

// CORRECT — call fresh after mutations
// ... opBoolean UNION and deripEdges ...
var freshOuterScopeDefinitionFaces = try(getSMDefinitionEntities(context, outerScope, EntityType.FACE));
```

---

### 8. Per-Location opBoolean UNION Processing Prevents Cascading Failures

**Issue**: Placing a single batch `opBoolean` call for all placement locations means a geometry failure at one location fails the entire feature.

**Solution**: Process each placement location in its own loop iteration with its own scoped operation IDs. Assign location-scoped operation IDs using `id + unstableIdComponent(locationIndex)` as the per-iteration parent, so each location's operations are isolated:

```featurescript
for (var locationIndex = 0; locationIndex < size(locationBodySets); locationIndex += 1)
{
    const locationId = id + unstableIdComponent(locationIndex);
    const locationUnionBodies = qHasAttributeWithValueMatching(locationBodies, ...);

    try
    {
        opBoolean(context, locationId + "unionTabToWall", { ... });
    }
    catch
    {
        // Log and continue — other locations still process
        throw regenError(ErrorStringEnum.SHEET_METAL_TAB_FAILS_MERGE, ["unionScope"]);
    }
}
```

---

### 9. Implied Outer Subtraction Bodies — opPattern Must Not Copy Attributes

**Issue**: When copying union surface bodies to serve as implied outer subtraction tools (when no dedicated outer subtract body is tagged), the copies must not carry the role attribute from the source bodies.

**Solution**: Use `opPattern` with `copyPropertiesAndAttributes: false`:

```featurescript
opPattern(context, copyId, {
            "entities"                    : sourceBody,
            "transforms"                  : [identityTransform()],
            "instanceNames"               : ["implied"],
            "copyPropertiesAndAttributes" : false
        });
```

If `copyPropertiesAndAttributes` is `true`, the copies would be found by role-attribute queries and included in the wrong boolean operation sets.

---

### 10. The SM Tab Workflow — Surface-Only Definition, No Authored Thickness

**Summary of the SM Tab Apply workflow** (for future feature implementors):

1. **Tag Part Studio** (`smTabTag.fs`): Authors mark bodies with role attributes via a custom attribute (`smTabBodyAttribute`). Roles are: `smTabUnionSurface` (merge into SM wall), `smTabLocalSubtractBody` (cut the SM wall), `smTabOuterSubtractBody` (cut outer scope parts). All bodies remain as pure surface bodies — thickness is never embedded.

2. **Apply** (`smTabApply.fs`): At regeneration time, the feature runs in numbered phases:
   - **Phase 5** — Snaps union/local-subtract bodies onto the SM definition face (planar translation + optional flip).
   - **Phase 7** — Thickens outer subtract bodies using `getModelParameters` from the target SM wall — thickness is always read from the live model.
   - **Phase 9** (per-location loop) — Runs deRip for rip joint resolution, `opBoolean UNION allowSheets: true` to merge the tab into the SM wall, and `opBoolean SUBTRACTION allowSheets: true` for local cuts. Each location is isolated so a failure at one location does not block others.
   - **Phase 10** — Runs `createBooleanToolsForFace` + `opBoolean localizedInFaces` for SM outer scope subtraction, and `opBoolean SUBTRACTION` for solid outer scope.
   - **Phase 12** — Calls `assignSMAttributesToNewOrSplitEntities` + `updateSheetMetalGeometry` to finalize SM state.

**Why surface-only in the tag Part Studio**: Keeping all tab bodies as surfaces means the tag Part Studio is completely gauge-agnostic. Swapping to a different material thickness requires no changes in the tool Part Studio — the apply feature reads the correct thickness at the moment of feature regeneration.

---

### 11. Master Surface Edits Don't Show Until a Later SM Tool — `associatedChanges` Must Be Body-Wide Face Tracking

**Issue**: A feature edits the SM master surface definition (e.g. `opReplaceFace`, `opOffsetFace`, `opMoveFace`) and builds without error, but the 3D solid and flat pattern do not update. The change only becomes visible after a *different* sheet metal tool (such as Move Face) is applied, which dirties the master surfaces again. This has bitten Bip Joints and other custom features.

**Root Cause**: `updateSheetMetalGeometry` rebuilds the 3D/flat representation only for the entities reported in `associatedChanges`. Tracking queries anchored to the *specific selected faces* resolve to empty after the operation, because ops like `opReplaceFace` delete and regenerate those faces — the tracking anchor is lost, so the change set is empty and the rebuild is deferred.

**Solution**: Snapshot **all faces of the SM definition body** with `startTracking` **before** the op, and pass that as `associatedChanges`. Body-wide tracking survives face regeneration. This mirrors `sheetMetalTab.fs:78` (`startTracking(context, qOwnedByBody(sheetMetalBodiesQuery, EntityType.FACE))`) and `moveFace.fs:1022-1027`.

```featurescript
const sheetMetalModels = qOwnerBody(masterFaces);
const associatedChanges = startTracking(context, qOwnedByBody(sheetMetalModels, EntityType.FACE)); // before the op
op...(context, id, { ... });
const toUpdate = assignSMAttributesToNewOrSplitEntities(context, sheetMetalModels, initialData, id);
updateSheetMetalGeometry(context, id, {
    "entities" : qUnion([toUpdate.modifiedEntities, associatedChanges]),
    "deletedAttributes" : toUpdate.deletedAttributes,
    "associatedChanges" : associatedChanges });
```

**Rule of thumb**: If a master-surface edit doesn't trigger a rebuild, your `associatedChanges` is too narrow or was anchored to entities the op destroyed. Always track the whole body's faces up front.

---

### 12. Bend Support on Master-Surface Edits — Reject Folds, Recompute Adjacent Angles

**Issue**: A master-surface edit (e.g. `opReplaceFace`) on a wall adjacent to a bend either corrupts the fold or leaves the bend at its old angle so the 3D/flat is geometrically wrong.

**Root Cause**: A replaced/moved wall changes the geometry feeding an adjacent bend, but bend faces store their fold angle as a joint attribute. The op does not recompute that attribute, and editing a bend (or a wall flanking a cylindrical bend) directly invalidates the fold the rebuild can no longer resolve.

**Solution**: Mirror Move Face. Reject the operation up front when a selected master face is a `SMJointType.BEND` or borders a cylindrical bend (`moveFace.fs:848-865`). After the op, call `updateJointAngle` on edges *and* faces adjacent to the edited faces, and grow the rebuild set with faces flanking each cylindrical bend so `updateSheetMetalGeometry` re-folds them (`moveFace.fs:986-991`, `:1021`).

```featurescript
// before the op: highlight the offending pick instead of failing inside the op
if (jointAttribute.jointType.value == SMJointType.BEND)
    throw regenError(ErrorStringEnum.SHEET_METAL_CANNOT_MOVE_BEND_EDGE, ["replaceFaces"], masterFace);
// after the op: re-fold adjacent bends
updateJointAngle(context, id, qUnion([qAdjacent(robust, AdjacencyType.EDGE, EntityType.EDGE),
                                      qAdjacent(robust, AdjacencyType.EDGE, EntityType.FACE)]));
modifiedFaces = qUnion([modifiedFaces, qAdjacent(qGeometry(modifiedFaces, GeometryType.CYLINDER), AdjacencyType.EDGE, EntityType.FACE)]);
```

**Rule of thumb**: There are reasons Move Face forbids certain bend selections; follow its gates rather than relaxing them. Always recompute neighbouring joint angles after a wall edit so bends re-fold.

---

## Version Information

This document is based on FeatureScript 2815 and Onshape Standard Library version 2815.0. Sections 4–10 were added during SM Tab Apply development (FeatureScript 2909). Sections 11–12 were added during Butcher Replace Face development (FeatureScript 2960).

## Contributing

If you discover additional sheet metal development gotchas, please document them here following the same format:
- Clear description of the issue
- Root cause explanation
- Concrete solution with code examples
- Impact assessment
