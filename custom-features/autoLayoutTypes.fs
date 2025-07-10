FeatureScript 2679;
import(path : "onshape/std/geometry.fs", version : "2679.0");

export type AutoLayoutAttribute typecheck canBeAutoLayoutAttribute;

export predicate canBeAutoLayoutAttribute(value)
{
    value is string;
    value == "AutoLayout_PLACED";
}
