FeatureScript 1777;
import(path : "onshape/std/common.fs", version : "1777.0");

/**
 * Attach a list of variables to the context, which can be retrieved by another feature defined later. If a variable of the same name already exists, this function will overwrite it. The variable name will match the key of each item, and may be prepended or appended with a custom string.
 * 
 * @example `setVariables(context, {"foo" : 1, "bar" : 2}, "start_", "_end")` attaches the variables "start_foo_end" with a value of 1 and "start_bar_end" with a value of 2
 * 
 * @param variablesList : A map of variables with the key as the variable name and the value as the variable value.
 * @param perpend : Adds custom text before the variable names.
 * @param append : Adds custom text after the variable names.
 */
export function setVariables(context is Context, variablesList is map, prepend is string, append is string)
{
    for (var key, value in variablesList)
    {
        setVariable(context, prepend ~ key ~ append, value);
    }
}

/**
 * Attach a list of variables to the context, which can be retrieved by another feature defined later. If a variable of the same name already exists, this function will overwrite it. The variable name will match the key of each item, and may be prepended with a custom string.
 * 
 * @example `setVariables(context, {"foo" : 1, "bar" : 2}, "start_")` attaches the variables "start_foo" with a value of 1 and "start_bar" with a value of 2
 * 
 * @param variablesList : A map of variables with the key as the variable name and the value as the variable value.
 * @param perpend : Adds custom text before the variable names.
 */
export function setVariables(context is Context, variablesList is map, prepend is string)
{
    for (var key, value in variablesList)
    {
        setVariable(context, prepend ~ key, value);
    }
}

/**
 * Attach a list of variables to the context, which can be retrieved by another feature defined later. If a variable of the same name already exists, this function will overwrite it. The variable name will match the key of each item, and may be prepended or appended with a custom string.
 * 
 * @example `setVariables(context, {"foo" : 1, "bar" : 2})` attaches the variables "foo" with a value of 1 and "bar" with a value of 2
 * 
 * @param variablesList : A map of variables with the key as the variable name and the value as the variable value.
 */
export function setVariables(context is Context, variablesList is map)
{
    for (var key, value in variablesList)
    {
        setVariable(context, key, value);
    }
}

export function embedVariableList(context is Context, id is Id, variableList is map)
{
    /// using the feature ID as the variable name makes it unique, and the format uses a "[" to start, which won't let it show up in the part studio. Win win.
    setVariable(context, toString(id), variableList);
}

export function mapSize(Map is map) returns number
{
    var i = 0;
    for (var item in Map)
    {
        i += 1;
    }
    return i;
}

export function getLastMapItem(Map is map)
{
    var lastItem;
    for (var item in Map)
    {
        lastItem = item;
    }
    return lastItem;
}


