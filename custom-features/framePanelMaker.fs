FeatureScript 2716;
import(path : "onshape/std/common.fs", version : "2716.0");

annotation { "Feature Type Name" : "Panel Maker", "Feature Type Description" : "Creates a panel within a clear opening bounded by four solid bodies" }
export const myFeature = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Bounding Extrusions", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 4 }
        definition.bodies is Query;

        annotation { "Group Name" : "Detail Parameters" }
        {
            annotation { "Name" : "Panel Thickness", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isLength(definition.thickness, {(millimeter) : [0.1, 5.6, 100]} as LengthBoundSpec);
            
            annotation { "Name" : "Channel Depth", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isLength(definition.depth, {(millimeter) : [0.1, 9.18, 100]} as LengthBoundSpec);
    
            annotation { "Name" : "Edge Gap", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE }
            isLength(definition.gap, {(millimeter) : [0.0, 3, 100]} as LengthBoundSpec);
        }

        annotation { "Name" : "Name Dimensions in mm" }
        definition.mm is boolean;
        
        annotation { "Name" : "Name Prefix", "Default" : "Panel" }
        definition.text is string;
        
    }
    {
        var bodies = {};
        bodies.query = evaluateQuery(context, definition.bodies);

        // confirm there is 0 gap between sequential members
        
        for (var i = 0; i < 4; i += 1)
        {
            var j = i == 3 ? 0 : i+1;
            var distance = evDistance(context, {
                "side0" : bodies.query[i],
                "side1" : bodies.query[j]
            }).distance;
            
            if(distance != 0 * meter)
            {
                reportFeatureWarning(context, id, "Selections must be sequential and form a closed loop");
                return;
            }
        }
        
        // make bounding box cuboids around each frame member so we get simple faces
        
        bodies.minCorner = makeArray(4);
        bodies.maxCorner = makeArray(4);
        var cuboid = makeArray(4);
        var faces = [];
        
        for (var i = 0; i < 4; i += 1)
        {
            bodies.minCorner[i] = evBox3d(context, {
                "topology" : bodies.query[i],
                "tight" : true}).minCorner;

            bodies.maxCorner[i] = evBox3d(context, {
                "topology" : bodies.query[i],
                "tight" : true}).maxCorner;

            var cuboidName = "cuboid"~i;

            fCuboid(context, id + cuboidName, {
                "corner1" : bodies.minCorner[i],
                "corner2" : bodies.maxCorner[i]});

            var name = "cuboid"~i;

            cuboid[i] = qCreatedBy(id + name, EntityType.BODY);
            
            faces = append(faces, qCreatedBy(id + name, EntityType.FACE));
        }
        
        // calculate closest points and midpoints between opposite members, then midplanes
        
        var points = {};
        points.start = makeArray(2);
        points.end = makeArray(2);
        points.mid = makeArray(2);
        points.distance = makeArray(2);
        points.midplane = makeArray(2);
        points.axis = makeArray(2);
        
        var evd = makeArray(2);
        evd[0] = evDistance(context, {"side0" : cuboid[0],"side1" : cuboid[2]});
        evd[1] = evDistance(context, {"side0" : cuboid[1],"side1" : cuboid[3]});
        
        for (var i = 0; i < 2; i += 1)
        {
            points.start[i] = evd[i].sides[0].point;
            points.end[i] = evd[i].sides[1].point;
            points.mid[i] = (points.start[i] + points.end[i])/2;
            points.distance[i] = evd[i].distance;
            points.midplane[i] = plane(points.mid[i], normalize(points.end[i] - points.start[i]));
        }

        // find a plane of the clear opening (may not be centered)
        
        var normal = normalize(cross(points.end[0] - points.start[0], points.end[1] - points.start[1])); 
        var normalPlane = plane(points.mid[0], normal);
        
        // find faces of member 0 parallel to that plane

        var parallelFaces = evaluateQuery(context, qParallelPlanes(qCreatedBy(id + "cuboid0", EntityType.FACE), normal, true));

        // find the true central plane of the clear opening

        var frontPlane = evPlane(context, {"face" : parallelFaces[0]});
        var backPlane = evPlane(context, {"face" : parallelFaces[1]});

        //find midpoint of line between front and back faces

        var frontBackEVD = evDistance(context, {
            "side0" : parallelFaces[0],
            "side1" : parallelFaces[1]});
        var frontCenter = frontBackEVD.sides[0].point;
        var backCenter = frontBackEVD.sides[1].point;
        var center = (frontCenter + backCenter) / 2;
        
        // make the true central plane of the clear opening
        
        var centerPlane = plane(center, normal);

        // collapse points into one plane in the center of the opening

        for (var i = 0; i < 2; i += 1)
        {
            var j = i == 0 ? 1 : 0;
            
            // project endpoints and midpoints of opposite members on to the central plane of the line connecting the other opposite members
            
            points.start[i] = project(points.midplane[j], points.start[i]);
            points.end[i] = project(points.midplane[j], points.end[i]);
            points.mid[i] = project(points.midplane[j], points.mid[i]);

            // project all of those points onto the true center plane of the opening

            points.start[i] = project(centerPlane, points.start[i]);
            points.end[i] = project(centerPlane, points.end[i]);
            points.mid[i] = project(centerPlane, points.mid[i]);
            
            // make axes of the two perpendicular sets of points
            
            points.axis[i] = normalize(points.end[i] - points.start[i]);

            addDebugPoint(context, points.start[i], DebugColor.RED);
            addDebugPoint(context, points.end[i], DebugColor.RED);
            addDebugPoint(context, points.mid[i], DebugColor.RED);
        }
        
        //project opposite corner points on to the top and bottom planes of the clear opening, moving them forward and back half the panel thickness
        
        var corner0 = project(plane(points.start[0], points.axis[0]), points.start[1]) + 
            (definition.thickness / 2) * normal;
        
        var corner1 = project(plane(points.end[0], points.axis[0]), points.end[1]) - 
            (definition.thickness / 2) * normal;
        
        //use projected corner points to create the panel cuboid
        
        fCuboid(context, id + "panel", {"corner1" : corner0,"corner2" : corner1});
    
        //delete frame member cuboids
    
        var bodiesToDelete = qUnion([
            qCreatedBy(id + "cuboid0", EntityType.BODY), 
            qCreatedBy(id + "cuboid1", EntityType.BODY), 
            qCreatedBy(id + "cuboid2", EntityType.BODY), 
            qCreatedBy(id + "cuboid3", EntityType.BODY)]);

        opDeleteBodies(context, id + "deleteBodies1", {
            "entities" : bodiesToDelete});

        //get all faces of the panel

        const allFaces = qCreatedBy(id + "panel", EntityType.FACE);

        //find faces parallel to the true center plane of the opening

        const allParallelFaces = qParallelPlanes(allFaces, centerPlane, true);

        //get the side faces from the set of all faces

        const sideFaces = qSubtraction(allFaces, allParallelFaces);

        //offset the side faces the full depth of the extrusion trench, minus the specified gap

        var totalOffset = definition.depth - definition.gap;

        opOffsetFace(context, id + "offsetFace1", {
            "moveFaces" : sideFaces,
            "offsetDistance" : totalOffset});

        //calculate the dimensions of the panel

        var dim1 = roundToPrecision((((norm(points.end[0] - points.start[0])) + 2 * totalOffset) / meter), 3);
        var dim2 = roundToPrecision((((norm(points.end[1] - points.start[1])) + 2 * totalOffset) / meter), 3);
            
        //rename panel    
        
        if(definition.mm)
        {
            setProperty(context, {
                "entities" : qCreatedBy(id + "panel", EntityType.BODY),
                "propertyType" : PropertyType.NAME,
                "value" : definition.text~" - "~toString(dim1 * 1000)~"mm x "~toString(dim2 * 1000)~"mm"
            });
        }
        else
        {
            var in1 = dim1 * 1000 / 25.4;
            var name1 = toString(in1);
            var denom1 = 16;
            var in1A = floor(in1);
            var in1B = round(in1 % 1 * denom1);

            println(in1B);

            if(in1B == 0)
            {
                name1 = toString(in1A);
            }
            else
            {
                var even = in1B % 2 == 0 ? true : false;
            
                if(even)
                {
                    while(even)
                    {
                        in1B = in1B / 2;
                        denom1 = denom1 / 2;
                        if(denom1 == 1) continue;
                        even = in1B % 2 == 0 ? true : false;
                    }
                }
                
                name1 = toString(in1A)~"-"~in1B~"/"~denom1;
                
            }
            
            var in2 = dim2 * 1000 / 25.4;
            var name2 = toString(in2);
            var denom2 = 16;
            var in2A = floor(in2);
            var in2B = round(in2 % 1 * denom2);

            if(in2B == 0)
            {
                name2 = toString(in2A);
            }
            else
            {
                var even = in2B % 2 == 0 ? true : false;
                
                if(even)
                {
                    while(even)
                    {
                        in2B = in2B / 2;
                        denom2 = denom2 / 2;
                        if(denom2 == 1) continue;
                        
                        even = in2B % 2 == 0 ? true : false;
                    }
                }
                
                name2 = toString(in2A)~"-"~in2B~"/"~denom2;
            }            

            setProperty(context, {
                "entities" : qCreatedBy(id + "panel", EntityType.BODY),
                "propertyType" : PropertyType.NAME,
                "value" : definition.text~" - "~name1~" in x "~name2~" in"
            });

        }

    });
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
