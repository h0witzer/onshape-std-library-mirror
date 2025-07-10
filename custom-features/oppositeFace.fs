FeatureScript 2641;
import(path : "onshape/std/common.fs", version : "2641.0");
//import(path : "onshape/std/evaluate.fs", version : "2615.0"); // Needed for evDistance
/**
 * @Name Find Closest Opposite Face (Behind Only)
 * @Description Finds the face on the same body that is opposite to the selected face
 * AND located behind it relative to the selected face's normal.
 * Assumes a thin part where "opposite" means roughly parallel with an opposing normal.
 * If multiple opposite faces are found behind the selected face, returns the one closest.
 *
 * @param context The current context.
 * @param definition Map containing the definition parameters.
 * @field selectedFace {Query} : A query that must resolve to exactly ONE face.
 * @field id {Id} : The feature ID for reporting errors/warnings.
 *
 * @returns {Query} A query containing the single closest opposite face behind the selection, or qNothing() if none found.
 */
export function findOppositeFace(context is Context, definition is map) returns Query
{
    // --- Validate Input ---
    if (!(definition.selectedFace is Query))
    {
        throw regenError("Input 'selectedFace' must be a Query.");
    }
    const selectedFaces = evaluateQuery(context, definition.selectedFace);
    if (size(selectedFaces) != 1)
    {
        reportFeatureWarning(context, definition.id, "Please select exactly one face.");
        return qNothing();
    }
    const selectedFace = selectedFaces[0];

    // --- Get Data from Selected Face ---
    const ownerBody = qOwnerBody(selectedFace);
    if (isQueryEmpty(context, ownerBody))
    {
        reportFeatureWarning(context, definition.id, "Could not find owner body for the selected face.");
        return qNothing();
    }

    var selectedPlaneData = try silent(evFaceTangentPlane(context, {
            "face" : selectedFace,
            "parameter" : vector(0.5, 0.5)
        }));
    if (selectedPlaneData == undefined)
    {
        reportFeatureWarning(context, definition.id, "Could not evaluate tangent plane for the selected face.");
        return qNothing();
    }
    const selectedNormal = normalize(selectedPlaneData.normal);
    const selectedPoint = selectedPlaneData.origin;

    // --- Find and Filter Candidate Faces ---
    const allBodyFaces = qOwnedByBody(ownerBody, EntityType.FACE);
    const candidateFacesQuery = qSubtraction(allBodyFaces, selectedFace);
    const candidateFaces = evaluateQuery(context, candidateFacesQuery);

    // --- Track Closest Face ---
    var closestOppositeFace = undefined; // Will store the Query of the closest face
    // Initialize with a very large ValueWithUnits. 1,000,000 meters is effectively infinite for CAD models.
    var minDistance is ValueWithUnits = 1000000 * meter;

    const DOT_PRODUCT_TOLERANCE = -0.99; // Tolerance for anti-parallel check

    // --- Loop Through Candidates ---
    for (var candidateFace in candidateFaces)
    {
        var candidatePlaneData = try silent(evFaceTangentPlane(context, {
                "face" : candidateFace,
                "parameter" : vector(0.5, 0.5)
            }));
        if (candidatePlaneData == undefined)
        {
            continue; // Skip if tangent plane fails
        }
        const candidateNormal = normalize(candidatePlaneData.normal);
        const candidatePoint = candidatePlaneData.origin;

        // Check 1: Are the normals approximately anti-parallel?
        if (dot(selectedNormal, candidateNormal) < DOT_PRODUCT_TOLERANCE)
        {
            // Check 2: Is the candidate face "behind" the selected face?
            const directionVector = candidatePoint - selectedPoint;
            // Use a small negative tolerance to handle floating point noise near zero
           if (dot(directionVector, selectedNormal) < (-TOLERANCE.zeroLength * meter))
            {
                // Check 3: Calculate the distance between the selected face and this candidate
                var distanceResult = try silent(evDistance(context, {
                        "side0" : selectedFace,
                        "side1" : candidateFace
                    }));

                // Check 4: If distance valid and closer than current minimum?
                // This comparison should now be valid (ValueWithUnits < ValueWithUnits)
                if (distanceResult != undefined && distanceResult.distance < minDistance)
                {
                    minDistance = distanceResult.distance;
                    closestOppositeFace = candidateFace; // Store the query itself
                }
            }
        }
    }

    // --- Return Result ---
    return closestOppositeFace == undefined ? qNothing() : closestOppositeFace;
}
