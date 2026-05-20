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
        3. Find the top exterior face as the planar face farthest in the upward
           direction (opposite to the bottom face outward normal) using
           qParallelPlanes + qFarthestAlong.
        4. Flood-fill concavely from the bottom exterior face (qConcaveConnectedFaces)
           to capture all features that open into the bottom: pockets, countersinks,
           counterbores, bottom-side chamfers, etc.
        5. Flood-fill concavely from the top exterior face the same way to identify
           features accessible from the top, including through-holes and top-side
           pockets.
        6. Subtract the top-accessible zone from the bottom zone. What remains are
           faces reachable only from the bottom -- the second-side features.
        7. Delete those faces with opDeleteFace (heal mode), per-body, with error
           isolation so one failing body does not abort the rest.

    Face orientation:
        Auto Layout orients each part so that the largest planar face lies face-down
        on the cut sheet. That face is therefore the BOTTOM exterior face. Its
        outward normal (as returned by evPlane) points away from the solid toward
        the machine table -- i.e. downward. The machining tool approaches from
        the opposite direction, which is the upward direction (-bottomFaceNormal).

    Known limitation:
        Through-counterbores (a large-diameter bore on the bottom with a smaller
        through-bore continuing to the top) may not be fully removed. Because the
        smaller through-bore is concavely connected to both the top and the bottom
        faces, the flood-fill from the top reaches the counterbore shoulder and
        large bore through the through-bore, causing those faces to appear in the
        top-accessible zone and survive the subtraction. Blind counterbores
        (not reaching the top face) are handled correctly.
*/

FeatureScript 2909;
import(path : "onshape/std/geometry.fs", version : "2909.0");

// ---------------------------------------------------------------------------
// AutoLayout attribute type
// This mirrors the definition in autoLayoutTypes.fs so that this feature can
// locate bodies placed by Auto Layout+ without requiring a direct import of
// that file's Onshape document path. The attribute value and type name must
// match exactly what Auto Layout+ sets via setAttribute.
// ---------------------------------------------------------------------------

export type AutoLayoutAttribute typecheck canBeAutoLayoutAttribute;

export predicate canBeAutoLayoutAttribute(value)
{
    value is string;
    value == "AutoLayout_PLACED";
}

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
        const placedBodies = qAttributeQuery("AutoLayout_PLACED" as AutoLayoutAttribute);

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

            // All faces owned by this body.
            const bodyFaces = qOwnedByBody(body, EntityType.FACE);

            // ------------------------------------------------------------------
            // Step 3: Identify the exterior bottom and top faces.
            //
            // qParallelPlanes returns every planar face whose normal is parallel
            // (or anti-parallel) to the bottom-face normal. For a flat plate these
            // are the bottom and top exterior faces, plus any horizontal interior
            // faces (pocket floors, counterbore shoulders, etc.).
            //
            // qFarthestAlong then picks the face(s) at each extreme:
            //   - farthest in the downward direction (bottomFacePlane.normal) = the
            //     bottom exterior face resting on the cut sheet.
            //   - farthest in the upward direction (upDirection) = the top exterior
            //     face that the machining tool operates on.
            // ------------------------------------------------------------------
            const horizontalFaces = qParallelPlanes(bodyFaces, bottomFacePlane.normal);

            const bottomExteriorFaces = qFarthestAlong(horizontalFaces, bottomFacePlane.normal);
            const topExteriorFaces    = qFarthestAlong(horizontalFaces, upDirection);

            // ------------------------------------------------------------------
            // Step 4: Flood-fill concavely from the bottom exterior face.
            //
            // qConcaveConnectedFaces follows concave edges outward from the seed,
            // capturing all faces that form features opening into the bottom face:
            // pocket walls, pocket floors, countersink cones, counterbore
            // cylinders, counterbore shoulders, chamfers on bottom-side pockets, etc.
            // ------------------------------------------------------------------
            const bottomConcaveZone = qConcaveConnectedFaces(bottomExteriorFaces);

            // ------------------------------------------------------------------
            // Step 5: Flood-fill concavely from the top exterior face.
            //
            // This captures every feature accessible from the top: top-side
            // pockets, through-holes, and -- via the through-bore path -- the
            // interior faces of through-counterbores. Subtracting this zone from
            // the bottom zone excludes through-holes and any geometry that is
            // reachable from above, leaving only purely bottom-side features.
            // ------------------------------------------------------------------
            const topConcaveZone = qConcaveConnectedFaces(topExteriorFaces);

            // ------------------------------------------------------------------
            // Step 6: Compute the second-side face set.
            //
            // Second-side faces = bottom concave zone
            //                   minus top concave zone      (through/top features)
            //                   minus bottom exterior faces  (the flat underside -- keep it)
            //                   minus top exterior faces     (the cut face -- keep it)
            //
            // What remains are faces belonging to features that can only be
            // reached by approaching from below.
            // ------------------------------------------------------------------
            const exteriorFaces = qUnion([bottomExteriorFaces, topExteriorFaces]);

            const secondSideFaces = qSubtraction(
                qSubtraction(bottomConcaveZone, topConcaveZone),
                exteriorFaces
            );

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
            // Step 7: Delete the second-side faces and heal the voids.
            //
            // capVoid: true  -- if the standard heal cannot close the gap,
            //                   fall back to capping the void with a flat face.
            // includeFillet: true -- pull in any adjacent fillet/chamfer faces
            //                       that belong to the same feature, so the heal
            //                       has clean geometry to work with.
            //
            // deleteSucceeded is set inside the try block only if opDeleteFace
            // returns without throwing, making it a reliable success indicator.
            // An untouched value of false after the try block means the operation
            // failed; we report a warning so the user knows which body needs
            // manual attention regardless of the diagnostics toggle.
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


