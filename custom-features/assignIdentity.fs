FeatureScript 1036;
import(path : "onshape/std/geometry.fs", version : "1036.0");

annotation { "Feature Type Name" : "Assign Identity",
             "Feature Name Template": "Identity #identity", 
             "UIHint" : "NO_PREVIEW_PROVIDED",
             "Editing Logic Function" : "assignIdentityEditingLogic"}
export const assignIdentity = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Entity", 
                    "Filter" : EntityType.FACE || EntityType.BODY || 
                               EntityType.EDGE || QueryFilterCompound.ALLOWS_VERTEX,
                    "MaxNumberOfPicks" : 1,
                    "UIHint" : UIHint.PREVENT_CREATING_NEW_MATE_CONNECTORS}
        definition.entity is Query;
        
        annotation { "Name" : "Identity", "UIHint" : "UNCONFIGURABLE" }
        definition.identity is string;
        
    }
    {
        opNameEntity(context, id, { "entity" : definition.entity, "entityName" : definition.identity});
    });
    
    
export function assignIdentityEditingLogic(context is Context, id is Id, oldDefinition is map, definition is map,
    specifiedParameters is map) returns map
{
    if (specifiedParameters.identity != true)
        definition.identity = toIdentity(id);

    return definition;
}

function toIdentity(id is Id) returns string
{
    var out is string = id[0];
    for (var i = 1; i < size(id); i += 1)
    {
        out ~= "." ~ id[i];
    }
    return out;
}
