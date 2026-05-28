/*
    Self-Shadow Tester

    Calls opSplitBySelfShadow on user-selected bodies with a user-supplied view
    direction and paints each result set a distinct debug color. Use this to
    understand how the view direction maps to visible/invisible classifications
    before wiring that logic into a production feature.

    Result sets:
        GREEN   — visibleFaces   (all faces classified visible, including unsplit ones)
        BLUE    — invisibleFaces (all faces classified invisible, including unsplit ones)
        YELLOW  — shadow edges   (the new imprint edges created by the split)
        CYAN    — split-visible  (only the faces that were actually split, visible side)
        MAGENTA — split-invisible(only the faces that were actually split, invisible side)

    No geometry is deleted. The split edges are committed to the model so you can
    inspect the topology; roll back the feature when done.
*/

FeatureScript 2909;
import(path : "onshape/std/geometry.fs", version : "2909.0");

annotation { "Feature Type Name" : "Self-Shadow Tester" }
export const selfShadowTester = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Bodies",
                     "Filter" : EntityType.BODY && BodyType.SOLID && ModifiableEntityOnly.YES }
        definition.bodies is Query;

        annotation { "Name" : "View direction",
                     "Description" : "Edge, linear axis, planar face normal, or mate connector Z-axis",
                     "Filter" : QueryFilterCompound.ALLOWS_DIRECTION || BodyType.MATE_CONNECTOR,
                     "MaxNumberOfPicks" : 1 }
        definition.viewDirectionEntity is Query;

        annotation { "Name" : "Flip direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
        definition.flipDirection is boolean;

        annotation { "Name" : "Show visible faces (GREEN)" }
        definition.showVisibleFaces is boolean;

        annotation { "Name" : "Show invisible faces (BLUE)" }
        definition.showInvisibleFaces is boolean;

        annotation { "Name" : "Show shadow edges (YELLOW)" }
        definition.showShadowEdges is boolean;

        annotation { "Name" : "Show split-visible faces (CYAN)" }
        definition.showSplitVisible is boolean;

        annotation { "Name" : "Show split-invisible faces (MAGENTA)" }
        definition.showSplitInvisible is boolean;
    }
    {
        verifyNonemptyQuery(context, definition, "bodies", "Select at least one solid body.");
        verifyNonemptyQuery(context, definition, "viewDirectionEntity", "Select an edge, face, or mate connector to define the view direction.");

        // Resolve the view direction vector from the selection.
        var viewDir;
        if (!isQueryEmpty(context, qBodyType(definition.viewDirectionEntity, BodyType.MATE_CONNECTOR)))
        {
            // Use the Z axis of the mate connector as the view direction.
            viewDir = evMateConnector(context, { "mateConnector" : definition.viewDirectionEntity }).zAxis;
        }
        else
        {
            viewDir = extractDirection(context, definition.viewDirectionEntity);
        }

        if (definition.flipDirection)
            viewDir = -viewDir;

        reportFeatureInfo(context, id, "View direction: " ~ viewDir);

        // Run the shadow split.
        const splitResult = opSplitBySelfShadow(context, id + "split", {
                    "bodies"        : definition.bodies,
                    "viewDirection" : viewDir
                });

        const visibleFacesQuery   = qUnion(splitResult.visibleFaces);
        const invisibleFacesQuery = qUnion(splitResult.invisibleFaces);
        const shadowEdgesQuery    = qCreatedBy(id + "split", EntityType.EDGE);
        const splitVisibleQuery   = qSplitBy(id + "split", EntityType.FACE, false);
        const splitInvisibleQuery = qSplitBy(id + "split", EntityType.FACE, true);

        // Report counts for each set.
        reportFeatureInfo(context, id,
            "visible=" ~ evaluateQueryCount(context, visibleFacesQuery) ~
            "  invisible=" ~ evaluateQueryCount(context, invisibleFacesQuery) ~
            "  shadow edges=" ~ evaluateQueryCount(context, shadowEdgesQuery) ~
            "  split-visible=" ~ evaluateQueryCount(context, splitVisibleQuery) ~
            "  split-invisible=" ~ evaluateQueryCount(context, splitInvisibleQuery));

        // Paint the requested sets.
        if (definition.showVisibleFaces)
            addDebugEntities(context, visibleFacesQuery, DebugColor.GREEN);

        if (definition.showInvisibleFaces)
            addDebugEntities(context, invisibleFacesQuery, DebugColor.BLUE);

        if (definition.showShadowEdges)
            addDebugEntities(context, shadowEdgesQuery, DebugColor.YELLOW);

        if (definition.showSplitVisible)
            addDebugEntities(context, splitVisibleQuery, DebugColor.CYAN);

        if (definition.showSplitInvisible)
            addDebugEntities(context, splitInvisibleQuery, DebugColor.MAGENTA);

    }, {
        flipDirection      : false,
        showVisibleFaces   : true,
        showInvisibleFaces : true,
        showShadowEdges    : true,
        showSplitVisible   : false,
        showSplitInvisible : false
    });
