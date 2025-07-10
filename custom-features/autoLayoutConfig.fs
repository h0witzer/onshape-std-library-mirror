FeatureScript 2679;
import(path : "onshape/std/geometry.fs", version : "2679.0");

// This file contains the defaults for thickness, cut sheet width, cut sheet height, and spacing.
// Edit as necessary.
export const DEFAULT_SHEET_LENGTH =
{
            (meter) : [1e-5, 2, 500],
            (centimeter) : 150,
            (millimeter) : 1500,
            (inch) : 96,
            (foot) : 8,
            (yard) : 2
        } as LengthBoundSpec;

export const DEFAULT_SHEET_WIDTH =
{
            (meter) : [1e-5, 1, 500],
            (centimeter) : 100,
            (millimeter) : 1000,
            (inch) : 48,
            (foot) : 4,
            (yard) : 1
        } as LengthBoundSpec;

export const DEFAULT_SPACING =
{
            (meter) : [1e-5, .05, 500],
            (centimeter) : 1,
            (millimeter) : 5,
            (inch) : 0.1,
            (foot) : 0.01,
            (yard) : 0.01
        } as LengthBoundSpec;

export const ROTATION_BOUNDS =
{
            (unitless) : [1, 15, 90],
        } as IntegerBoundSpec;
