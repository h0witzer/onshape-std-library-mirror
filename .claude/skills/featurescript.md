# FeatureScript Development Skill

You are working with FeatureScript code in the Onshape Standard Library mirror. Follow these critical rules from AGENTS.md:

## Core Environment Facts
- **Current Onshape Standard Library version: 2780**
- Replace all ✨ with `2780` in headers: `FeatureScript 2780;`
- Replace version stars in imports: `import(path : "onshape/std/feature.fs", version : "2780.0");`
- This is **FeatureScript** (Onshape's custom CAD language), NOT F# or JavaScript
- Reference documentation at https://cad.onshape.com/FsDoc/library.html

## Critical Language Constraints
1. **NO FUNCTION NESTING** - Cannot declare functions inside other functions
2. **NO BITSHIFTING/BITMASKING** - These operations don't exist in FeatureScript
3. **Feature definition FIRST** - Put helper functions BELOW the feature definition
4. **Type system is unique** - Study https://cad.onshape.com/FsDoc/variables.html for FeatureScript-specific types and values

## Function Usage Rules
- **VERIFY EVERY `op*` or `ev*` FUNCTION** against the mirrored library (search `geomOperations.fs` or `evaluate.fs`)
- Do NOT add code referencing functions that don't exist in this repository or official docs
- Non-standard functions from custom features folder MUST be noted explicitly in the header
- Pay attention to modern adjacency types when using `qAdjacent`

## Code Quality Standards
1. **Explicit naming** - NO abbreviations like "ctrl" - use descriptive names like "evaluatedSurfaceControlPoints"
2. **Match Standard Library readability** - Follow naming conventions from official Onshape functions
3. **Add clear comments** - Explain function purpose, inputs, and outputs above each function
4. **Comment functional blocks** - Help with debugging via console logs

## Design Principles
- **Generalize solutions** - Don't assume planar constraints unless explicitly required
- Support cylinders, cones, and complex geometry when possible, not just trivial cases
- Prioritize solutions that work beyond the simplest scenarios

## Testing Approach
- Compare code against existing Standard Library functions
- Verify against reference docs for consistency
- Use console logs and comments for debugging
- No local Onshape environment available

## Warnings
- Ignore features marked "Under development, not for general use" - they're non-functional examples
- Custom features are non-standard and need explicit attribution

---

When responding to tasks involving FeatureScript:
1. Verify functions exist in the repository FIRST
2. Use explicit, readable naming
3. Put feature definition at top, helpers below
4. Add comprehensive comments
5. Generalize solutions beyond trivial cases
6. Check language constraints (no nesting, no bitshift)
7. Use version 2780 consistently
