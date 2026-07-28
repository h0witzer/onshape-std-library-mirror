FeatureScript 2679;
import(path : "onshape/std/geometry.fs", version : "2679.0");

export type AutoLayoutAttribute typecheck canBeAutoLayoutAttribute;

export predicate canBeAutoLayoutAttribute(value)
{
    value is string;
    value == "AutoLayout_PLACED";
}

// Marker attribute stamped by the "Set Grain Direction" feature. It is placed (as a legacy
// unnamed / typed attribute, matching AutoLayoutAttribute) on both the arrow sigil's long
// shaft edge and the arrow island face. Auto Layout+ only ever reads it off the EDGE and
// derives the live grain axis from that edge's geometry, so no direction is stored here -
// the attribute is a pure presence marker that rides along through moves and pattern copies.
export type GrainDirectionAttribute typecheck canBeGrainDirectionAttribute;

export predicate canBeGrainDirectionAttribute(value)
{
    value is string;
    value == "GrainDirection";
}

// NOTE: SheetGrainAxis is intentionally defined in autoLayout.fs, not here. An enum used as a
// precondition parameter type must be exported from the feature's own file; a cross-tab import
// of it fails with "Enum used as parameter type must be exported".
