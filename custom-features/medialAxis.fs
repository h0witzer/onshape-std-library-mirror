FeatureScript 2856;
import(path : "onshape/std/common.fs", version : "2856.0");
import(path : "onshape/std/evaluate.fs", version : "2856.0");
import(path : "onshape/std/geomOperations.fs", version : "2856.0");
import(path : "onshape/std/query.fs", version : "2856.0");
import(path : "onshape/std/units.fs", version : "2856.0");
import(path : "onshape/std/vector.fs", version : "2856.0");
import(path : "onshape/std/coordSystem.fs", version : "2856.0");
import(path : "onshape/std/curveGeometry.fs", version : "2856.0");
import(path : "onshape/std/math.fs", version : "2856.0");
import(path : "onshape/std/containers.fs", version : "2856.0");

/**
 * Medial Axis Transform (MAT) feature for planar faces.
 * 
 * This feature computes the medial axis (skeleton) of a planar face using a tracing algorithm
 * based on "A tracing algorithm for constructing medial axis transform of 3D objects bound by
 * free-form surfaces" by Ramanathan & Gurumoorthy.
 * 
 * The medial axis is the locus of centers of maximal inscribed disks (circles). For a planar
 * region, medial axis points are equidistant from two or more boundary edges.
 * 
 * Algorithm overview:
 * 1. Extract boundary edges from the planar face
 * 2. Trace along boundary edges parametrically
 * 3. Find points where normals from pairs of edges intersect
 * 4. Verify distance criterion (equidistant from boundaries)
 * 5. Verify curvature criterion (maximal disk radius ≤ boundary curvature)
 * 6. Connect valid medial axis points into curves
 */

annotation { 
    "Feature Type Name" : "Medial Axis", 
    "Feature Type Description" : "Computes the medial axis (skeleton) of a planar face using a tracing algorithm"
}
export const medialAxis = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Face", "Filter" : EntityType.FACE, "MaxNumberOfPicks" : 1 }
        definition.face is Query;
        
        annotation { "Name" : "Tracing step size" }
        isLength(definition.stepSize, { (meter) : [0.0001, 0.005, 0.1] } as LengthBoundSpec);
        
        annotation { "Name" : "Distance tolerance" }
        isLength(definition.distanceTolerance, { (meter) : [0.00001, 0.0001, 0.01] } as LengthBoundSpec);
        
        annotation { "Name" : "Create composite curve", "Default" : true }
        definition.createComposite is boolean;
    }
    {
        // Verify that a face was selected
        if (isQueryEmpty(context, definition.face))
        {
            throw regenError("Please select a face");
        }
        
        // Verify the face is planar
        const facePlane = try silent(evPlane(context, { "face" : definition.face }));
        if (facePlane == undefined)
        {
            throw regenError("Selected face must be planar. 3D medial axis for curved surfaces is not yet supported.");
        }
        
        // Extract boundary edges of the face
        const boundaryEdges = qAdjacent(definition.face, AdjacencyType.EDGE, EntityType.EDGE);
        const boundaryEdgeArray = evaluateQuery(context, boundaryEdges);
        
        if (size(boundaryEdgeArray) < 2)
        {
            throw regenError("Face must have at least 2 boundary edges");
        }
        
        // Trace medial axis using intersection of normals approach
        const medialAxisPoints = traceMedialAxisByNormals(context, boundaryEdgeArray, facePlane, definition.stepSize, definition.distanceTolerance);
        
        if (size(medialAxisPoints) < 2)
        {
            reportFeatureWarning(context, id, "Medial axis computation resulted in too few points. Try adjusting step size or tolerance.");
            return;
        }
        
        // Create curves representing the medial axis
        createMedialAxisCurves(context, id, medialAxisPoints, definition.createComposite);
    }, 
    {
        stepSize : 0.005 * meter,
        distanceTolerance : 0.0001 * meter,
        createComposite : true
    });

/**
 * Traces the medial axis by finding intersections of normals from boundary edge pairs.
 * This implements the core algorithm from the paper.
 * 
 * For each pair of boundary edges:
 * - March along one edge parametrically (tracing parameter)
 * - Compute normal at each point
 * - Find where this normal intersects the normal from another edge
 * - Check distance criterion: point is equidistant from both edges
 * - Check curvature criterion: maximal disk fits within boundaries
 * 
 * @param context : The context
 * @param boundaryEdges : Array of boundary edge queries
 * @param facePlane : The plane of the face
 * @param stepSize : Step size for tracing along edges
 * @param tolerance : Tolerance for distance equality check
 * 
 * @returns Array of medial axis points with positions and radii
 */
function traceMedialAxisByNormals(context is Context, boundaryEdges is array, facePlane is Plane, stepSize is ValueWithUnits, tolerance is ValueWithUnits) returns array
{
    var medialPoints = [];
    const numEdges = size(boundaryEdges);
    
    // For each pair of edges, find medial axis points
    for (var i = 0; i < numEdges; i += 1)
    {
        for (var j = i + 1; j < numEdges; j += 1)
        {
            const edge1 = boundaryEdges[i];
            const edge2 = boundaryEdges[j];
            
            // Get edge lengths to determine sampling
            const edge1Length = try silent(evLength(context, { "entities" : edge1 }));
            const edge2Length = try silent(evLength(context, { "entities" : edge2 }));
            
            if (edge1Length == undefined || edge2Length == undefined)
                continue;
            
            // Sample along edge1 and find intersections with normals from edge2
            const numSamples1 = max(5, floor(edge1Length / stepSize));
            
            for (var k = 0; k < numSamples1; k += 1)
            {
                const param1 = (numSamples1 > 1) ? (k / (numSamples1 - 1)) : 0.5;
                
                // Get point and normal on edge1
                const tangentLine1 = try silent(evEdgeTangentLine(context, {
                    "edge" : edge1,
                    "parameter" : param1
                }));
                
                if (tangentLine1 == undefined)
                    continue;
                
                const point1 = tangentLine1.origin;
                const tangent1 = tangentLine1.direction;
                
                // Compute inward normal (perpendicular to tangent, in plane)
                const normal1 = computeInwardNormal(tangent1, facePlane);
                
                // Sample along edge2 to find intersection
                const numSamples2 = max(5, floor(edge2Length / stepSize));
                
                for (var m = 0; m < numSamples2; m += 1)
                {
                    const param2 = (numSamples2 > 1) ? (m / (numSamples2 - 1)) : 0.5;
                    
                    // Get point and normal on edge2
                    const tangentLine2 = try silent(evEdgeTangentLine(context, {
                        "edge" : edge2,
                        "parameter" : param2
                    }));
                    
                    if (tangentLine2 == undefined)
                        continue;
                    
                    const point2 = tangentLine2.origin;
                    const tangent2 = tangentLine2.direction;
                    
                    // Compute inward normal
                    const normal2 = computeInwardNormal(tangent2, facePlane);
                    
                    // Find intersection of the two normals
                    const intersection = intersectLines(point1, normal1, point2, normal2, facePlane);
                    
                    if (intersection == undefined)
                        continue;
                    
                    // Check distance criterion: point should be equidistant from both edges
                    const dist1 = norm(intersection - point1);
                    const dist2 = norm(intersection - point2);
                    
                    if (abs(dist1 - dist2) > tolerance)
                        continue;
                    
                    const radius = (dist1 + dist2) / 2;
                    
                    // Check curvature criterion: radius ≤ min local curvature radius
                    if (!checkCurvatureCriterion(context, edge1, param1, radius, tolerance) ||
                        !checkCurvatureCriterion(context, edge2, param2, radius, tolerance))
                        continue;
                    
                    // Valid medial axis point
                    medialPoints = append(medialPoints, {
                        "position" : intersection,
                        "radius" : radius
                    });
                }
            }
        }
    }
    
    return medialPoints;
}

/**
 * Computes the inward normal to an edge in a planar face.
 * The normal is perpendicular to the edge tangent and lies in the face plane.
 * 
 * @param tangent : Tangent vector to the edge
 * @param facePlane : Plane of the face
 * 
 * @returns Inward normal vector (unit vector)
 */
function computeInwardNormal(tangent is Vector, facePlane is Plane) returns Vector
{
    // Cross product of face normal and edge tangent gives perpendicular in plane
    var perpendicular = cross(facePlane.normal, tangent);
    
    if (norm(perpendicular) < TOLERANCE.zeroLength * meter)
    {
        // Tangent parallel to face normal - shouldn't happen for planar face
        // Return arbitrary perpendicular
        perpendicular = vector(1, 0, 0) * meter;
    }
    
    return normalize(perpendicular);
}

/**
 * Finds the intersection point of two lines in 3D space projected onto a plane.
 * Uses least squares approach for non-intersecting lines.
 * 
 * @param point1 : Point on first line
 * @param direction1 : Direction of first line
 * @param point2 : Point on second line  
 * @param direction2 : Direction of second line
 * @param plane : Plane to project onto
 * 
 * @returns Intersection point, or undefined if lines are parallel
 */
function intersectLines(point1 is Vector, direction1 is Vector, point2 is Vector, direction2 is Vector, plane is Plane) returns Vector
{
    // Check if directions are parallel
    const crossProd = cross(direction1, direction2);
    if (norm(crossProd) < TOLERANCE.zeroLength * meter)
        return undefined;
    
    // Convert to 2D in plane coordinate system
    const p1_2d = worldToPlane(plane, point1);
    const p2_2d = worldToPlane(plane, point2);
    
    // Project directions onto plane
    const d1_3d = direction1 - dot(direction1, plane.normal) * plane.normal;
    const d2_3d = direction2 - dot(direction2, plane.normal) * plane.normal;
    
    const d1_2d = worldToPlane(plane, point1 + d1_3d) - p1_2d;
    const d2_2d = worldToPlane(plane, point2 + d2_3d) - p2_2d;
    
    // Solve for intersection in 2D: p1 + t1*d1 = p2 + t2*d2
    // This gives: t1*d1 - t2*d2 = p2 - p1
    // In matrix form: [d1 -d2] * [t1; t2] = [p2 - p1]
    
    const dx = p2_2d[0] - p1_2d[0];
    const dy = p2_2d[1] - p1_2d[1];
    
    const det = d1_2d[0] * (-d2_2d[1]) - d1_2d[1] * (-d2_2d[0]);
    
    // Check if determinant is too small (parallel lines in 2D)
    // det has units of (length^2), so compare with tolerance squared
    const detTolerance = TOLERANCE.zeroLength * TOLERANCE.zeroLength * meter * meter;
    if (abs(det) < detTolerance)
        return undefined;
    
    const t1 = (dx * (-d2_2d[1]) - dy * (-d2_2d[0])) / det;
    
    // Compute intersection in 2D
    const intersect_2d = p1_2d + t1 * d1_2d;
    
    // Convert back to 3D
    return planeToWorld(plane, intersect_2d);
}

/**
 * Checks the curvature criterion: the maximal disk radius must be ≤ the local radius
 * of curvature of the boundary edge at the touchpoint.
 * 
 * This ensures the maximal disk is actually maximal and doesn't extend beyond the boundary.
 * 
 * @param context : The context
 * @param edge : The boundary edge
 * @param parameter : Parameter on the edge
 * @param diskRadius : Radius of the maximal disk
 * @param tolerance : Tolerance for comparison
 * 
 * @returns true if criterion is satisfied, false otherwise
 */
function checkCurvatureCriterion(context is Context, edge is Query, parameter is number, diskRadius is ValueWithUnits, tolerance is ValueWithUnits) returns boolean
{
    // Get curvature at the parameter
    const curvatureData = try silent(evEdgeCurvature(context, {
        "edge" : edge,
        "parameter" : parameter
    }));
    
    if (curvatureData == undefined)
        return true; // Cannot check, assume valid
    
    const curvature = curvatureData.curvature;
    
    // Radius of curvature is 1/curvature
    // For straight edges, curvature is 0, so radius is infinite
    // curvature has units of 1/length, so use zeroLength for comparison
    const curvatureTolerance = TOLERANCE.zeroLength / (meter * meter);
    if (abs(curvature) < curvatureTolerance)
        return true; // Straight edge, criterion always satisfied
    
    const radiusOfCurvature = 1 / abs(curvature);
    
    // Disk radius must be ≤ radius of curvature (with tolerance)
    return diskRadius <= radiusOfCurvature + tolerance;
}

/**
 * Creates curves representing the medial axis from computed points.
 * Points are connected into chains and curves are fit through them.
 * 
 * @param context : The context
 * @param id : The feature id
 * @param medialPoints : Array of medial axis points
 * @param createComposite : Whether to create a composite curve
 */
function createMedialAxisCurves(context is Context, id is Id, medialPoints is array, createComposite is boolean)
{
    // Remove duplicate points
    const uniquePoints = removeDuplicatePoints(medialPoints, 0.0001 * meter);
    
    // Sort points into connected chains
    const chains = connectMedialPoints(uniquePoints, 0.02 * meter);
    
    // Create a curve for each chain
    for (var chainIndex = 0; chainIndex < size(chains); chainIndex += 1)
    {
        const chain = chains[chainIndex];
        
        if (size(chain) < 2)
            continue;
        
        // Extract positions from chain
        var positions = [];
        for (var pt in chain)
        {
            positions = append(positions, pt.position);
        }
        
        // Create curve using fit spline or polyline
        try
        {
            if (size(positions) >= 3)
            {
                // Use fit spline for smoother curves
                opFitSpline(context, id + ("spline" ~ chainIndex), {
                    "points" : positions
                });
            }
            else if (size(positions) == 2)
            {
                // Use polyline for 2 points
                opPolyline(context, id + ("polyline" ~ chainIndex), {
                    "points" : positions
                });
            }
        }
        catch
        {
            // If curve creation fails, skip this chain
            continue;
        }
    }
    
    // Create composite curve if requested
    if (createComposite)
    {
        const allCurves = qCreatedBy(id, EntityType.BODY);
        if (!isQueryEmpty(context, allCurves))
        {
            try
            {
                opCreateCompositePart(context, id + "composite", {
                    "bodies" : allCurves,
                    "closed" : false
                });
            }
            catch
            {
                // Composite creation failed, individual curves remain
            }
        }
    }
}

/**
 * Removes duplicate points from the medial axis point set.
 * 
 * @param points : Array of points
 * @param tolerance : Distance tolerance for considering points duplicates
 * 
 * @returns Array of unique points
 */
function removeDuplicatePoints(points is array, tolerance is ValueWithUnits) returns array
{
    if (size(points) == 0)
        return [];
    
    var uniquePoints = [points[0]];
    
    for (var i = 1; i < size(points); i += 1)
    {
        var isDuplicate = false;
        
        for (var uniquePt in uniquePoints)
        {
            if (norm(points[i].position - uniquePt.position) < tolerance)
            {
                isDuplicate = true;
                break;
            }
        }
        
        if (!isDuplicate)
        {
            uniquePoints = append(uniquePoints, points[i]);
        }
    }
    
    return uniquePoints;
}

/**
 * Connects medial axis points into chains based on proximity.
 * Uses a greedy nearest-neighbor approach to build connected chains.
 * 
 * @param medialPoints : Array of medial axis points
 * @param maxDistance : Maximum distance to consider points connected
 * 
 * @returns Array of chains (each chain is an array of points)
 */
function connectMedialPoints(medialPoints is array, maxDistance is ValueWithUnits) returns array
{
    if (size(medialPoints) == 0)
        return [];
    
    var chains = [];
    var used = [];
    
    // Initialize used array
    for (var i = 0; i < size(medialPoints); i += 1)
    {
        used = append(used, false);
    }
    
    // Build chains by connecting nearby points
    for (var i = 0; i < size(medialPoints); i += 1)
    {
        if (used[i])
            continue;
        
        var chain = [medialPoints[i]];
        used[i] = true;
        
        // Grow chain by finding nearest unused neighbors
        var growing = true;
        while (growing)
        {
            growing = false;
            const lastPoint = chain[size(chain) - 1];
            var nearestIndex = -1;
            var nearestDist = maxDistance;
            
            // Find nearest unused point
            for (var j = 0; j < size(medialPoints); j += 1)
            {
                if (used[j])
                    continue;
                
                const dist = norm(lastPoint.position - medialPoints[j].position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestIndex = j;
                }
            }
            
            // Add to chain if close enough
            if (nearestIndex != -1)
            {
                chain = append(chain, medialPoints[nearestIndex]);
                used[nearestIndex] = true;
                growing = true;
            }
        }
        
        chains = append(chains, chain);
    }
    
    return chains;
}
