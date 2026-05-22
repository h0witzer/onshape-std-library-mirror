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
        2. Identify the bottom (non-cut) face of each body as its largest planar
           face. Auto Layout lays parts face-down on the cut sheet, so the largest
           face is the one resting on the table.
        3. Derive the tool approach direction (upward) as the negation of the
           bottom face's outward normal.
        4. Call opSplitBySelfShadow with viewDirection = upward to split each body
           into faces visible from above (accessible to the mill) and faces invisible
           from above (second-side features). The operation inserts shadow curve
           edges at visibility transitions.
        5. From the invisible set, subtract only the bottom exterior face (the flat
           face coplanar with the cut sheet). Side walls are not self-shadowed and
           land in the visible set automatically. What remains are blind bottom
           pockets, countersinks, counterbores, and similar features that require
           a second setup.
        6. Delete those faces with opDeleteFace (heal mode), per-body, with error
           isolation so one failing body does not abort the rest.

    Face orientation:
        Auto Layout orients each part so that the largest planar face lies face-down
        on the cut sheet. That face is therefore the BOTTOM exterior face. Its
        outward normal (as returned by evPlane) points away from the solid toward
        the machine table -- i.e. downward. The machining tool approaches from
        the opposite direction, which is the upward direction (-bottomFaceNormal).

    Why opSplitBySelfShadow correctly handles through-holes:
        Under a parallel projection from above, rays entering a through-hole from
        the top opening illuminate the cylindrical wall -- it is NOT self-shadowed.
        Through-holes therefore land in the visible set and are preserved. Blind
        bottom pockets are fully blocked by surrounding material and land in the
        invisible set, correctly targeting them for removal.
*/

FeatureScript 2909;
import(path : "onshape/std/geometry.fs", version : "2909.0");
import(path : "aa8ee374e7061289b937b984", version : "b0af54cc89dae3c240e344e8"); //autoLayoutConfig.fs
import(path : "bb79595d1ad4e6528fb60762", version : "20987b283a5fd1abb9b2d6f5"); //autoLayoutTypes.fs

// ---------------------------------------------------------------------------
// Feature definition
// ---------------------------------------------------------------------------

/**
 * Remove Second Side Features
 *
 * Scans all bodies placed by Auto Layout+ and deletes geometry that is only
 * accessible from the bottom (second-setup) side of the part: pockets,
 * countersinks, counterbores, and any concave features whose entrance is on
 * the bottom exterior face. Auto Layout lays parts with their largest planar
 * face down on the cut sheet, so that face is treated as the bottom. The
 * resulting voids are healed automatically.
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
        // ------------------------------------------------------------------
        // Step 1: Locate all bodies that Auto Layout+ has already placed.
        // The attribute "AutoLayout_PLACED" is stamped on each body after
        // it is successfully nested and transformed onto the cut sheet.
        // ------------------------------------------------------------------
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

        // ------------------------------------------------------------------
        // Step 2: Process each placed body independently so that a failure
        // on one body (e.g. opDeleteFace cannot heal a particular void) does
        // not abort cleanup of the remaining bodies.
        // ------------------------------------------------------------------
        for (var bodyIndex = 0; bodyIndex < placedBodyCount; bodyIndex += 1)
        {
            const body = qNthElement(placedBodies, bodyIndex);

            // Find the bottom exterior face: the largest planar face of the body.
            // Auto Layout orients every part so that this face rests on the cut sheet,
            // making it the bottom (non-cut) face. Its outward normal points away from
            // the solid toward the machine table -- i.e. downward.
            const bottomExteriorFace = qLargest(qGeometry(qOwnedByBody(body, EntityType.FACE), GeometryType.PLANE));

            if (isQueryEmpty(context, bottomExteriorFace))
            {
                if (definition.enableDiagnostics)
                    reportFeatureInfo(context, id,
                        "Body " ~ bodyIndex ~ ": no planar face found -- skipping (non-planar part?).");
                continue;
            }

            bodiesProcessed += 1;

            // Evaluate the bottom face plane to obtain its outward normal.
            // That normal points downward (toward the table). The up direction
            // (toward the machining tool) is the opposite.
            const bottomFacePlane = evPlane(context, { "face" : bottomExteriorFace });
            const upDirection = -bottomFacePlane.normal;

            // All faces owned by this body. This query re-resolves lazily, so
            // it reflects the post-split state when used after opSplitBySelfShadow.
            const bodyFaces = qOwnedByBody(body, EntityType.FACE);

            // ------------------------------------------------------------------
            // Step 3: Split the body into visible/invisible face regions with
            // respect to the tool approach direction (viewDirection = upward).
            //
            // opSplitBySelfShadow inserts shadow curve edges where a face
            // transitions from visible to invisible under a parallel projection
            // from the given viewDirection. The returned SplitBySelfShadowResult
            // contains two arrays:
            //   - visibleFaces:   faces accessible from above (top, through-holes)
            //   - invisibleFaces: faces blocked from above (second-side candidates)
            //
            // Through-holes remain in the visible set because rays from above
            // enter the top opening and illuminate the cylindrical wall -- they
            // are NOT self-shadowed. Blind bottom pockets and counterbores are
            // blocked by surrounding material and land in the invisible set.
            // ------------------------------------------------------------------
            const shadowId = id + ("shadow_" ~ bodyIndex);
            const shadowResult = opSplitBySelfShadow(context, shadowId, {
                        "bodies"        : body,
                        "viewDirection" : upDirection
                    });

            // Use the full invisibleFaces array returned by the operation.
            // qSplitBy would only return faces that were physically split by a
            // shadow curve edge; entirely-invisible faces (e.g. a plain pocket
            // floor) never get split and would be missed. invisibleFaces from
            // the result struct includes both split and unsplit invisible faces.
            const invisibleFacesQuery = qUnion(shadowResult.invisibleFaces);

            // ------------------------------------------------------------------
            // Step 4: Exclude the bottom exterior face from the deletion set.
            //
            // The bottom exterior face is the flat face that rests on the cut
            // sheet. It is invisible from above (the body self-shadows it), so
            // it appears in invisibleFacesQuery and must be explicitly preserved.
            //
            // Side walls do NOT need to be excluded: under a self-shadow analysis
            // from above, outer perimeter walls are not blocked by any other part
            // of the same body, so they land in the visible set automatically.
            // ------------------------------------------------------------------
            const bottomExteriorFaces = qFarthestAlong(
                qParallelPlanes(bodyFaces, bottomFacePlane.normal),
                bottomFacePlane.normal
            );

            // ------------------------------------------------------------------
            // Step 5: Compute the second-side face set.
            //
            //   secondSideFaces = invisible from above
            //                   minus bottom exterior face
            //
            // What remains are faces belonging to blind bottom pockets,
            // countersinks, counterbores, and similar features that require
            // a second machine setup to manufacture.
            // ------------------------------------------------------------------
            const secondSideFaces = qSubtraction(invisibleFacesQuery, bottomExteriorFaces);

            if (definition.enableDiagnostics)
            {
                // Blue:  the full invisible-from-above set (everything opSplitBySelfShadow classified as invisible)
                addDebugEntities(context, invisibleFacesQuery, DebugColor.BLUE);
                // Green: the bottom exterior face being preserved (would open the part if deleted)
                addDebugEntities(context, bottomExteriorFaces, DebugColor.GREEN);
                // Red:   faces that will be sent to opDeleteFace
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

            // ------------------------------------------------------------------
            // Step 6: Delete the second-side faces and heal the voids.
            //
            // capVoid: true  -- if the standard heal cannot close the gap,
            //                   fall back to capping the void with a flat face.
            // includeFillet: true -- pull in adjacent fillet/chamfer faces that
            //                        belong to the same feature so the heal has
            //                        clean geometry to work with.
            //
            // deleteSucceeded is set inside the try block only if opDeleteFace
            // returns without throwing, making it a reliable success indicator.
            // ------------------------------------------------------------------
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


