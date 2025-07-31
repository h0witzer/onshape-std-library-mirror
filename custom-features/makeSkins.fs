FeatureScript 2543;
import(path : "onshape/std/common.fs", version : "2543.0");
import(path : "c19fc82481f015a9979ba0ea", version : "a6f8ca9897b0fc17152577ee");

//Author: Christopher Violet / Dziuba.Christopher@gmail.com

annotation { "Feature Type Name" : "Make Skins", "Feature Type Description" : "" }
export const makeSkins = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Faces to Skin", "Filter" : EntityType.FACE, "MinNumberOfPicks" : 1 , "UIHint" : [UIHint.ALLOW_QUERY_ORDER] }
        definition.selectedFaces is Query;

        annotation { "Name" : "Skin Thickness" }
        isLength(definition.skinThickness, Laminate_Length_Bounds);

        annotation { "Name" : "Auto Select Tangent connected Faces", "Default" : true }
        definition.autoTangent is boolean;

        annotation { "Name" : "Join Tangent connected skins", "Default" : true }
        definition.joinTangent is boolean;

    }
    {
        forEachEntity(context, id + "makeselectedFaces1", qOwnerBody(definition.selectedFaces), function(skinedBody is Query, id is Id)
            {
                //make spliting surface
                var selectedFaces = qIntersection(definition.selectedFaces, qOwnedByBody(skinedBody, EntityType.FACE));
                if (definition.autoTangent)
                {
                    selectedFaces = qUnion(selectedFaces,qTangentConnectedFaces(selectedFaces));
                }
                selectedFaces = makeRobustQuery(context, selectedFaces);
                const selectedFacesPlusAdjacent = qAdjacent(selectedFaces, AdjacencyType.EDGE, EntityType.FACE)->qUnion(selectedFaces);
                opExtractSurface(context, id + "skineAndAdjacentSurfaces", {
                            "faces" : selectedFacesPlusAdjacent,
                            "offset" : 0 * millimeter,
                            "useFacesAroundToTrimOffset" : false
                        });
                var cutSurface = qCreatedBy(id + "skineAndAdjacentSurfaces", EntityType.BODY);

                //find the corresponding faces on the cut surfaces
                var cutFacesOrdered = [];
                var processedFaces = qNothing();
                for (var selectedFace in evaluateQuery(context, selectedFaces))
                {
                    if (!isQueryEmpty(context, qIntersection(processedFaces,selectedFace)))
                    {
                        continue;
                    }
                    var selectedFaceSet=selectedFace;
                    if (definition.joinTangent)
                    {
                        selectedFaceSet = qUnion(selectedFace,qTangentConnectedFaces(selectedFace)->qIntersection(selectedFaces));
                    }
                    const matchingFaces = findMatchingFaces(context, selectedFaceSet, cutSurface);
                    cutFacesOrdered = append(cutFacesOrdered,matchingFaces);
                    processedFaces = qUnion(processedFaces,selectedFaceSet);
                }
                
                //Offset the face and split what remains
                var remainingSkinedBody = skinedBody;
                for (var i = 0; i < size(cutFacesOrdered); i += 1)
                {
                    var facesToCut = cutFacesOrdered[i];

                    //offset face and cut skinned body
                    opOffsetFace(context, id + i + "offsetToCut", {
                                "moveFaces" : facesToCut,
                                "offsetDistance" : -definition.skinThickness
                            });
                    opSplitPart(context, id + i + "splitLaminate", {
                                "targets" : remainingSkinedBody,
                                "tool" : cutSurface,
                                "keepTools" : true
                            });
                    remainingSkinedBody = qSplitBy(id + i + "splitLaminate", EntityType.BODY, true);
                }
                opDeleteBodies(context, id + "deleteSpliters", {
                            "entities" : cutSurface
                        });
            });
    });

export function findMatchingFaces(context is Context, facesToFindMatch, faceFindEntities is Query)
{
    const facesToCheck = qOwnedByBody(faceFindEntities, EntityType.FACE);
    var boxsToMatch=[];
    for(var faceToFindMatch in evaluateQuery(context, facesToFindMatch))
    {
        boxsToMatch = append(boxsToMatch, evBox3d(context, { "topology" : faceToFindMatch, "tight" : true }));
    }
    
    var matchedFaces = qNothing();
    for(var boxToMatch in boxsToMatch)
    {
        for (var i = 0; i < size(evaluateQuery(context, facesToCheck)); i += 1)
        {
            var faceToCheck = qNthElement(facesToCheck, i);
            var boxToCheck = evBox3d(context, { "topology" : faceToCheck, "tight" : true });
            if (boxToCheck == boxToMatch)matchedFaces = qUnion(matchedFaces,faceToCheck);
        }
    }
    return makeRobustQuery(context, matchedFaces);
}
