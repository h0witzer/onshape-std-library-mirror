# Contributor Guide

## Dev Environment Tips
- All functions in this github are a mirror of the Onshape Standard Library functions with version numbers stripped from the imports
- The current version number of the Onshape standard library is 2780, replace the stars in the header with this
- For example "FeatureScript ✨;" should become "FeatureScript 2780;" and "import(path : "onshape/std/feature.fs", version : "✨");" should become "import(path : "onshape/std/feature.fs", version : "2780.0");"
- Look at the Onshape Standard Library documentation at https://cad.onshape.com/FsDoc/library.html for function applications, expected inputs and outputs, and general reference
- Verify every `op*` or `ev*` function against the mirrored library (for example by searching `geomOperations.fs` or `evaluate.fs`) before using it, and to avoid adding code that references functions that cannot be found in this repository or the official documentation
- Functions that exist in the custom features folder are by definition non-standard and will need to be noted explicitly where these references are being pulled from in the header of the code when they are used
- Browse https://cad.onshape.com/FsDoc/ for general Featurescript knowledge and in particular lexical reference
- Pay strong attention to the values and types used in Featurescript, there are many differences from other C-like languages that are optimized for parametric CAD to be aware of https://cad.onshape.com/FsDoc/variables.html
- These .fs files are not F Sharp or Javascript, Featurescript is a custom language developed for Onshape
- Naming convention for the features we are working with should be more explicit and less shorthand. Match the level of readability seen in the Onshape Standard Library functions
- Don't use abbreviated naming convention for functions or counters, I can't read that shit, name things with clarity and relation to application like the Standard Library does. We can afford the extra vowels, we don't need to name variables "ctrl" when "evalutatedSurfaceControlPoints" is way more descriptive of what that thing is.
- Function nesting is not a thing in featurescript. It isn't possible to declare a function inside of the body of another function.
- Put the functions below the feature definition, I hate having to scroll to find my feature
- Bitshifting and bitmasking operations are not a thing in featurescript
- Pay attention to the way qAdjacent works with the modern adjacency types when used
- If you encounter a feature labeled "Under development, not for general use" in the header assume that the code is non-functional and does not represent valid featurescript development practice
- Add clear comments above functions explaining their function and intended purpose and defining inputs and outputs, make it easy for me to read what blocks of code perform a job and what that job is, listing the fields and supported data types here is immensely helpful
- Prioritize solutions to problems that apply to more than the most trivial cases, for example when working on a feature that interacts with surface geometry don't assume a planar constraint unless it's explicitly clear that the function will only be called on planar geometry when another solution exists that would generalize to cylinders and cones

## Testing Instructions
- Since there is no way to run Onshape in a localized environment here we will rely mostly on comparing code samples with existing functions in the standard library and against the reference docs to ensure consistency with the code base
- Debugging will be done largely via reports delivered via console log
- Leave comments for functional blocks of code to help track down errors
