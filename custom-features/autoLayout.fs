/*    
    Auto Layout
    
    Automatically lays out planar parts for machining using a binary tree packing method.
    
            1.0     - May  6, 2016 - Marena Richardson - Initial Demo Version published to forums.
            2.0     - Mar 20, 2018 - Arul Suresh       - Updated to work in-context, multiple features.
            2.0.1   - Mar 20, 2018 - Arul Suresh       - Removed blank feature definition.
            2.1     - Mar 22, 2018 - Arul Suresh       - Added info sheet and posted on OS forum.
            2.1.1   - Mar 22, 2018 - Arul Suresh       - Fixed bug with more than two Auto Layout features in one Part Studio.
            2.2     - May 15, 2018 - Arul Suresh       - Fixed bug with finding rotated placements.
            2.2.1   - May  4, 2020 - Arul Suresh       - Removed extraneous input item.
            3.0     - May  8, 2020 - Arul Suresh       - Update to latest FS release.
                                                       - Improvement: option to set orientation of parts when using feature to lay out routed parts.
                                                       - Fixed bug which caused part spacing violation in rare cases. 
            3.1     - May  8, 2020 - Arul Suresh       - Changed part orientation to ease usage for routed parts.
            3.1.1   - Dec  8, 2023 - Arul Suresh       - Add feature description to publish feature.
            4.0     - Apr 11, 2025 - Derek Van Allen   - Removed thickness input, nest per material and thickess support added, composite resulting groups.
*/ 

FeatureScript 2625;
import(path : "onshape/std/geometry.fs", version : "2625.0");
import(path : "aa8ee374e7061289b937b984", version : "b0af54cc89dae3c240e344e8"); //autoLayoutConfig.fs
import(path : "bb79595d1ad4e6528fb60762", version : "20987b283a5fd1abb9b2d6f5"); //autoLayoutTypes.fs
import(path : "f4e7238da5afaf5a3f1498c0/7a207cd9ceffd98f8f03ad47/22d17eb94c85900576fbf53e", version : "d8911b6f752a07bc27cfc8dc");


annotation { "Feature Type Name" : "Auto Layout+",
        "Feature Type Description" : "Nests parts for 2D fabrication.<br>" ~
        "Implements a rectangular bin-packing heuristic algorithm, and " ~
        "saves nesting state per-part so that multiple calls of this feature " ~
        "can be used to nest parts of different thicknesses.<br><br>" ~
        "This feature assumes that the parts <b>are planar</b> " ~
        "and that the <b>largest face determines the cutting plane</b>.",
        "Editing Logic Function" : "editLogic" }
export const autolayout = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {

        annotation { "Name" : "Cut sheet width", "UIHint" : "REMEMBER_PREVIOUS_VALUE" }
        isLength(definition.width, DEFAULT_SHEET_WIDTH);

        annotation { "Name" : "Cut sheet length", "UIHint" : "REMEMBER_PREVIOUS_VALUE" }
        isLength(definition.length, DEFAULT_SHEET_LENGTH);

        annotation { "Name" : "Spacing", "UIHint" : "REMEMBER_PREVIOUS_VALUE" }
        isLength(definition.spacing, DEFAULT_SPACING);

        annotation { "Name" : "Multiple copies", "UIHint" : "DISPLAY_SHORT" }
        definition.copies is boolean;

        if (definition.copies)
        {
            annotation { "Name" : "Number of copies", "UIHint" : "DISPLAY_SHORT" }
            isInteger(definition.N, POSITIVE_COUNT_BOUNDS);
        }

        annotation { "Name" : "Assign oriented faces", "UIHint" : "DISPLAY_SHORT" }
        definition.orientFaces is boolean;

        if (definition.orientFaces)
        {
            annotation { "Name" : "Faces to orient", "Filter" : EntityType.FACE }
            definition.orientedFaces is Query;
        }

        annotation { "Name" : "Set rotation increment", "UIHint" : "DISPLAY_SHORT" }
        definition.setIncrement is boolean;

        if (definition.setIncrement)
        {
            annotation { "Name" : "Increment", "UIHint" : "DISPLAY_SHORT" }
            isInteger(definition.RDelta, ROTATION_BOUNDS);
        }

        annotation { "Name" : "Show cut sheet sketches", "UIHint" : "REMEMBER_PREVIOUS_VALUE" }
        definition.showSheets is boolean;

        annotation { "Name" : "Material data", "UIHint" : [UIHint.READ_ONLY] }
        isAnything(definition.materialPropertyData);

        annotation { "Name" : "Refresh Layouts" }
        isButton(definition.refresh);

        // DEBUG ONLY
        // annotation { "Name" : "Test Part", "Filter" : EntityType.BODY, "MaxNumberOfPicks" : 1 }
        // definition.testP is Query;

    }
    {
        reportFeatureInfo(context, id, "Auto Layout with material and thickness grouping");
        //reportFeatureInfo(context, id, "If the groups look weird, check your part thicknesses and materials");


        // === Step 1: Extract all part data ===
        // getProperty is editLogic-only (properties.fs documents that it cannot be called
        // in the current context during feature body execution — features regenerate before
        // user-set properties are applied). editLogic therefore pre-resolves material names
        // to plain strings in definition.materialPropertyData, in the same order that
        // qAllModifiableSolidBodies() returns bodies. Both editLogic and the feature body
        // run against the same context state, so the index order is stable.
        //
        // Query objects must NOT be stored in definition. QueryType is an enum that becomes
        // a raw integer after JSON serialization, so canBeQuery (value.queryType is QueryType)
        // always fails when the map is read back. Bodies are obtained fresh here via
        // qNthElement, which is the typed Query constructor used throughout this file
        // (see doOneLayout, line ~229) and is guaranteed to pass canBeQuery.
        var partData = [];
        const allBodiesQuery = qAllModifiableSolidBodies();
        const bodyCount = size(evaluateQuery(context, allBodiesQuery));
        // Safely read the material name array; default to empty if editLogic hasn't run yet.
        const materialData = (definition.materialPropertyData != undefined) ? definition.materialPropertyData : [];

        for (var bodyIndex = 0; bodyIndex < bodyCount; bodyIndex += 1)
        {
            // qNthElement returns a typed Query (queryType: QueryType.NTH_ELEMENT) that
            // satisfies canBeQuery and can be passed to any function taking body is Query.
            const body = qNthElement(allBodiesQuery, bodyIndex);
            const materialName = (bodyIndex < size(materialData))
                ? materialData[bodyIndex].materialName
                : "Undefined Material";
            const thickness = getBoundingThickness(context, body);

            partData = append(partData, {
                        "entity" : body,
                        "material" : materialName,
                        "thickness" : thickness
                    });
        }

        println("Total parts collected: " ~ size(partData));

        // Step 2: Group by material and thickness using tolerantEquals
        var groups = {}; // material -> array of { thickness, parts }

        for (var part in partData)
        {
            const mat = part.material;

            if (groups[mat] == undefined)
                groups[mat] = [];

            var assigned = false;

            for (var i = 0; i < size(groups[mat]); i += 1)
            {
                const candidateThickness = groups[mat][i].thickness;

                if (tolerantEquals(part.thickness, candidateThickness))
                {
                    groups[mat][i].parts = append(groups[mat][i].parts, part);
                    assigned = true;
                    break;
                }
            }

            if (!assigned)
            {
                groups[mat] = append(groups[mat], {
                            "thickness" : part.thickness,
                            "parts" : [part]
                        });
            }
        }

        println("Material and thickness groups built:");
        println(groups);
        var groupIndex = 0;

        // === Step 3: Process each group ===
        for (var materialName in keys(groups))
        {
            for (var group in groups[materialName])
            {
                const thickness = group.thickness;
                const partGroup = group.parts;

                println("Laying out material: " ~ materialName ~ ", thickness: " ~ thickness);

                var partQueries = [];
                for (var part in partGroup)
                    partQueries = append(partQueries, part.entity);

                var combinedQuery = qUnion(partQueries);

                var tempDef = definition;
                tempDef.entities = combinedQuery;
                tempDef.thickness = thickness;
                tempDef.material = materialName;

                doOneLayout(context, id + makeId("group_" ~ groupIndex), tempDef, combinedQuery);
                groupIndex += 1;
            }
        }

    });


export function doOneLayout(context is Context, id is Id, definition is map, bodies is Query)
{
    // === Initial setup ===
    var initialY = 0 * meter;
    try silent
    {
        initialY = getVariable(context, "AutoLayout_yinitial");
    }

    // Remove already-placed bodies (so we only process unplaced parts)
    var hasAttribute = qAttributeQuery("" as AutoLayoutAttribute);
    bodies = qSubtraction(bodies, hasAttribute);

    // If user requested multiple copies, pattern them
    var operBodies = bodies;
    if (definition.copies && definition.N > 1)
    {
        var M = definition.N - 1;
        var transformArray = makeArray(M, identityTransform());
        var instanceArray = makeArray(M, "");
        for (var i = 0; i < M; i += 1)
        {
            instanceArray[i] = "" ~ i;
        }

        opPattern(context, id + "make_copies", {
                    "entities" : operBodies,
                    "transforms" : transformArray,
                    "instanceNames" : instanceArray
                });

        operBodies = qUnion([operBodies, qCreatedBy(id + "make_copies", EntityType.BODY)]);
    }

    // === Build blocks ===
    var blocks = [];
    var N = size(evaluateQuery(context, operBodies));

    for (var i = 0; i < N; i += 1)
    {
        var body = qNthElement(operBodies, i);
        var face = getOrientedFace(context, definition, id + "make_copies", body);

        if (!isQueryEmpty(context, face))
        {
            var blockInfo = getInitialTransform(context, definition, face);
            blocks = append(blocks, new box({
                            'w' : blockInfo.w,
                            'h' : blockInfo.h,
                            'owner' : body,
                            'transform' : blockInfo.transform,
                            'rotated' : false
                        }));
        }
    }

    // === Move unprocessed parts aside for clarity ===
    var noParts = qSubtraction(bodies, operBodies);
    if (!isQueryEmpty(context, noParts))
    {
        const bbox = evBox3d(context, { "topology" : noParts });
        var transformAway = transform(vector(-(definition.length * 0.3), 0 * meter, 0 * meter));
        var transformToOrigin = transform(-bbox.maxCorner);

        opTransform(context, id + "transform_noParts", {
                    "bodies" : noParts,
                    "transform" : transformAway * transformToOrigin
                });
    }

    // === Sort and place blocks ===
    var sortedBlocks = sortBlocks(blocks);
    var prevBlocks = [];
    var cutSheetNumber = 0;
    blocks = [];
    var placed = qNothing();

    while (size(sortedBlocks) > 0)
    {
        Packer(definition.length, definition.width, definition.spacing, sortedBlocks, cutSheetNumber, initialY);

        for (var i = 0; i < size(sortedBlocks); i += 1)
        {
            var block = sortedBlocks[i];

            if (block[].fit != undefined)
            {
                opTransform(context, id + ("transform_to_bin" ~ i ~ cutSheetNumber), {
                            "bodies" : block[].owner,
                            "transform" : block[].transform
                        });

                placed = qUnion([placed, block[].owner]);
            }
            else
            {
                blocks = append(blocks, block);
            }
        }

        // Optional: draw sheet layout sketch
        if (definition.showSheets)
        {
            var sketch = newSketch(context, id + ("sketch" ~ cutSheetNumber), {
                    "sketchPlane" : qCreatedBy(makeId("Top"), EntityType.FACE)
                });

            var newX = cutSheetNumber * definition.length * 1.1;

            skRectangle(sketch, "rectangle" ~ cutSheetNumber, {
                        "firstCorner" : vector(newX, initialY),
                        "secondCorner" : vector(definition.length + newX, initialY + definition.width)
                    });

            skSolve(sketch);
        }

        // Safety: check for packing failure
        if (size(prevBlocks) != 0 && size(blocks) != 0 && prevBlocks == blocks)
        {
            println("Warning: Some parts are too large for the sheet size. Moving aside.");

            // Collect oversized bodies
            var oversizedBodies = [];
            for (var block in blocks)
            {
                oversizedBodies = append(oversizedBodies, block[].owner);
            }

            if (size(oversizedBodies) > 0)
            {
                const materialLabel = definition.material != undefined ? definition.material : "Unknown Material";
                const thicknessLabel = round(definition.thickness / millimeter) ~ "mm";

                println("⚠️ " ~ size(oversizedBodies) ~ " part(s) too large for nesting in group " ~ materialLabel ~ " (" ~ thicknessLabel ~ "). Moved aside and excluded from layout.");

                const oversizedQuery = qUnion(oversizedBodies);
                const bbox = evBox3d(context, { "topology" : oversizedQuery });

                var offsetDistance = vector(definition.length * 1.5, 0 * meter, 0 * meter);
                var transformAway = transform(offsetDistance);
                var transformToOrigin = transform(-bbox.maxCorner);

                opTransform(context, id + makeId("transform_oversized_parts"), {
                            "bodies" : oversizedQuery,
                            "transform" : transformAway * transformToOrigin
                        });

                // setAttribute(context, {
                //             "entities" : oversizedQuery,
                //             "attribute" : "AutoLayout_OVERSIZED" as AutoLayoutAttribute
                //         });
            }

            // Clear blocks so nesting can continue
            blocks = [];
            prevBlocks = [];

            // No need to continue sorting, skip to next iteration
            sortedBlocks = [];
        }
        else
        {
            prevBlocks = blocks;
            sortedBlocks = sortBlocks(blocks);
            cutSheetNumber += 1;
            blocks = [];
        }

    }

    // === Final: Create composite part ===
    // IMPORTANT: setAttribute MUST come after opCreateCompositePart and setProperty.
    // `placed` is a qUnion of qNthElement(qSubtraction(bodies, hasAttribute), i) queries,
    // which are all lazily evaluated. Calling setAttribute on `placed` first marks those
    // bodies with AutoLayoutAttribute, causing qSubtraction(bodies, hasAttribute) to
    // exclude them, making every qNthElement inside `placed` resolve to the wrong body
    // or nothing. opCreateCompositePart would then receive 0 valid entities and throw
    // COMPOSITE_PART_SELECT_ENTITIES. Always evaluate `placed` for operations first,
    // then stamp the attribute.
    if (!isQueryEmpty(context, placed))
    {
        opCreateCompositePart(context, id + "Placed_Composite", {
                    "bodies" : placed
                });

        // Clean name: Material and thickness
        const cleanMaterialName = definition.material != undefined ? replace(definition.material, " ", "") : "UnknownMaterial";
        const cleanThickness = round(definition.thickness * 1000 / inch) / 1000;
        const compositeName = cleanThickness ~ "_" ~ cleanMaterialName;

        setProperty(context, {
                    "entities" : qCreatedBy(id + "Placed_Composite", EntityType.BODY),
                    "propertyType" : PropertyType.NAME,
                    "value" : compositeName
                });

        // Set attribute last so the lazy qSubtraction queries inside `placed` still
        // resolve correctly for the operations above.
        setAttribute(context, {
                    "entities" : placed,
                    "attribute" : "AutoLayout_PLACED" as AutoLayoutAttribute
                });
    }

    // Update Y variable for next layout stack
    setVariable(context, "AutoLayout_yinitial", initialY + definition.width * 1.1);
}



// Sort parts based on heuristic metric
// Currently sorted by decreasing area
export function sortBlocks(blocks is array)
{
    var sortedBlocks = sort(blocks, function(block1, block2)
    {
        // if (max(block2[].w, block2[].h) != max(block1[].w, block1[].h))
        // {
        //     return max(block2[].w, block2[].h) - max(block1[].w, block1[].h);
        // }
        // else
        // {
        //     return min(block2[].w, block2[].h) - min(block1[].w, block1[].h);
        // }

        return (block2[].w * block2[].h - block1[].w * block1[].h);

        // var block1param = block1[].w * block1[].h * (1 + log(max(block1[].w, block1[].h) / min(block1[].w, block1[].h)));
        // var block2param = block2[].w * block2[].h * (1 + log(max(block2[].w, block2[].h) / min(block2[].w, block2[].h)));
        // return block2param - block1param;
    });
    return sortedBlocks;
}

// This is a helper function that computes transforms to rotate blocks in place so that
// they can be placed either vertically or horizontally on the cut sheet.
export function rotateBlock(block is box)
{
    var zaxis is Line = line(vector(0, 0, 0) * inch, vector(0, 0, 1));
    var rotateTransform = rotationAround(zaxis, 90 * degree);
    var transformToOrigin = transform(vector(block[].h, 0 * inch, 0 * inch));

    block[].transform = transformToOrigin * rotateTransform * block[].transform;
    block[].rotated = true;
}

// Modified binary tree bin packing from: https://github.com/jakesgordon/bin-packing/blob/master/js/packer.js

// Initializer for the bin packing algorithm
export function Packer(length is ValueWithUnits, width is ValueWithUnits, spacing is ValueWithUnits, blocks is array, cutSheetNumber, initialY is ValueWithUnits) returns array
{
    var root = new box({ 'x' : cutSheetNumber * length * 1.1 + spacing, 'y' : initialY + spacing, 'w' : length - 2 * spacing, 'h' : width - 2 * spacing, 'used' : false, 'rotated' : false, 'fitParam' : 0 * meter });
    return fit(root, blocks, spacing);
}

// Fit function calls findNode to determine recursively where the part fits on the sheet,
// then calls placeBlockAndSplit to create a bin above and a bin to the right
export function fit(root is box, blocks is array, spacing is ValueWithUnits) returns array
{
    var node;
    var block;
    for (var i = 0; i < size(blocks); i += 1)
    {
        block = blocks[i];
        node = findNode(root, block);

        if (node != undefined)
        {
            block[].fit = placeBlockAndSplit(node, block, spacing);
        }
        else
        {
            block[].fit = undefined;
        }
    }
    return blocks;
}

// Recursively finds a bin where the part will fit
export function findNode(root is box, block is box)
{
    var w = block[].w;
    var h = block[].h;

    if (root[].used)
    {
        var right = findNode(root[].right, block);
        var above = findNode(root[].above, block);
        if (right != undefined && above != undefined)
        {
            // Part can fit in a subnode somewhere to the right or somewhere above; choose the better one according to a heuristic
            // Currently chooses the placement minimizing maximum X-coordinate
            if (above[].fitParam < right[].fitParam)
            {
                return above;
            }
            else
            {
                return right;
            }
        }
        else if (right != undefined)
        {
            return right;
        }
        else if (above != undefined)
        {
            return above;
        }
    }
    else // Find orientation within root with the minimum total x coordinate
    {
        var normalFit = undefined;
        var rotatedFit = undefined;

        if ((w < root[].w || tolerantEquals(w, root[].w)) && (h < root[].h || tolerantEquals(h, root[].h)))
        {
            // The part will fit in root without rotation
            normalFit = w + root[].x;
            // normalFit = root[].w - w;
        }

        if ((h < root[].w || tolerantEquals(h, root[].w)) && (w < root[].h || tolerantEquals(w, root[].h)))
        {
            // The part will fit in root with rotation
            rotatedFit = h + root[].x;
            // rotatedFit = root[].h - h;
        }

        if (normalFit != undefined && rotatedFit != undefined) //Part fits both ways, choose tighter fit
        {
            if (normalFit < rotatedFit || tolerantEquals(normalFit, rotatedFit))
            {
                root[].fitParam = normalFit;
                root[].rotated = false;
                return root;
            }
            else
            {
                root[].fitParam = rotatedFit;
                root[].rotated = true;
                return root;
            }
        }
        else if (normalFit != undefined)
        {
            root[].fitParam = normalFit;
            root[].rotated = false;
            return root;
        }
        else if (rotatedFit != undefined)
        {
            root[].fitParam = rotatedFit;
            root[].rotated = true;
            return root;
        }
        else
        {
            return undefined;
        }
    }
}

// Computes final transform on the part, splits the used node into one above it and one to the right of it
export function placeBlockAndSplit(node is box, block is box, spacing is ValueWithUnits) returns box
{
    if (node[].rotated)
    {
        rotateBlock(block);
    }
    var fitVector = vector(node[].x, node[].y, 0 * inch);
    var transformToBin = transform(fitVector);
    block[].transform = transformToBin * block[].transform;

    var w = block[].w;
    var h = block[].h;

    if (block[].rotated)
    {
        w = block[].h;
        h = block[].w;
    }

    node[].used = true;


    node[].above = new box({ 'x' : node[].x, 'y' : node[].y + h + spacing, 'w' : node[].w, 'h' : node[].h - (h + spacing), 'used' : false, 'rotated' : false, 'fitParam' : 0 * meter });
    node[].right = new box({ 'x' : node[].x + w + spacing, 'y' : node[].y, 'w' : node[].w - (w + spacing), 'h' : h, 'used' : false, 'rotated' : false, 'fitParam' : 0 * meter });

    // DEBUG ONLY
    // debug(context, node[].x);
    // debug(context, node[].y);
    // debug(context, node[].above);
    // debug(context, node[].right);


    return node;
}

// Computes the initial transform (rotation + movement) to place the part at the origin, oriented with the minimum bounding box
export function getInitialTransform(context is Context, definition is map, largestFace is Query)
{
    const tempLargestFacePlane = evPlane(context, {
                "face" : largestFace
            });
    const largestFacePlane = plane(tempLargestFacePlane.origin, -tempLargestFacePlane.normal, tempLargestFacePlane.x);

    const body = qOwnerBody(largestFace);

    // List of all straight edges to use as candidate x axes
    var orientationEdges = qGeometry(qAdjacent(largestFace, AdjacencyType.EDGE, EntityType.EDGE), GeometryType.LINE);
    // Only unique directions (including directly opposed)
    var unique = getUniqueVectors(context, orientationEdges);

    // DEBUG ONLY
    // debug(context, unique);

    if (size(unique) == 0)
    {
        var xDir = largestFacePlane.x;
        var yDir = yAxis(largestFacePlane);
        var increment = 15;
        if (definition.setIncrement)
        {
            increment = definition.RDelta;
        }

        for (var i = 0; i < 90; i += increment)
        {
            var testDir = xDir * cos(i * degree) + yDir * sin(i * degree);
            unique = append(unique, testDir);
        }
    }

    var finalXAxis = unique[0];
    var finalBBox = undefined;
    var minimumArea = (1000 * meter) * (1000 * meter); // assume we're nesting parts less than 1 km^2

    var deltaX = 0;
    var deltaY = 0;

    for (var dir in unique)
    {
        var orientedCSys = coordSystem(largestFacePlane.origin, dir, largestFacePlane.normal);
        var bbox is Box3d = evBox3d(context, {
                "topology" : body,
                "cSys" : orientedCSys
            });

        deltaX = abs(bbox.maxCorner[0] - bbox.minCorner[0]);
        deltaY = abs(bbox.maxCorner[1] - bbox.minCorner[1]);

        // Store dir if new minimum area
        if (deltaX * deltaY < minimumArea)
        {
            minimumArea = deltaX * deltaY;
            finalXAxis = dir;
            finalBBox = bbox;
        }
    }

    var finalDeltaX = abs(finalBBox.maxCorner[0] - finalBBox.minCorner[0]);
    var finalDeltaY = abs(finalBBox.maxCorner[1] - finalBBox.minCorner[1]);
    var finalCSys = coordSystem(largestFacePlane.origin, finalXAxis, largestFacePlane.normal);

    // DEBUG ONLY
    // debug(context, finalCSys);
    // debug(context, finalDeltaX);
    // debug(context, finalDeltaY);

    // Calculate transform to the top plane and to the origin
    var transformFromWorld = fromWorld(finalCSys);
    var transformToOrigin = transform(-finalBBox.minCorner);

    var blockInfo = {
        "transform" : transformToOrigin * transformFromWorld,
        "w" : finalDeltaX,
        "h" : finalDeltaY
    };

    return blockInfo;
}

export function getOrientedFace(context is Context, definition is map, patternID is Id, body is Query)
{
    var face = qNothing();

    if (definition.orientFaces)
    {
        var candidateFace = qLargest(qOwnedByBody(definition.orientedFaces, body)); // Grab largest of any oriented faces that are owned by this body
        var correspondingFace = getCorrespondingFace(context, definition, body);

        if (evaluateQuery(context, candidateFace) != [])
        {
            face = candidateFace; // One of the original parts, so use the selected face directly
        }
        else if (evaluateQuery(context, correspondingFace) != [])
        {
            face = correspondingFace; // If the body was created by pattern and there's a matching face, choose that
        }
        else
        {
            // No orientations, just get largest planar face
            face = getLargestFace(context, body);
        }
    }
    else
    {
        // No orientations, just get largest planar face
        face = getLargestFace(context, body);
    }
    return face;
}


// Given definition.orientedFaces containing a set of faces defining part orientations,
// and a body to find corresponding faces in,
// return the largest face owned by body such that
//   1) the face has equal area to a face in definition.orientedFaces
//   2) the face is coplanar to that same face in definition.orientedFaces
//   3) the face has maximum distance 0 to that same face in definition.orientedFaces
export function getCorrespondingFace(context is Context, definition is map, body is Query)
{
    var correspondingFace = qNothing();

    var orientedFaces = sort(evaluateQuery(context, definition.orientedFaces), function(face1, face2)
    {
        return (evArea(context, { "entities" : face2 }) - evArea(context, { "entities" : face1 }));
    });

    var NO = size(orientedFaces);

    for (var o = 0; o < NO; o += 1)
    {
        var orientedArea = evArea(context, { "entities" : orientedFaces[o] });
        var orientedPlane = evPlane(context, { "face" : orientedFaces[o] });
        var bodyFaces = sort(evaluateQuery(context,
            qCoincidesWithPlane(qOwnedByBody(body, EntityType.FACE), orientedPlane)
            ), function(face1, face2)
        {
            return (evArea(context, { "entities" : face2 }) - evArea(context, { "entities" : face1 }));
        });
        var NB = size(bodyFaces);

        for (var b = 0; b < NB; b += 1)
        {
            var bodyArea = evArea(context, { "entities" : bodyFaces[b] });

            if (tolerantEquals(orientedArea, bodyArea))
            {
                var distance = evDistance(context, {
                            "side0" : orientedFaces[o],
                            "side1" : bodyFaces[b]
                        }).distance;

                if (tolerantEquals(distance, 0 * meter))
                {
                    correspondingFace = bodyFaces[b];
                    return correspondingFace;
                }
            }
            else if (bodyArea < orientedArea)
            {
                break;
            }
        }
    }

    return correspondingFace;
}

// Given a set of edges, return an array U of unique directions such that for all a,b in U, dot(a,b) != 0
export function getUniqueVectors(context is Context, edgeList is Query)
{
    var edges = evaluateQuery(context, edgeList);
    var U = [];

    for (var edge in edges)
    {
        var edgeDirection = evAxis(context, { "axis" : edge }).direction;

        if (size(filter(U, function(x)
                    {
                        return abs(abs(dot(edgeDirection, x)) - 1) < 1e-6; // return elements of U which are parallel or antiparallel to edge
                    })) == 0) // If there are none of these, we have a new vector, so add it to the list
        {
            U = append(U, edgeDirection);
        }
    }

    return U;
}

// Gets the thickness of the part, normal to the largest face
export function getBoundingThickness(context is Context, body is Query)
{
    // Get largest planar face
    var face = getLargestFace(context, body);

    // If part has planar faces
    if (evaluateQuery(context, face) != [])
    {
        var largestFacePlane = evPlane(context, {
                "face" : face
            });
        const orientedCSys = planeToCSys(largestFacePlane);
        const bbox is Box3d = evBox3d(context, {
                    "topology" : body,
                    "cSys" : orientedCSys
                });
        return abs(bbox.maxCorner[2] - bbox.minCorner[2]);
    }
    else
    {
        return -1 * meter;
    }
}

export function editLogic(context is Context, id is Id, oldDefinition is map, definition is map, isCreating is boolean, specifiedParameters is map, clickedButton is string) returns map
{
    // Fetch all modifiable solid bodies in the Part Studio
    var entities = evaluateQuery(context, qAllModifiableSolidBodies());

    // Prepare an array to hold property data
    definition.materialPropertyData = [];

    // Iterate over each entity and fetch its property
    for (var entity in entities)
    {
        var propertyDef = {
            "entity" : entity,
            "propertyType" : PropertyType.MATERIAL
        };

        var prop = getProperty(context, propertyDef);

        // Store only the material name as a plain string. Query objects cannot be
        // round-tripped through definition because QueryType is an enum that becomes
        // a raw integer after JSON serialization, causing canBeQuery to fail at runtime.
        // The feature body re-evaluates qAllModifiableSolidBodies() to obtain fresh Query
        // references and correlates them with these material names by index.
        definition.materialPropertyData = append(definition.materialPropertyData, {
                    "materialName" : (prop != undefined && prop.name != undefined) ? prop.name : "Undefined Material"
                });
    }

    println("Number of entities found: " ~ size(definition.materialPropertyData));

    return definition;
}
