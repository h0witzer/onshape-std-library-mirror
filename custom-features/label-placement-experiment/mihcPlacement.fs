FeatureScript 2878;

// Maximum Internal Horizontal Chord (MIHC) Label Placement
// Implements the deterministic scanline-based label placement strategy
// described in "Planar Face Label Placement Strategies.md"
// Places a mate connector at the computed optimal point on a planar face

import(path : "onshape/std/common.fs", version : "2878.0");
import(path : "onshape/std/coordSystem.fs", version : "2878.0");
import(path : "onshape/std/evaluate.fs", version : "2878.0");
import(path : "onshape/std/feature.fs", version : "2878.0");
import(path : "onshape/std/mateConnector.fs", version : "2878.0");
import(path : "onshape/std/query.fs", version : "2878.0");
import(path : "onshape/std/valueBounds.fs", version : "2878.0");
import(path : "onshape/std/vector.fs", version : "2878.0");
import(path : "labelPlacementUtils.fs", version : "");  // Shared utilities

annotation {
    "Feature Type Name" : "MIHC Label Placement",
    "Feature Type Description" : "Places a mate connector at the optimal label position on a planar face using Maximum Internal Horizontal Chord algorithm",
    "Feature Name Template" : "MIHC #scanlineCount scanline(s)"
}
export const mihcPlacement = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation {
            "Name" : "Planar face",
            "Filter" : EntityType.FACE && GeometryType.PLANE,
            "MaxNumberOfPicks" : 1
        }
        definition.face is Query;
        
        annotation {
            "Name" : "Number of scanlines",
            "Description" : "1 scanline = center only, 3 scanlines = 25%, 50%, 75% heights for robustness",
            "Default" : 3
        }
        isInteger(definition.scanlineCount, POSITIVE_COUNT_BOUNDS);
    }
    {
        // Verify face is selected
        verifyNonemptyQuery(context, definition, "face", "Select a planar face");
        
        const face = definition.face;
        
        // Get the tangent plane for 2D projection
        const tangentPlane = evFaceTangentPlane(context, {
            "face" : face,
            "parameter" : vector(0.5, 0.5)
        });
        
        // Get all vertices of the face boundary
        const vertices = evaluateQuery(context, qAdjacent(face, AdjacencyType.VERTEX, EntityType.VERTEX));
        
        if (size(vertices) < 3)
        {
            throw regenError("Face must have at least 3 vertices");
        }
        
        // Sort vertices into a contiguous loop and project to 2D
        const polygon2D = sortPolygonVertices(context, face, vertices, tangentPlane);
        
        // Compute bounding box
        const boundingBox = computeBoundingBox2D(polygon2D);
        
        // Generate scanlines based on count
        var scanlineYValues = [];
        if (definition.scanlineCount == 1)
        {
            scanlineYValues = [(boundingBox.minY + boundingBox.maxY) / 2];
        }
        else if (definition.scanlineCount == 3)
        {
            const height = boundingBox.maxY - boundingBox.minY;
            scanlineYValues = [
                boundingBox.minY + 0.25 * height,
                boundingBox.minY + 0.5 * height,
                boundingBox.minY + 0.75 * height
            ];
        }
        else
        {
            // Support arbitrary scanline counts - evenly distribute
            const height = boundingBox.maxY - boundingBox.minY;
            for (var i = 1; i <= definition.scanlineCount; i += 1)
            {
                scanlineYValues = append(scanlineYValues, 
                    boundingBox.minY + (i / (definition.scanlineCount + 1)) * height);
            }
        }
        
        // Find the longest chord across all scanlines
        var bestChord = undefined;
        var bestChordLength = 0;
        
        for (var scanlineY in scanlineYValues)
        {
            const chord = findLongestHorizontalChord(polygon2D, scanlineY);
            if (chord != undefined && chord.length > bestChordLength)
            {
                bestChord = chord;
                bestChordLength = chord.length;
            }
        }
        
        if (bestChord == undefined)
        {
            throw regenError("Could not find valid placement point. Face may be degenerate.");
        }
        
        // Midpoint of the best chord is the placement location
        const placement2D = vector(
            (bestChord.start[0] + bestChord.end[0]) / 2,
            (bestChord.start[1] + bestChord.end[1]) / 2
        );
        
        // Convert back to 3D
        const placement3D = unproject2DPoint(tangentPlane, placement2D);
        
        // Create coordinate system for mate connector
        // Z-axis is face normal, X-axis aligned with chord direction
        const chordDirection2D = normalize(bestChord.end - bestChord.start);
        const chordDirection3D = tangentPlane.x * chordDirection2D[0] + tangentPlane.y * chordDirection2D[1];
        
        const placementCsys = coordSystem(placement3D, chordDirection3D, tangentPlane.normal);
        
        // Create the mate connector
        opMateConnector(context, id + "mateConnector", {
            "coordSystem" : placementCsys,
            "owner" : qNothing()
        });
    });

// Find the longest horizontal chord at a given Y value
function findLongestHorizontalChord(polygon is array, yValue is number) returns map
{
    // Find all intersections of the scanline with polygon edges
    var intersections = [];
    const edges = getPolygonEdges(polygon);
    
    for (var edge in edges)
    {
        const p1 = edge.p1;
        const p2 = edge.p2;
        
        const y1 = p1[1];
        const y2 = p2[1];
        
        // Check if edge crosses the scanline
        if ((y1 <= yValue && yValue <= y2) || (y2 <= yValue && yValue <= y1))
        {
            // Skip if edge is horizontal and on the scanline (degenerate case)
            // Use dimensionless tolerance since we're in 2D projected space
            if (abs(y1 - y2) < TOLERANCE.zeroLength)
            {
                continue;
            }
            
            // Calculate intersection X coordinate
            const t = (yValue - y1) / (y2 - y1);
            const xIntersection = p1[0] + t * (p2[0] - p1[0]);
            intersections = append(intersections, xIntersection);
        }
    }
    
    if (size(intersections) < 2)
    {
        return undefined;
    }
    
    // Sort intersections in ascending order
    intersections = sort(intersections, function(a, b) { return a - b; });
    
    // Find longest internal segment
    // Due to Jordan Curve Theorem, segments [x0, x1], [x2, x3], ... are interior
    var longestChord = undefined;
    var longestLength = 0;
    
    for (var i = 0; i < size(intersections) - 1; i += 2)
    {
        const x1 = intersections[i];
        const x2 = intersections[i + 1];
        const length = abs(x2 - x1);
        
        if (length > longestLength)
        {
            longestLength = length;
            longestChord = {
                "start" : vector(x1, yValue),
                "end" : vector(x2, yValue),
                "length" : length
            };
        }
    }
    
    return longestChord;
}
