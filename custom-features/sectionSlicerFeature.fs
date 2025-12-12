FeatureScript 1803;
import(path : "onshape/std/geometry.fs", version : "1803.0");
export import(path : "d9d9565d9c7076555198d0fe", version : "60e7a384dcb8822bb5daa495");

/*  "Section Slicer" - Custom Feature
    Anthony Lu
    July 2022
    
    Slices a solid body into sections of uniform thickness with rectangular slots for fitting together. The orientation of sliced sections may be adjusted by selecting a coordinate system in the settings, and uses world coordinates by default. The slicer arranges its sections along their respective slicer axes, with its X-axis serving as the reference axis. In two-axis mode, the skew angle of the U-axis (angle with respect to the slicer Y-axis) is adjustable.
    In three-axis mode, the angles between X, U, V axes are fixed to produce a hexagonal pattern between their sections, and restrictions are enforced to avoid regions where sections of all three axes intersect. The U, V axes are fixed to skew angles of 30 deg and -30 deg, respectively. The section space must be greater than twice the section width to give V-axis sections enough clearance.
    The resulting sections are named and numbered according to their axis.
    The convention is to prefix position and direction vectors with lowercase letters 'w' for world coordinates, and 'l' for local (slicer) coordinates.
*/

annotation { "Feature Type Name" : "Section Slicer",
             "Editing Logic Function" : "OnFeatureChange",
             "Manipulator Change Function" : "OnManipulatorChange"
            }
export const myFeature = defineFeature(function(context is Context, id is Id, def is map)
    precondition
    {
        annotation { "Name" : "Target", "Filter" : EntityType.BODY && BodyType.SOLID, "MaxNumberOfPicks" : 1, "Description" : "The target body to slice into sections." }
        def.target is Query;
        annotation { "Name" : "Keep Target", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE, "Description" : "Preserve the unmodified target body in addition to creating sliced sections." }
        def.keepTarget is boolean;

        annotation { "Name" : "Section Width", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE, "Description" : "The width of sliced sections." }
        isLength(def.sectionWidth, SECTION_WIDTH_BOUNDS);
        annotation { "Name" : "Section Space", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE, "Description" : "The spacing between sliced sections." }
        isLength(def.sectionSpace, SECTION_SPACE_BOUNDS);
        annotation { "Name" : "Reverse Slot Direction", "UIHint" : UIHint.REMEMBER_PREVIOUS_VALUE, "Description" : "Reverses the direction of slots cut into each section. By default, the X-axis sections slots oriented 'downwards' (opposite the normal vector of the slicer horizontal plane)." }
        def.reverseSlot is boolean;
        annotation { "Name" : "Horizontal Plane", "Filter" : QueryFilterCompound.ALLOWS_PLANE, "MaxNumberOfPicks" : 1, "Default" : qTopPlane(EntityType.BODY), "Description" : "The sliced sections are oriented normal to the selected horizontal plane. Default is Top Plane." }
        def.hPlane is Query;
        annotation { "Name" : "X-Axis Geometry Reference", "Filter" : QueryFilterCompound.ALLOWS_DIRECTION, "MaxNumberOfPicks" : 1, "Description" : "Define a direction for the slicer X axis. Make adjustments with the X Axis Angle Adjust input. Default is Horizontal Plane's X-axis." }
            def.xAxis_geometry is Query;
        
        annotation { "Group Name" : "X-Axis Sections", "Collapsed By Default" : false }
        {
            annotation { "Name" : "Adjust Angle", "Description" : "Adjust the angle of the slicer X axis."}
            isAngle(def.xAxis_adjustAngle, ANGLE_360_ZERO_DEFAULT_BOUNDS);
            annotation { "Name" : "Section Offset", "Description" : "Adjust the positioning of sliced sections along the slicer X axis." }
            isLength(def.xSection_offset, ZERO_DEFAULT_LENGTH_BOUNDS);
        }

        annotation { "Name" : "U-Axis Sections" }
        def.uAxis_en is boolean;
        if (def.uAxis_en)
        {
            annotation { "Group Name" : "U-Axis Settings", "Driving Parameter" : "uAxis_en", "Collapsed By Default" : false }
            {                
                annotation { "Name" : "Skew Angle", "Description" : "Set the skew angle between the slicer Y and U axes in two-axis mode. A value of zero produces U-axis sections orthogonal to the X-axis sections. This value is fixed to 30 degrees in three-axis mode." }
                isAngle(def.uAxis_skewAngle, SKEW_ANGLE_BOUNDS);
                annotation { "Name" : "Section Offset", "Description" : "Adjust the positioning of sliced sections along the slicer U axis." }
                isLength(def.uSection_offset, ZERO_DEFAULT_LENGTH_BOUNDS);
            }
            annotation { "Name" : "V-Axis Sections" }
            def.vAxis_en is boolean;
        }
        if (def.vAxis_en)
        {
            annotation { "Group Name" : "V-Axis Settings", "Driving Parameter" : "vAxis_en", "Collapsed By Default" : false }
            {
                annotation { "Name" : "Section Offset", "Description" : "Adjust the positioning of sliced sections along the slicer V axis. Values are restricted to avoid regions where sections of all three axes intersect." }
                isLength(def.vSection_offset, ZERO_DEFAULT_LENGTH_BOUNDS);
            }
        }
        
        annotation { "Group Name" : "Debug View" }
        {
            annotation { "Name" : "Coord System" }
            def.debug_coordSystem is boolean;
            annotation { "Name" : "Bounding Box" }
            def.debug_boundingBox is boolean;
            annotation { "Name" : "Section Points" }
            def.debug_sectionPoints is boolean;
            annotation { "Name" : "X-Axis Sections" }
            def.debug_xSections is boolean;
            if (def.uAxis_en)
            {
                annotation { "Name" : "U-Axis Sections" }
                def.debug_uSections is boolean;
            }
            if (def.vAxis_en)
            {
                annotation { "Name" : "V-Axis Sections" }
                def.debug_vSections is boolean;
            }
            annotation { "Name" : "Console Output" }
            def.debug_consoleOutput is boolean;
        }
    }
    {
        SectionSlicer_Main(context, id, def);
    });
