FeatureScript 1447;
import(path : "onshape/std/geometry.fs", version : "1447.0");
import(path : "0bb13c1b6ed6d4a6dd75cf99/fc71c5f156dd1f3e303da1d4/1ec63e5aed89d809a384210e", version : "a9259b0fa59c2451bab47115");//curveTransformUtils.fs


annotation { "Feature Type Name" : "Evolute" }
export const opCurveEvolute = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Curve", "Filter" : EntityType.EDGE }
        definition.curve is Query;

    }
    {
        const trFunction = function(arg)
            {
                return evCurvatureCenter(context, arg, definition);
            };

        opTransformCurve3d(context, id + "transformCurve3d", {
                    "edges" : definition.curve,
                    "transformFunction" : trFunction,
                    "lengthStep " : 0.1 * millimeter
                });
    });

function evCurvatureCenter(context is Context, arg is map, definition is map) returns Vector
{
    const curvResult = evEdgeCurvature(context, {
                "edge" : arg.edge,
                "parameter" : arg.parameter
            });

    const curveNormal = curvResult.frame.xAxis;
    const curveRadius = 1 / curvResult.curvature;

    debug(context, arg.point + curveNormal * curveRadius);

    return arg.point + curveNormal * curveRadius;
}
