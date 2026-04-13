FeatureScript 2909;
// SM Tab Tag — Tag Part Studio setup feature for Sheet Metal Tab Apply.
// Marks surface bodies (union geometry), surface subtraction tool bodies, and an optional
// placement origin mate connector so that smTabApply.fs can instantiate, thicken, and merge
// them into a target sheet metal model without any thickness knowledge in the tag Part Studio.
//
// Design intent
//   - All tag bodies (union, local subtract, outer subtract) are surface bodies.  smTabApply.fs
//     thickens every tagged surface using the target model's getModelParameters at apply-time.
//   - Union surfaces must be planar sheet bodies.  opThicken + SM wall matching is performed
//     entirely by smTabApply.fs.
//   - Keeping thickness out of this Part Studio enables a future opWrap path for non-planar
//     SM walls; the tag contract does not need to change when that path is added.

import(path : "onshape/std/attributes.fs", version : "2909.0");
import(path : "onshape/std/containers.fs", version : "2909.0");
import(path : "onshape/std/coordSystem.fs", version : "2909.0");
import(path : "onshape/std/error.fs", version : "2909.0");
import(path : "onshape/std/evaluate.fs", version : "2909.0");
import(path : "onshape/std/feature.fs", version : "2909.0");
import(path : "onshape/std/geomOperations.fs", version : "2909.0");
import(path : "onshape/std/query.fs", version : "2909.0");
import(path : "onshape/std/units.fs", version : "2909.0");

// ---------------------------------------------------------------------------
// Attribute names — must match the constants read by smTabApply.fs
// ---------------------------------------------------------------------------

/**
 * Named attribute key written onto every body tagged by smTabTag.
 * smTabApply.fs reads bodies carrying this name and inspects the role field.
 */
export const SM_TAB_BODY_ATTRIBUTE_NAME = "smTabBodyAttribute";

/**
 * Role written into the attribute map for union surface bodies (planar sheet bodies that
 * define the tab footprint).  smTabApply.fs thickens these using the target SM model parameters.
 */
export const SM_TAB_ROLE_UNION_SURFACE = "smTabUnionSurface";

/**
 * Role written into the attribute map for solid bodies used as localised subtraction tools
 * (slots, relief cuts, passthroughs) applied only to the merged SM wall.
 */
export const SM_TAB_ROLE_LOCAL_SUBTRACT = "smTabLocalSubtractBody";

/**
 * Role written into the attribute map for solid bodies used as general outer subtraction tools
 * applied across the broader subtraction scope specified in smTabApply.fs.
 */
export const SM_TAB_ROLE_OUTER_SUBTRACT = "smTabOuterSubtractBody";

/**
 * Variable name stored in the Part Studio context so that smTabApply.fs can retrieve
 * a human-readable feature name via buildFunction + getVariable.
 */
export const SM_TAB_FEATURE_NAME_VAR = "smTabFeatureName";

// ---------------------------------------------------------------------------
// Feature definition
// ---------------------------------------------------------------------------

/**
 * SM Tab Tag feature.
 *
 * Place this feature inside a tool Part Studio that will be referenced by smTabApply.fs.
 * Tag each body with its role (union surface, local subtraction, or outer subtraction) and
 * optionally mark a mate connector as the placement origin.
 *
 * Feature Name Template: displays "SM Tab Tag - <name>" when a feature name is provided.
 */
annotation { "Feature Type Name" : "SM Tab Tag", "Feature Name Template" : "SM Tab Tag#featureNameDisplay" }
export const smTabTag = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // ------------------------------------------------------------------
        // Union surface bodies — restricted to planar sheet/surface bodies.
        // The PLANE geometry filter prevents non-planar surfaces from being
        // selected; nonplanar support will be added in a future opWrap path.
        // ------------------------------------------------------------------
        annotation {
                    "Name" : "Union surface bodies",
                    "Description" : "Planar surface bodies that define the tab footprint. smTabApply.fs thickens these to match the target SM model.",
                    "Filter" : EntityType.BODY && BodyType.SHEET
                }
        definition.unionSurfaceBodies is Query;

        // ------------------------------------------------------------------
        // Local subtraction surfaces — cuts scoped to the merged SM wall only.
        // smTabApply.fs thickens these using the target model parameters before cutting.
        // ------------------------------------------------------------------
        annotation {
                    "Name" : "Local subtraction bodies",
                    "Description" : "Surface bodies for localised cuts on the merged wall (slots, relief cuts, passthroughs). smTabApply.fs thickens these to match the target SM model.",
                    "Filter" : EntityType.BODY && BodyType.SHEET
                }
        definition.localSubtractBodies is Query;

        // ------------------------------------------------------------------
        // Outer subtraction surfaces — broader subtraction scope.
        // smTabApply.fs thickens these using the target model parameters before cutting.
        // ------------------------------------------------------------------
        annotation {
                    "Name" : "Outer subtraction bodies",
                    "Description" : "Surface bodies for general subtraction applied across the full subtraction scope in smTabApply.fs. smTabApply.fs thickens these to match the target SM model.",
                    "Filter" : EntityType.BODY && BodyType.SHEET
                }
        definition.outerSubtractBodies is Query;

        // ------------------------------------------------------------------
        // Placement origin mate connector.
        // If omitted, a connector is created automatically at the world origin.
        // ------------------------------------------------------------------
        annotation {
                    "Name" : "Placement origin mate connector",
                    "Description" : "Defines the tool coordinate system.  Leave empty to use the world origin.",
                    "Filter" : BodyType.MATE_CONNECTOR,
                    "MaxNumberOfPicks" : 1
                }
        definition.csysMateConnector is Query;

        // ------------------------------------------------------------------
        // Human-readable feature name forwarded to smTabApply feature tree.
        // ------------------------------------------------------------------
        annotation { "Name" : "Feature name (optional)" }
        definition.featureName is string;

        // Hidden computed parameter for Feature Name Template display.
        annotation { "Name" : "Feature name display (computed)", "UIHint" : UIHint.ALWAYS_HIDDEN }
        definition.featureNameDisplay is string;
    }
    {
        verify(!isInFeaturePattern(context), "smTabTag cannot be used inside a feature pattern.");

        // ------------------------------------------------------------------
        // Validate selections before writing any attributes.
        // ------------------------------------------------------------------
        const hasUnionSurfaces    = !isQueryEmpty(context, definition.unionSurfaceBodies);
        const hasLocalSubtract    = !isQueryEmpty(context, definition.localSubtractBodies);
        const hasOuterSubtract    = !isQueryEmpty(context, definition.outerSubtractBodies);

        if (!hasUnionSurfaces && !hasLocalSubtract && !hasOuterSubtract)
        {
            throw regenError("Select at least one union surface body or subtraction tool body.");
        }

        // Union surfaces must be planar sheet bodies.
        if (hasUnionSurfaces)
        {
            validateUnionSurfaces(context, id, definition.unionSurfaceBodies);
        }

        // Subtraction bodies must be solid, non-consumed.
        if (hasLocalSubtract)
        {
            validateSubtractBodies(context, id, definition.localSubtractBodies, "localSubtractBodies");
        }
        if (hasOuterSubtract)
        {
            validateSubtractBodies(context, id, definition.outerSubtractBodies, "outerSubtractBodies");
        }

        // ------------------------------------------------------------------
        // Clear any existing smTab attributes so re-running tag is idempotent.
        // ------------------------------------------------------------------
        const previouslyTaggedBodies = qHasAttribute(qEverything(EntityType.BODY), SM_TAB_BODY_ATTRIBUTE_NAME);
        if (!isQueryEmpty(context, previouslyTaggedBodies))
        {
            reportFeatureInfo(context, id, "Removing previous SM Tab Tag attributes before applying new ones.");
            setAttribute(context, {
                        "entities" : previouslyTaggedBodies,
                        "name" : SM_TAB_BODY_ATTRIBUTE_NAME,
                        "attribute" : undefined
                    });
        }

        // ------------------------------------------------------------------
        // Write role attributes.
        // ------------------------------------------------------------------
        if (hasUnionSurfaces)
        {
            setAttribute(context, {
                        "entities" : definition.unionSurfaceBodies,
                        "name" : SM_TAB_BODY_ATTRIBUTE_NAME,
                        "attribute" : { "role" : SM_TAB_ROLE_UNION_SURFACE }
                    });
        }
        if (hasLocalSubtract)
        {
            setAttribute(context, {
                        "entities" : definition.localSubtractBodies,
                        "name" : SM_TAB_BODY_ATTRIBUTE_NAME,
                        "attribute" : { "role" : SM_TAB_ROLE_LOCAL_SUBTRACT }
                    });
        }
        if (hasOuterSubtract)
        {
            setAttribute(context, {
                        "entities" : definition.outerSubtractBodies,
                        "name" : SM_TAB_BODY_ATTRIBUTE_NAME,
                        "attribute" : { "role" : SM_TAB_ROLE_OUTER_SUBTRACT }
                    });
        }

        // ------------------------------------------------------------------
        // Resolve the placement origin mate connector.
        // Create one at the world origin if none was selected.
        // ------------------------------------------------------------------
        var csysMateConnector = definition.csysMateConnector;
        if (isQueryEmpty(context, csysMateConnector))
        {
            const originConnectorId = id + "originMateConnector";
            opMateConnector(context, originConnectorId, { "coordSystem" : WORLD_COORD_SYSTEM });
            csysMateConnector = qCreatedBy(originConnectorId, EntityType.BODY)->qBodyType(BodyType.MATE_CONNECTOR);
        }
        setAttribute(context, {
                    "entities" : csysMateConnector,
                    "name" : SM_TAB_BODY_ATTRIBUTE_NAME,
                    "attribute" : { "role" : "smTabCsysMateConnector" }
                });

        // ------------------------------------------------------------------
        // Store feature name as a Part Studio variable for smTabApply.fs to read.
        // ------------------------------------------------------------------
        if (definition.featureName != "")
        {
            setVariable(context, SM_TAB_FEATURE_NAME_VAR, definition.featureName);
        }

        // Update the Feature Name Template display string.
        const displayName = (definition.featureName != "") ? (" - " ~ definition.featureName) : "";
        setFeatureComputedParameter(context, id, { "name" : "featureNameDisplay", "value" : displayName });

    }, {
            unionSurfaceBodies  : qNothing(),
            localSubtractBodies : qNothing(),
            outerSubtractBodies : qNothing(),
            csysMateConnector   : qNothing(),
            featureName         : "",
            featureNameDisplay  : ""
        });

// ---------------------------------------------------------------------------
// Validation helpers
// ---------------------------------------------------------------------------

/**
 * Verify that all bodies in unionSurfaces are planar sheet bodies.
 * Non-planar sheets (cylinders, cones, etc.) are rejected here; they will be
 * supported in a future opWrap-based extension of smTabApply.fs.
 *
 * @param context        {Context}
 * @param id             {Id}              Feature id used for error reporting.
 * @param unionSurfaces  {Query}           Bodies to validate; must all be BodyType.SHEET.
 */
function validateUnionSurfaces(context is Context, id is Id, unionSurfaces is Query)
{
    // All selected bodies must be sheet bodies (the filter already enforces this at UI level;
    // this check guards against programmatic misuse).
    const nonSheetBodies = qSubtraction(unionSurfaces, qBodyType(unionSurfaces, BodyType.SHEET));
    if (!isQueryEmpty(context, nonSheetBodies))
    {
        throw regenError("Union surface bodies must be surface (sheet) bodies, not solid bodies.", ["unionSurfaceBodies"], nonSheetBodies);
    }

    // Restrict to planar faces only.  A sheet body is considered planar when every face it
    // owns has plane geometry.  qGeometry(PLANE) filters to planar faces; any face that does
    // not pass this filter belongs to a non-planar sheet body.
    const allFaces = qOwnedByBody(unionSurfaces, EntityType.FACE);
    const nonPlanarFaces = qSubtraction(allFaces, qGeometry(allFaces, GeometryType.PLANE));
    if (!isQueryEmpty(context, nonPlanarFaces))
    {
        setErrorEntities(context, id, { "entities" : qOwnerBody(nonPlanarFaces) });
        throw regenError("Union surface bodies must be planar. Non-planar surface support (via opWrap) is planned for a future release.", ["unionSurfaceBodies"], qOwnerBody(nonPlanarFaces));
    }

    // Consumed bodies cannot be tagged.
    const consumedBodies = qConsumed(unionSurfaces, Consumed.YES);
    if (!isQueryEmpty(context, consumedBodies))
    {
        throw regenError("Union surface bodies must not be consumed by another feature.", ["unionSurfaceBodies"], consumedBodies);
    }
}

/**
 * Verify that all bodies in subtractBodies are surface (sheet) bodies and not consumed.
 * smTabApply.fs thickens these at apply-time using the target SM model parameters,
 * keeping thickness out of the tool Part Studio entirely.
 *
 * @param context          {Context}
 * @param id               {Id}      Feature id used for error reporting.
 * @param subtractBodies   {Query}   Bodies to validate; must all be BodyType.SHEET.
 * @param parameterName    {string}  Precondition parameter name for error highlighting.
 */
function validateSubtractBodies(context is Context, id is Id, subtractBodies is Query, parameterName is string)
{
    const nonSheetBodies = qSubtraction(subtractBodies, qBodyType(subtractBodies, BodyType.SHEET));
    if (!isQueryEmpty(context, nonSheetBodies))
    {
        throw regenError("Subtraction tool bodies must be surface (sheet) bodies, not solid bodies.", [parameterName], nonSheetBodies);
    }

    const consumedBodies = qConsumed(subtractBodies, Consumed.YES);
    if (!isQueryEmpty(context, consumedBodies))
    {
        throw regenError("Subtraction tool bodies must not be consumed by another feature.", [parameterName], consumedBodies);
    }
}
