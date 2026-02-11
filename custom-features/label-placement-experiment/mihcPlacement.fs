FeatureScript 2878;

// Maximum Internal Horizontal Chord (MIHC) Label Placement
// Implements the scanline-based algorithm from the markdown document
// Works with ALL face types by sampling edges (polygonal, splines, circles, arcs)

import(path : "onshape/std/common.fs", version : "2878.0");
import(path : "onshape/std/coordSystem.fs", version : "2878.0");
import(path : "onshape/std/evaluate.fs", version : "2878.0");
import(path : "onshape/std/feature.fs", version : "2878.0");
import(path : "onshape/std/mateConnector.fs", version : "2878.0");
import(path : "onshape/std/query.fs", version : "2878.0");
import(path : "onshape/std/valueBounds.fs", version : "2878.0");
import(path : "onshape/std/vector.fs", version : "2878.0");
import(path : "1470642f04a4ab4b999322bb", version : "44894d58ba1e712a741fef9d");  // Shared utilities

annotation {
    "Feature Type Name" : "MIHC Label Placement",
    "Feature Type Description" : "Places a mate connector at the widest part of a planar face using scanline analysis",
    "Feature Name Template" : "MIHC #scanlineCount"
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
            "Name" : "Scanline count",
            "Description" : "Number of horizontal scanlines (1=center, 3=recommended)",
            "Default" : 3
        }
        isInteger(definition.scanlineCount, POSITIVE_COUNT_BOUNDS);
    }
    {
        verifyNonemptyQuery(context, definition, "face", "Select a planar face");
        
        const face = definition.face;
        
        // Get tangent plane for 2D projection
        const tangentPlane = evFaceTangentPlane(context, {
            "face" : face,
            "parameter" : vector(0.5, 0.5)
        });
        
        // Sample edges to create polygon (works for all edge types)
        const edges = evaluateQuery(context, qAdjacent(face, AdjacencyType.EDGE, EntityType.EDGE));
        var polygon2D = [];
        
        for (var edge in edges)
        {
            // Sample edge at multiple points to capture curvature
            const numSamples = 4;
            var parameters = [];
            for (var i = 0; i < numSamples; i += 1)
            {
                parameters = append(parameters, i / numSamples);
            }
            
            const tangentLines = evEdgeTangentLines(context, {
                "edge" : edge,
                "parameters" : parameters,
                "arcLengthParameterization" : true
            });
            
            for (var line in tangentLines)
            {
                const point2D = project2DPoint(tangentPlane, line.origin);
                polygon2D = append(polygon2D, point2D);
            }
        }
        
        if (size(polygon2D) < 3)
        {
            throw regenError("Could not sample enough points from face edges");
        }
        
        // Compute bounding box
        const bbox = computeBoundingBox2D(polygon2D);
        
        // Generate scanlines
        var scanlineYValues = [];
        if (definition.scanlineCount == 1)
        {
            scanlineYValues = [(bbox.minY + bbox.maxY) / 2];
        }
        else if (definition.scanlineCount == 3)
        {
            const height = bbox.maxY - bbox.minY;
            scanlineYValues = [
                bbox.minY + 0.25 * height,
                bbox.minY + 0.5 * height,
                bbox.minY + 0.75 * height
            ];
        }
        else
        {
            const height = bbox.maxY - bbox.minY;
            for (var i = 1; i <= definition.scanlineCount; i += 1)
            {
                scanlineYValues = append(scanlineYValues,
                    bbox.minY + (i / (definition.scanlineCount + 1)) * height);
            }
        }
        
        // Find longest chord across all scanlines
        var bestChord = undefined;
        var bestLength = 0;
        
        for (var yValue in scanlineYValues)
        {
            const chord = findLongestHorizontalChord(polygon2D, yValue);
            if (chord != undefined && chord.length > bestLength)
            {
                bestChord = chord;
                bestLength = chord.length;
            }
        }
        
        if (bestChord == undefined)
        {
            throw regenError("Could not find valid chord in face");
        }
        
        // Midpoint of best chord
        const placement2D = vector(
            (bestChord.start[0] + bestChord.end[0]) / 2,
            (bestChord.start[1] + bestChord.end[1]) / 2
        );
        
        // Convert back to 3D
        const placement3D = unproject2DPoint(tangentPlane, placement2D);
        
        // Create coordinate system (X-axis along chord)
        const chordDir2D = normalize(bestChord.end - bestChord.start);
        const planeY = cross(tangentPlane.normal, tangentPlane.x);
        const chordDir3D = tangentPlane.x * (chordDir2D[0] * meter) + planeY * (chordDir2D[1] * meter);
        
        const placementCsys = coordSystem(placement3D, chordDir3D, tangentPlane.normal);
        
        opMateConnector(context, id + "mateConnector", {
            "coordSystem" : placementCsys,
            "owner" : qNothing()
        });
    });

// Find longest horizontal chord at given Y value
function findLongestHorizontalChord(polygon is array, yValue is number) returns map
{
    var intersections = [];
    const edges = getPolygonEdges(polygon);
    
    for (var edge in edges)
    {
        const p1 = edge.p1;
        const p2 = edge.p2;
        const y1 = p1[1];
        const y2 = p2[1];
        
        if ((y1 <= yValue && yValue <= y2) || (y2 <= yValue && yValue <= y1))
        {
            if (abs(y1 - y2) < TOLERANCE.zeroLength)
            {
                continue;
            }
            
            const t = (yValue - y1) / (y2 - y1);
            const xIntersection = p1[0] + t * (p2[0] - p1[0]);
            intersections = append(intersections, xIntersection);
        }
    }
    
    if (size(intersections) < 2)
    {
        return undefined;
    }
    
    intersections = sort(intersections, function(a, b) { return a - b; });
    
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
