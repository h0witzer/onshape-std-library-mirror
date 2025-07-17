# Contributor Guide

## Dev Environment Tips
- All functions in this github are a mirror of the Onshape Standard Library functions with version numbers stripped from the imports
- The current version number of the Onshape standard library is 2695, replace the stars in the header with this
- For example "FeatureScript ✨;" should become "FeatureScript 2695;" and "import(path : "onshape/std/feature.fs", version : "✨");" should become "import(path : "onshape/std/feature.fs", version : "2695.0");"
- Look at the Onshape Standard Library documentation at https://cad.onshape.com/FsDoc/library.html for function applications, expected inputs and outputs, and general reference
- Browse https://cad.onshape.com/FsDoc/ for general Featurescript knowledge and in particular lexical reference
- Pay strong attention to the values and types used in Featurescript, there are many differences from other C-like languages that are optimized for parametric CAD to be aware of https://cad.onshape.com/FsDoc/variables.html
- These .fs files are not F Sharp or Javascript, Featurescript is a custom language developed for Onshape
- Naming convention for the features we are working with should be more explicit and less shorthand. Match the level of readability seen in the Onshape Standard Library functions
- Don't use abbreviated naming convention for functions or counters, I can't read that shit, name things with clarity and relation to application like the Standard Library does. We can afford the extra vowels, we don't need to name variables "ctrl" when "evalutatedSurfaceControlPoints" is way more descriptive of what that thing is.
- Function nesting is not a thing in featurescript
- Put the functions below the feature definition, I hate having to scroll to find my feature
- Bitshifting and bitmasking operations are not a thing in featurescript

## Testing Instructions
- Since there is no way to run Onshape in a localized environment here we will rely mostly on comparing code samples with existing functions in the standard library and against the reference docs to ensure consistency with the code base
- Debugging will be done largely via reports delivered via console log
- Leave comments for functional blocks of code to help track down errors
