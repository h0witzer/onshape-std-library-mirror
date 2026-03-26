FeatureScript 2909;
import(path : "onshape/std/common.fs", version : "2909.0");

export const FOLD_ANGLE_BOUNDS = { (degree) : [.001, 90, 120] } as AngleBoundSpec;
export const OFFSET_LENGTH_BOUNDS = { (millimeter) : [0, .3, 500000] } as LengthBoundSpec;
export const THICKNESS_LENGTH_BOUNDS = { (millimeter) : [0, 3, 500000] } as LengthBoundSpec;
export const HINGE_THICKNESS_LENGTH_BOUNDS = { (millimeter) : [0, .5, 500000] } as LengthBoundSpec;
export const HINGE_RADIUS_LENGTH_BOUNDS = { (millimeter) : [0, 1.5, 500000] } as LengthBoundSpec;

export predicate userInput(definition is map)
{
    annotation { "Name" : "4D faces", "Filter" : EntityType.FACE }
    definition.faces is Query;

    annotation { "Group Name" : "Basic data", "Collapsed By Default" : false }
    {
        annotation { "Name" : "Nominal thickness" }
        isLength(definition.nominalThickness, THICKNESS_LENGTH_BOUNDS);

        annotation { "Name" : "Print clearance offset" }
        isLength(definition.printOffset, OFFSET_LENGTH_BOUNDS);
    }

    annotation { "Group Name" : "Living hinge dimensions", "Collapsed By Default" : false }
    {
        annotation { "Name" : "Thickness" }
        isLength(definition.livingHingeThickness, HINGE_THICKNESS_LENGTH_BOUNDS);

        annotation { "Name" : "Outer bend radius" }
        isLength(definition.livingHingeOuterRadius, HINGE_RADIUS_LENGTH_BOUNDS);
    }

    annotation { "Group Name" : "Default fold settings", "Collapsed By Default" : false }
    {
        annotation { "Name" : "Default fold angle" }
        isAngle(definition.defaultFoldAngle, FOLD_ANGLE_BOUNDS);

        annotation { "Name" : "Default fold direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
        definition.defaultFoldDirection is boolean;

        annotation { "Name" : "Show unfolded state", "Default" : false }
        definition.showUnfolded is boolean;
    }

    annotation { "Name" : "Unique fold settings", "Default" : false }
    definition.enableOverrides is boolean;

    if (definition.enableOverrides)
    {
        annotation { "Group Name" : "My Group", "Collapsed By Default" : false, "Driving Parameter" : "enableOverrides" }
        {
            annotation { "Name" : "Overrides", "Item name" : "Override" }
            definition.overrides is array;

            for (var override in definition.overrides)
            {
                annotation { "Name" : "Fold edge", "Filter" : EntityType.EDGE, "MaxNumberOfPicks" : 1 }
                override.edge is Query;

                annotation { "Name" : "Override angle" }
                isAngle(override.foldAngle, FOLD_ANGLE_BOUNDS);

                annotation { "Name" : "Opposite direction", "UIHint" : UIHint.OPPOSITE_DIRECTION }
                override.foldDirection is boolean;
            }
        }
    }
}
