/*
    Remove Horizontal-Adjacent Fillets

    Companion feature for Auto Layout+. Strips fillet faces that bound any
    planar face parallel to the XY plane on each placed body, before any
    second-side cleanup is performed.  Side fillets (whose axis is parallel to
    Z) are preserved so they can be milled.

    Run this feature BEFORE Remove Second Side Features in the feature tree.

    Why this matters at the CNC:
        Fillets that sit against or span any horizontal step surface confuse the
        self-shadow split used by the second-side removal algorithm.  Removing
        those fillets before the shadow analysis produces clean, unambiguous
        face classifications.

    Algorithm:
        1. Locate every solid body carrying the AutoLayout_PLACED attribute.
        2. For each body collect every planar face whose normal is parallel to Z
           (i.e. every XY-parallel flat face at any elevation).
        3. Collect all fillet faces on the body.
        4. Intersect the fillet set with faces edge-adjacent to any horizontal
           face so that fillets at every elevation — not just the top and bottom
           extremes — are captured.
        4b. Exclude any fillet face whose cylinder/torus axis is parallel to Z;
            those are side fillets spanning two vertical walls and must stay.
        5. Call opModifyFillet (REMOVE_FILLET) for the remaining set, with error
           isolation per body so that a failing body does not abort the rest.
*/

FeatureScript 2909;
import(path : "onshape/std/geometry.fs", version : "2909.0");
import(path : "aa8ee374e7061289b937b984", version : "b0af54cc89dae3c240e344e8"); //autoLayoutConfig.fs
import(path : "bb79595d1ad4e6528fb60762", version : "20987b283a5fd1abb9b2d6f5"); //autoLayoutTypes.fs

/**
 * Remove Horizontal-Adjacent Fillets
 *
 * Scans all bodies placed by Auto Layout+ and removes fillet faces that bound
 * any planar face parallel to the XY plane, at any elevation.  Fillet faces
 * whose axis is Z-aligned (side fillets between two vertical walls) are left
 * intact.  Place this feature immediately before Remove Second Side Features
 * in the feature tree.
 */
annotation { "Feature Type Name" : "Remove Horizontal-Adjacent Fillets",
        "Feature Type Description" : "Companion for Auto Layout+. Strips fillets bounding any XY-parallel flat face on each placed body (Z-aligned side fillets are preserved). Run this before Remove Second Side Features." }
export const removeTopBottomFillets = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Enable diagnostics", "UIHint" : "DISPLAY_SHORT" }
        definition.enableDiagnostics is boolean;
    }
    {
        // Step 1: Locate all bodies placed by Auto Layout+.
        const placedBodies = qAttributeQuery("" as AutoLayoutAttribute);

        if (isQueryEmpty(context, placedBodies))
        {
            reportFeatureWarning(context, id,
                "No Auto Layout placed bodies found. Run Auto Layout+ before this feature.");
            return;
        }

        const placedBodyCount = evaluateQueryCount(context, placedBodies);

        if (definition.enableDiagnostics)
            reportFeatureInfo(context, id, "Found " ~ placedBodyCount ~ " Auto Layout placed body/bodies.");

        const WORLD_DOWN = vector(0, 0, -1);

        var bodiesProcessed = 0;
        var bodiesModified  = 0;

        // Step 2: Process each placed body independently.
        for (var bodyIndex = 0; bodyIndex < placedBodyCount; bodyIndex += 1)
        {
            const body      = qNthElement(placedBodies, bodyIndex);
            const bodyFaces = qOwnedByBody(body, EntityType.FACE);

            // Horizontal faces are parallel to the XY plane (normal || Z).
            const horizontalFaces = qParallelPlanes(bodyFaces, WORLD_DOWN);

            if (isQueryEmpty(context, horizontalFaces))
            {
                if (definition.enableDiagnostics)
                    reportFeatureInfo(context, id,
                        "Body " ~ bodyIndex ~ ": no Z-aligned planar face found -- skipping.");
                continue;
            }

            bodiesProcessed += 1;

            // Step 3: All fillet faces on this body.
            // qFilletFaces returns fillet faces matching the radius of any seed face
            // that is itself a fillet face. Passing all body faces as the seed
            // returns every fillet face on the body.
            const allFilletFaces = qFilletFaces(bodyFaces, CompareType.EQUAL);

            if (isQueryEmpty(context, allFilletFaces))
            {
                if (definition.enableDiagnostics)
                    reportFeatureInfo(context, id,
                        "Body " ~ bodyIndex ~ ": no fillet faces found -- skipping.");
                continue;
            }

            // Step 4: All fillet faces edge-adjacent to any horizontal face.
            // This captures fillets at every elevation — not only the top and
            // bottom extremes — so stepped faces and mid-height pockets are
            // covered in full.
            const horizontalAdjacentFillets = qIntersection([allFilletFaces,
                        qAdjacent(horizontalFaces, AdjacencyType.EDGE, EntityType.FACE)]);

            if (isQueryEmpty(context, horizontalAdjacentFillets))
            {
                if (definition.enableDiagnostics)
                    reportFeatureInfo(context, id,
                        "Body " ~ bodyIndex ~ ": no horizontal-adjacent fillet faces found.");
                continue;
            }

            // Step 4b: Exclude fillet faces whose axis is Z-aligned.  A Z-aligned
            // fillet axis means the fillet spans two vertical (side) faces — those
            // are milled features and must be preserved.
            var facesToRemove = [];
            for (var face in evaluateQuery(context, horizontalAdjacentFillets))
            {
                var surfDef;
                try silent { surfDef = evSurfaceDefinition(context, { "face" : face }); }
                if (surfDef == undefined)
                    continue;

                var filletAxis = undefined;
                if (surfDef is Cylinder)
                    filletAxis = surfDef.coordSystem.zAxis;
                else if (surfDef is Torus)
                    filletAxis = surfDef.coordSystem.zAxis;

                // Skip if the fillet axis is parallel to Z (side fillet).
                if (filletAxis != undefined && abs(dot(filletAxis, WORLD_DOWN)) > (1 - 1e-6))
                    continue;

                facesToRemove = append(facesToRemove, face);
            }

            if (size(facesToRemove) == 0)
            {
                if (definition.enableDiagnostics)
                    reportFeatureInfo(context, id,
                        "Body " ~ bodyIndex ~ ": all horizontal-adjacent fillets are Z-aligned side fillets -- skipping.");
                continue;
            }

            const filletFacesToRemove = qUnion(facesToRemove);

            if (definition.enableDiagnostics)
            {
                // Green:  horizontal (XY-parallel) seed faces
                addDebugEntities(context, horizontalFaces,     DebugColor.GREEN);
                // Yellow: fillet faces to be removed
                addDebugEntities(context, filletFacesToRemove, DebugColor.YELLOW);
                reportFeatureInfo(context, id,
                    "Body " ~ bodyIndex ~ ": removing " ~ size(facesToRemove) ~ " horizontal-adjacent fillet face(s).");
            }

            // Step 5: Remove the fillets.  opModifyFillet heals the geometry back
            // to the sharp edges that existed before the fillets were applied.
            var removeSucceeded = false;
            try
            {
                opModifyFillet(context, id + ("removeFillet_" ~ bodyIndex), {
                            "faces"            : filletFacesToRemove,
                            "modifyFilletType" : ModifyFilletType.REMOVE_FILLET
                        });
                removeSucceeded = true;
            }

            if (removeSucceeded)
            {
                bodiesModified += 1;
            }
            else
            {
                reportFeatureWarning(context, id,
                    "Body " ~ bodyIndex ~ ": could not remove horizontal-adjacent fillets (geometry may be too complex). Manual cleanup may be needed.");
            }
        }

        reportFeatureInfo(context, id,
            "Processed " ~ bodiesProcessed ~ " body/bodies. " ~
            "Horizontal-adjacent fillets removed from " ~ bodiesModified ~ " body/bodies.");

    }, {
        enableDiagnostics : false
    });
