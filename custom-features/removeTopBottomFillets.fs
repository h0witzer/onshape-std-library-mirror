/*
    Remove Top/Bottom Fillets

    Companion feature for Auto Layout+. Strips exterior fillet faces that sit at
    the top or bottom horizontal plane of each placed body before any second-side
    cleanup is performed.

    Run this feature BEFORE Remove Second Side Features in the feature tree.

    Why this matters at the CNC:
        Bottom fillets sit against the spoilboard — they cannot be cut without
        lifting the part, and they confuse the self-shadow split used by the
        second-side removal algorithm.  Top fillets that span the top-edge of the
        stock silhouette similarly straddle the shadow boundary and can drag
        adjacent side-wall geometry into the wrong classification set.  Removing
        both classes of fillets before the shadow analysis produces clean,
        unambiguous results.

    Algorithm:
        1. Locate every solid body carrying the AutoLayout_PLACED attribute.
        2. For each body determine the top and bottom horizontal reference planes:
             — bottom: the horizontal face farthest along -Z (the stock bed, Z=0).
             — top:    the horizontal face farthest along +Z (the top surface of
                       the stock).
           Coplanar faces at each elevation are grouped together so multi-face
           flat regions are handled correctly.
        3. Collect all fillet faces on the body.
        4. Intersect the fillet set with faces edge-adjacent to the top flat region
           to get the top fillet set; repeat for bottom.
        5. Call opModifyFillet (REMOVE_FILLET) once per non-empty set, with error
           isolation per body so that a failing body does not abort the rest.
*/

FeatureScript 2909;
import(path : "onshape/std/geometry.fs", version : "2909.0");
import(path : "aa8ee374e7061289b937b984", version : "b0af54cc89dae3c240e344e8"); //autoLayoutConfig.fs
import(path : "bb79595d1ad4e6528fb60762", version : "20987b283a5fd1abb9b2d6f5"); //autoLayoutTypes.fs

/**
 * Remove Top/Bottom Fillets
 *
 * Scans all bodies placed by Auto Layout+ and removes exterior fillet faces
 * at the top horizontal surface, the bottom horizontal surface, or both.
 * Place this feature immediately before Remove Second Side Features in the
 * feature tree.
 */
annotation { "Feature Type Name" : "Remove Top/Bottom Fillets",
        "Feature Type Description" : "Companion for Auto Layout+. Strips exterior fillets at the top and/or bottom horizontal faces of all placed bodies, run this before Remove Second Side Features." }
export const removeTopBottomFillets = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Remove top fillets" }
        definition.removeTopFillets is boolean;

        annotation { "Name" : "Remove bottom fillets" }
        definition.removeBottomFillets is boolean;

        annotation { "Name" : "Enable diagnostics", "UIHint" : "DISPLAY_SHORT" }
        definition.enableDiagnostics is boolean;
    }
    {
        if (!definition.removeTopFillets && !definition.removeBottomFillets)
        {
            reportFeatureWarning(context, id, "Select at least one of 'Remove top fillets' or 'Remove bottom fillets'.");
            return;
        }

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
        const WORLD_UP   = vector(0, 0,  1);

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

            // Step 4: Determine the flat regions at the top and bottom elevations.
            // The reference face is the single farthest-along horizontal face; we
            // then collect every horizontal face at that same Z so multi-face flat
            // regions are fully covered.
            var topFlatFaces    = qNothing();
            var bottomFlatFaces = qNothing();

            if (definition.removeTopFillets)
            {
                const topRef = qFarthestAlong(horizontalFaces, WORLD_UP);
                if (!isQueryEmpty(context, topRef))
                {
                    try
                    {
                        const refZ  = evPlane(context, { "face" : topRef }).origin[2];
                        var topList = [];
                        for (var hFace in evaluateQuery(context, horizontalFaces))
                        {
                            try
                            {
                                const fp = evPlane(context, { "face" : hFace });
                                if (abs(fp.origin[2] - refZ) < TOLERANCE.zeroLength * meter)
                                    topList = append(topList, hFace);
                            }
                        }
                        if (size(topList) > 0)
                            topFlatFaces = qUnion(topList);
                        else
                            topFlatFaces = topRef;
                    }
                }
            }

            if (definition.removeBottomFillets)
            {
                const bottomRef = qFarthestAlong(horizontalFaces, WORLD_DOWN);
                if (!isQueryEmpty(context, bottomRef))
                {
                    try
                    {
                        const refZ     = evPlane(context, { "face" : bottomRef }).origin[2];
                        var bottomList = [];
                        for (var hFace in evaluateQuery(context, horizontalFaces))
                        {
                            try
                            {
                                const fp = evPlane(context, { "face" : hFace });
                                if (abs(fp.origin[2] - refZ) < TOLERANCE.zeroLength * meter)
                                    bottomList = append(bottomList, hFace);
                            }
                        }
                        if (size(bottomList) > 0)
                            bottomFlatFaces = qUnion(bottomList);
                        else
                            bottomFlatFaces = bottomRef;
                    }
                }
            }

            // Step 5: Fillet faces edge-adjacent to the top/bottom flat region.
            const topFilletFaces    = qIntersection([allFilletFaces,
                                          qAdjacent(topFlatFaces,    AdjacencyType.EDGE, EntityType.FACE)]);
            const bottomFilletFaces = qIntersection([allFilletFaces,
                                          qAdjacent(bottomFlatFaces, AdjacencyType.EDGE, EntityType.FACE)]);

            const filletFacesToRemove = qUnion([topFilletFaces, bottomFilletFaces]);

            if (definition.enableDiagnostics)
            {
                // Green:  top flat reference faces
                addDebugEntities(context, topFlatFaces,    DebugColor.GREEN);
                // Blue:   bottom flat reference faces
                addDebugEntities(context, bottomFlatFaces, DebugColor.BLUE);
                // Yellow: top fillet faces to be removed
                addDebugEntities(context, topFilletFaces,    DebugColor.YELLOW);
                // Cyan:   bottom fillet faces to be removed
                addDebugEntities(context, bottomFilletFaces, DebugColor.CYAN);
            }

            if (isQueryEmpty(context, filletFacesToRemove))
            {
                if (definition.enableDiagnostics)
                    reportFeatureInfo(context, id,
                        "Body " ~ bodyIndex ~ ": no top/bottom fillet faces found.");
                continue;
            }

            if (definition.enableDiagnostics)
            {
                const count = evaluateQueryCount(context, filletFacesToRemove);
                reportFeatureInfo(context, id,
                    "Body " ~ bodyIndex ~ ": removing " ~ count ~ " top/bottom fillet face(s).");
            }

            // Step 6: Remove the fillets.  opModifyFillet heals the geometry back
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
                    "Body " ~ bodyIndex ~ ": could not remove top/bottom fillets (geometry may be too complex). Manual cleanup may be needed.");
            }
        }

        reportFeatureInfo(context, id,
            "Processed " ~ bodiesProcessed ~ " body/bodies. " ~
            "Top/bottom fillets removed from " ~ bodiesModified ~ " body/bodies.");

    }, {
        removeTopFillets    : true,
        removeBottomFillets : true,
        enableDiagnostics   : false
    });
