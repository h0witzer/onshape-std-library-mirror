/*
    Remove Second Side Features

    Companion feature for Auto Layout+. Finds all bodies that have been marked as
    "placed" by the Auto Layout feature and removes geometry that would require a
    second machine setup: bottom-side pockets, countersinks, counterbores, and any
    other concave features whose opening faces the bottom (non-cut) side of the part.

    Usage: run this feature AFTER Auto Layout+ in the feature tree. All parts that
    were nested by Auto Layout will have their second-side geometry deleted and the
    resulting voids healed, leaving only features accessible from the top (cut) side.

    Algorithm overview:
        1. Locate every solid body carrying the AutoLayout_PLACED attribute.
        2. Auto Layout places all parts with their bottom face in the world XY plane,
           so world -Z is always the downward direction for every placed body.
        3. Call opSplitBySelfShadow with viewDirection = world -Z (downward) to split
           each body into faces visible from above (accessible to the mill) and faces
           invisible from above (second-side features).
        4. Build a keep set — faces that must NOT be deleted even if they land in the
           invisible set:
             a. All visible faces (top surfaces and illuminated side walls).
             b. All faces vertex-adjacent to any visible face, so that side walls whose
                shadow boundary passes through a shared vertex are also preserved.
             c. All faces coplanar with the bottom exterior plane (the stock bed, Z=0
                in the studio). These are the flat exterior bottom faces that cap the
                part against the spoilboard.
        5. Delete set = invisible faces minus keep set.  These are blind-bottom pockets,
           countersinks, counterbores, and similar features reachable only from below.
        6. Delete those faces with opDeleteFace (heal mode), per-body, with error
           isolation so one failing body does not abort the rest.

    Why opSplitBySelfShadow correctly handles through-holes:
        Under a parallel projection from above, rays entering a through-hole from
        the top opening illuminate the cylindrical wall -- it is NOT self-shadowed.
        Through-holes land in the visible set and are preserved. Blind bottom pockets
        are fully blocked by surrounding material and land in the invisible set.
*/

FeatureScript 2909;
import(path : "onshape/std/geometry.fs", version : "2909.0");
import(path : "aa8ee374e7061289b937b984", version : "b0af54cc89dae3c240e344e8"); //autoLayoutConfig.fs
import(path : "bb79595d1ad4e6528fb60762", version : "20987b283a5fd1abb9b2d6f5"); //autoLayoutTypes.fs

/**
 * Remove Second Side Features
 *
 * Scans all bodies placed by Auto Layout+ and deletes geometry that is only
 * accessible from the bottom (second-setup) side of the part: pockets,
 * countersinks, counterbores, and any concave features whose entrance faces
 * downward. Auto Layout places all parts with their bottom face in the world
 * XY plane, so world -Z is the tool approach direction for every placed body.
 * The resulting voids are healed automatically.
 *
 * Place this feature immediately after Auto Layout+ in the feature tree.
 */
annotation { "Feature Type Name" : "Remove Second Side Features",
        "Feature Type Description" : "Companion for Auto Layout+. Removes bottom-side pockets, countersinks, counterbores, and other geometry requiring a second machine setup from all Auto Layout placed parts." }
export const removeSecondSideFeatures = defineFeature(function(context is Context, id is Id, definition is map)
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

        var bodiesProcessed = 0;
        var bodiesModified = 0;

        // Step 2: Process each placed body independently so that a failure on one
        // body does not abort cleanup of the remaining bodies.
        for (var bodyIndex = 0; bodyIndex < placedBodyCount; bodyIndex += 1)
        {
            const body = qNthElement(placedBodies, bodyIndex);

            // Auto Layout places all parts with their bottom face in the world XY
            // plane. World -Z is therefore the downward direction for every placed
            // body and is used as the viewDirection for the self-shadow analysis.
            const WORLD_DOWN = vector(0, 0, -1);

            // All faces owned by this body. Defined here so the query re-resolves
            // lazily and reflects the post-split state after opSplitBySelfShadow.
            const bodyFaces = qOwnedByBody(body, EntityType.FACE);

            // Z-parallel planar faces include the bottom exterior face and any
            // pocket floors. If none exist the body has no horizontal geometry.
            const horizontalFaces = qParallelPlanes(bodyFaces, WORLD_DOWN);

            if (isQueryEmpty(context, horizontalFaces))
            {
                if (definition.enableDiagnostics)
                    reportFeatureInfo(context, id,
                        "Body " ~ bodyIndex ~ ": no Z-aligned planar face found -- skipping.");
                continue;
            }

            bodiesProcessed += 1;

            // Step 3: Split into visible/invisible regions from above.
            const shadowId = id + ("shadow_" ~ bodyIndex);
            const shadowResult = opSplitBySelfShadow(context, shadowId, {
                        "bodies"        : body,
                        "viewDirection" : WORLD_DOWN
                    });

            // Use the full faces arrays so entirely-visible or entirely-invisible
            // faces (never split) are not missed.
            const invisibleFacesQuery = qUnion(shadowResult.invisibleFaces);
            const visibleFacesQuery   = qUnion(shadowResult.visibleFaces);

            // Step 4: Build the keep set — everything that must not be deleted.
            //
            // (a) All faces visible from above (top surfaces and illuminated walls).
            //
            // (b) All faces vertex-adjacent to any visible face.  The shadow
            //     boundary can pass through a shared vertex, leaving part of a side
            //     wall in the invisible set even though it is topologically connected
            //     to a visible top face.  Including vertex-adjacent faces ensures
            //     those connected side walls are preserved.
            const vertexAdjacentFaces = qAdjacent(visibleFacesQuery, AdjacencyType.VERTEX, EntityType.FACE);

            // (c) All faces coplanar with the stock-bed plane (Z=0, the world Top
            //     plane).  Auto Layout places every part bottom-side-down at Z=0, so
            //     these are the flat exterior bottom faces that sit against the
            //     spoilboard.  The farthest-along face gives us the reference Z; we
            //     then collect every horizontal face at that same elevation.
            const bottomReferenceFace = qFarthestAlong(horizontalFaces, WORLD_DOWN);
            var coplanarWithBottom = bottomReferenceFace;
            if (!isQueryEmpty(context, bottomReferenceFace))
            {
                try
                {
                    const refZ = evPlane(context, { "face" : bottomReferenceFace }).origin[2];
                    var coplanarList = [];
                    for (var hFace in evaluateQuery(context, horizontalFaces))
                    {
                        try
                        {
                            const fp = evPlane(context, { "face" : hFace });
                            if (abs(fp.origin[2] - refZ) < TOLERANCE.zeroLength * meter)
                                coplanarList = append(coplanarList, hFace);
                        }
                    }
                    if (size(coplanarList) > 0)
                        coplanarWithBottom = qUnion(coplanarList);
                }
            }

            const keepFaces = qUnion([visibleFacesQuery, vertexAdjacentFaces, coplanarWithBottom]);

            // Step 5: Second-side faces = invisible from above minus keep set.
            const secondSideFaces = qSubtraction(invisibleFacesQuery, keepFaces);

            if (definition.enableDiagnostics)
            {
                // Blue:    full invisible-from-above set
                addDebugEntities(context, invisibleFacesQuery, DebugColor.BLUE);
                // Green:   visible-from-above faces (shadow result)
                addDebugEntities(context, visibleFacesQuery, DebugColor.GREEN);
                // Cyan:    vertex-adjacent faces added to the keep set
                addDebugEntities(context, vertexAdjacentFaces, DebugColor.CYAN);
                // Yellow:  faces coplanar with the stock-bed plane (Z=0)
                addDebugEntities(context, coplanarWithBottom, DebugColor.YELLOW);
                // Red:     faces that will be sent to opDeleteFace
                addDebugEntities(context, secondSideFaces, DebugColor.RED);
            }

            if (isQueryEmpty(context, secondSideFaces))
            {
                if (definition.enableDiagnostics)
                    reportFeatureInfo(context, id,
                        "Body " ~ bodyIndex ~ ": no second-side features detected.");
                continue;
            }

            if (definition.enableDiagnostics)
            {
                const removedFaceCount = evaluateQueryCount(context, secondSideFaces);
                reportFeatureInfo(context, id,
                    "Body " ~ bodyIndex ~ ": removing " ~ removedFaceCount ~ " second-side face(s).");
            }

            // Step 6: Delete second-side faces and heal the voids.
            var deleteSucceeded = false;
            try
            {
                opDeleteFace(context, id + ("deleteFace_" ~ bodyIndex), {
                            "deleteFaces"   : secondSideFaces,
                            "includeFillet" : true,
                            "capVoid"       : true,
                            "leaveOpen"     : false
                        });
                deleteSucceeded = true;
            }

            if (deleteSucceeded)
            {
                bodiesModified += 1;
            }
            else
            {
                reportFeatureWarning(context, id,
                    "Body " ~ bodyIndex ~ ": could not remove second-side faces (geometry may be too complex to heal automatically). Manual cleanup may be needed.");
            }
        }

        reportFeatureInfo(context, id,
            "Processed " ~ bodiesProcessed ~ " body/bodies. " ~
            "Second-side features removed from " ~ bodiesModified ~ " body/bodies.");

    }, { enableDiagnostics : false });


