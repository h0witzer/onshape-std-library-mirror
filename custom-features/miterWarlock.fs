FeatureScript 2641; // Or your desired version
import(path : "onshape/std/common.fs", version : "2641.0");
import(path : "onshape/std/evaluate.fs", version : "2641.0");
import(path : "onshape/std/query.fs", version : "2641.0"); // For qOwnerBody, qCapEntity, qIntersection etc.
import(path : "onshape/std/boolean.fs", version : "2641.0"); // For processNewBodyIfNeeded if original boolean intent is kept
import(path : "onshape/std/deleteFace.fs", version : "2641.0"); // For opDeleteFace
import(path : "onshape/std/splitpart.fs", version : "2641.0"); // For opSplitPart
import(path : "onshape/std/loft.fs", version : "2641.0");
import(path : "onshape/std/tool.fs", version : "2641.0");
import(path : "onshape/std/defaultFeatures.fs", version : "2641.0"); // For qNothing
import(path : "onshape/std/error.fs", version : "2641.0"); // For reportFeatureError, setErrorEntities etc.
import(path : "5557b5ff5a6ef09364d81524", version : "df1182ec466fd584e2b73bf5"); // oppositeFace
import(path : "dc975367d80965f6254eb9dc", version : "734e497a16d6a2a2e121a5eb"); // triangulateFaces
import(path : "onshape/std/lofttopology.gen.fs", version : "2641.0");
import(path : "onshape/std/booleanoperationtype.gen.fs", version : "2641.0"); // For BooleanOperationType
import(path : "onshape/std/splitoperationkeeptype.gen.fs", version : "2641.0"); // For SplitOperationKeepType


/**
* @Name Miter Warlock
 * @Description For each selected face, finds its opposite face, creates a loft,
 *          does some triangulation to the degenerate faces of some lofts
 *          modifies the loft to be a splitting tool, and splits the original body.
 *
 * @param context The current context.
 * @param definition Map containing the definition parameters.
 * @field startFaces {Query} : Query resolving to one or more faces to loft *from*.
 * @field id {Id} : The ID of the calling feature, used for sub-feature IDs and error reporting.
 * @field bodyType {ToolBodyType} : @optional The type of body to create for the initial loft (SOLID or SURFACE). Default is SOLID.
 * @field keepSplitToolParts {SplitOperationKeepType} : @optional Which parts to keep after splitting. Default is KEEP_ALL.
 */


annotation { "Feature Type Name" : "Miter Warlock" }
export const loftToOppositeFeature = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Start Faces",
                    "Filter" : EntityType.FACE && ConstructionObject.NO && SketchObject.NO,
                    "Description" : "Select faces to loft FROM. The opposite face will be found automatically." }
        definition.startFaces is Query;
    }
    {
        // Call the main logic function
        loftToOppositeFaces(context, {
                    "startFaces" : definition.startFaces,
                    "id" : id, // Pass the feature ID
                    "bodyType" : definition.bodyType,
                    "operationType" : definition.operationType,
                    "booleanScope" : definition.booleanScope,

                });
    });

export function loftToOppositeFaces(context is Context, definition is map)
{
    // --- Input Validation ---
    if (!(definition.startFaces is Query))
    {
        throw regenError("Input 'startFaces' must be a Query.");
    }
    if (!(definition.id is Id))
    {
        throw regenError("Internal error: Feature ID (id) was not provided.");
    }

    const startFacesArray = evaluateQuery(context, definition.startFaces);
    const numFaces = size(startFacesArray);
    if (numFaces == 0)
    {
        reportFeatureWarning(context, definition.id, "No start faces selected.");
        return;
    }

    // --- Get Parameters ---
    // bodyType for the initial loft. If SOLID, caps will be deleted to make it a surface tool.
    const bodyTypeForLoftTool = definition.bodyType == undefined ? ToolBodyType.SOLID : definition.bodyType;
    const topology = definition.topologyOption == undefined ? LoftTopology.MINIMAL : definition.topologyOption;
    const keepSplitParts = definition.keepSplitToolParts == undefined ? SplitOperationKeepType.KEEP_ALL : definition.keepSplitToolParts;

    // --- Loop, Find Opposite, Loft, Prepare Tool, and Split ---
    // ... (numFaces, bodyTypeForLoftTool, topology, keepSplitParts) ...
    var facesWithoutOppositeCount = 0;

    // --- Pass 1: Generate all tools and collect target/tool pairs ---
    var splitTasks = []; // Array of maps: [{targetBodyQ: Query, toolBodyQ: Query}]

    for (var i = 0; i < numFaces; i += 1)
    {
        const faceA = startFacesArray[i];
        const originalFaceAOwnerBodyQuery = qOwnerBody(faceA); // Query for the owner at the start

        // --- IDs for tool generation phase for THIS faceA ---
        const toolGenPhaseIdRoot = definition.id + "toolGen" + i;
        const findOppositeId = toolGenPhaseIdRoot + "findOpposite";
        const loftId = toolGenPhaseIdRoot + "loft";
        const triangulateOpId = toolGenPhaseIdRoot + "triangulate"; // If active
        const deleteCapsId = toolGenPhaseIdRoot + "deleteCaps";

        const faceBQuery = findOppositeFace(context, { "selectedFace" : faceA, "id" : findOppositeId });
        if (isQueryEmpty(context, faceBQuery))
        {
            facesWithoutOppositeCount += 1;
            continue;
        }

        var currentLoftBodyQuery; // Query to the body created by opLoft
        var toolSheetBodyQuery; // Query to the final sheet tool

        try
        {
            opLoft(context, loftId, {
                        "profileSubqueries" : [faceA, faceBQuery],
                        "bodyType" : bodyTypeForLoftTool,
                        "operationType" : NewBodyOperationType.NEW,
                        "topology" : topology
                    });
            currentLoftBodyQuery = qCreatedBy(loftId, EntityType.BODY);
            if (isQueryEmpty(context, currentLoftBodyQuery))
            { /* warning, continue */
            }

            // Triangulate (if needed, ensure it modifies currentLoftBodyQuery or update reference)
            try
            {
                triangulateFaces(context, triangulateOpId, { "inputBody" : currentLoftBodyQuery });
            }
            catch (triangulateError)
            { /* error, continue */
            }

            toolSheetBodyQuery = currentLoftBodyQuery; // Assume it might stay as is if already surface or no caps
            if (bodyTypeForLoftTool == ToolBodyType.SOLID)
            {
                const startCaps = qIntersection([qCapEntity(loftId, CapType.START, EntityType.FACE), qOwnedByBody(currentLoftBodyQuery, EntityType.FACE)]);
                const endCaps = qIntersection([qCapEntity(loftId, CapType.END, EntityType.FACE), qOwnedByBody(currentLoftBodyQuery, EntityType.FACE)]);
                const capsToDelete = qUnion([startCaps, endCaps]);
                // debug(context, capsToDelete, DebugColor.CYAN); // Keep for verification

                if (!isQueryEmpty(context, capsToDelete))
                {
                    opDeleteFace(context, deleteCapsId, {
                                "deleteFaces" : capsToDelete, "includeFillet" : false, "capVoid" : false, "leaveOpen" : true
                            });
                    // currentLoftBodyQuery still refers to the body, now modified (hopefully to sheet)
                    if (size(evaluateQuery(context, qBodyType(currentLoftBodyQuery, BodyType.SHEET))) == 0)
                    {
                        reportFeatureWarning(context, deleteCapsId, "Tool from face " ~ i ~ " did not become sheet. Skipping.");
                        continue;
                    }
                    toolSheetBodyQuery = currentLoftBodyQuery; // Confirmed or now a sheet
                }
                else
                {
                    // No caps found, if it started solid and is still solid, it's not the desired tool.
                    if (size(evaluateQuery(context, qBodyType(currentLoftBodyQuery, BodyType.SHEET))) == 0)
                    {
                        reportFeatureWarning(context, deleteCapsId, "Solid tool from face " ~ i ~ " had no caps, not a sheet. Skipping.");
                        continue;
                    }
                    // If it was already a surface, or became one (e.g. degenerate loft), toolSheetBodyQuery is fine.
                }
            }

            // If we successfully have a tool (presumably a sheet body now)
            if (!isQueryEmpty(context, toolSheetBodyQuery) &&
                !isQueryEmpty(context, originalFaceAOwnerBodyQuery)) // Double check owner too
            {
                splitTasks = append(splitTasks, {
                            "targetBodyQ" : originalFaceAOwnerBodyQuery,
                            "toolBodyQ" : toolSheetBodyQuery,
                            "originalFaceAIndex" : i // For debug/error messages
                        });
            }

        }
        catch (toolGenError)
        {
            reportFeatureError(context, definition.id, "Error generating tool for face " ~ (i + 1) ~ ": " ~ toolGenError.message);
            setErrorEntities(context, definition.id, { "entities" : faceA });
        }
    } // End of Pass 1 (Tool Generation)

    // --- Pass 2: Perform Splits ---
    // Group tools by target body to make one split call per original target body
    var toolsByOriginalTargetKey = {}; // Key: toString(targetQuery), Value: {targetQ: Query, toolQs: [Query]}
    var uniqueOriginalTargetsOrdered = []; // To maintain order and get unique queries

    for (var task in splitTasks)
    {
        const targetKey = toString(task.targetBodyQ); // Simplistic key, assumes stable string for same initial query
        if (toolsByOriginalTargetKey[targetKey] == undefined)
        {
            toolsByOriginalTargetKey[targetKey] = { "targetQ" : task.targetBodyQ, "toolQs" : [] };
            uniqueOriginalTargetsOrdered = append(uniqueOriginalTargetsOrdered, task.targetBodyQ); // Or store the key and lookup later
        }
        toolsByOriginalTargetKey[targetKey].toolQs = append(toolsByOriginalTargetKey[targetKey].toolQs, task.toolBodyQ);
    }

    for (var k = 0; k < size(uniqueOriginalTargetsOrdered); k += 1)
    {
        const targetQuery = uniqueOriginalTargetsOrdered[k];
        const targetKey = toString(targetQuery);
        const taskGroup = toolsByOriginalTargetKey[targetKey];

        if (taskGroup == undefined || size(taskGroup.toolQs) == 0)
            continue;

        const combinedToolQuery = qUnion(taskGroup.toolQs);
        const splitOpId = definition.id + "performSplit" + k; // Unique ID for this split operation

        try
        {
            // debug(context, taskGroup.targetQ, DebugColor.RED);
            // debug(context, combinedToolQuery, DebugColor.GREEN);

            opSplitPart(context, splitOpId, {
                        "targets" : taskGroup.targetQ, // The original target body query
                        "tool" : combinedToolQuery,
                        "keepTool" : false,
                        "keepType" : keepSplitParts
                    });
        }
        catch (splitError)
        {
            reportFeatureError(context, definition.id, "Split failed for target set " ~ k ~ ". Error: " ~ splitError.message);
            setErrorEntities(context, definition.id, qUnion([taskGroup.targetQ, combinedToolQuery]));
        }
    }
}
