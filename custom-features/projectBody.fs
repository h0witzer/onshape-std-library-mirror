FeatureScript 1337;
import(path : "onshape/std/geometry.fs", version : "1337.0");

// This is for query addition
import(path : "cfcc264d41817d876589755c/57b971a18d531de9c6b1ab4b/5f9b7e7b3552581bf2500485", version : "9a27a81f350038cc6a439be4");

annotation { "Feature Type Name" : "Project body" }
export const opCreateOutlineFS = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Tool bodies and faces", "Filter" : (EntityType.FACE || EntityType.BODY && (BodyType.SOLID || BodyType.SHEET)) && ConstructionObject.NO }
        definition.tools is Query;

        annotation { "Name" : "Targets",
                    "Filter" : (EntityType.FACE && (GeometryType.PLANE || GeometryType.CYLINDER || GeometryType.EXTRUDED)) || BodyType.MATE_CONNECTOR }
        definition.target is Query;
    }
    {
        var toDelete is Query = qNothing();
        var tools is Query = definition.tools;
        if (context->evaluateQuery(tools->qEntityFilter(EntityType.FACE)) != []) // We have faces.
        {
            // To deal with them, we use opExtractSurface to get a surface body.
            const toolFaces is Query = tools->qEntityFilter(EntityType.FACE);
            opExtractSurface(context, id + "extract", {
                        "faces" : toolFaces,
                        "offset" : 0 * meter
                    });

            const extracted is Query = qCreatedBy(id + "extract");
            tools = tools - toolFaces + extracted->qEntityFilter(EntityType.BODY);
            toDelete += extracted;
        }
        const targets is array = context->evaluateQuery(definition.target);
        var i is number = 0;

        for (var target in targets)
        {
            // We use unstableIdComponent here, so that, if we change the face selection, it won't break downstream queries.
            const id is Id = id + unstableIdComponent(i);
            context->setExternalDisambiguation(id, target);

            if (context->evaluateQuery(target->qEntityFilter(EntityType.FACE)) == []) // It is a mate connector
            {
                // Create a plane, so that we can then pass it to opCreateOutline.
                context->opPlane(id + "plane", {
                            "plane" : context->evMateConnector({ "mateConnector" : target })->plane()
                        });

                const q is Query = qCreatedBy(id + "plane");

                target = q->qEntityFilter(EntityType.FACE);
                toDelete += q; // Make sure that we clean up after ourselves.
            }
            context->opCreateOutline(id + "outline", {
                        "tools" : tools,
                        "target" : target,
                        "asVersion" : FeatureScriptVersionNumber.V876_PS_VERSION_31_0_154
                    });
            i += 1;
        }

        if (toDelete != qNothing())
            context->opDeleteBodies(id + "delete", { "entities" : toDelete });
    });
