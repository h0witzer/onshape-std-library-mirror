# Contributor Guide

## Dev Environment Tips
- All functions in this github are a mirror of the Onshape Standard Library functions with version numbers stripped from the imports
- Look at the Onshape Standard Library documentation at https://cad.onshape.com/FsDoc/library.html for function applications, expected inputs and outputs, and general reference
- Browse https://cad.onshape.com/FsDoc/ for general Featurescript knowledge and in particular lexical reference
- Pay strong attention to the values and types used in Featurescript, there are many differences from other C-like languages that are optimized for parametric CAD to be aware of https://cad.onshape.com/FsDoc/variables.html
- These .fs files are not F Sharp or Javascript, Featurescript is a custom language developed for Onshape
- Naming convention for the features we are working with should be more explicit and less shorthand. Match the level of readability seen in the Onshape Standard Library functions

## Testing Instructions
- Since there is no way to run Onshape in a localized environment here we will rely mostly on comparing code samples with existing functions in the standard library and against the reference docs to ensure consistency with the code base
- Debugging will be done largely via reports delivered via console log
