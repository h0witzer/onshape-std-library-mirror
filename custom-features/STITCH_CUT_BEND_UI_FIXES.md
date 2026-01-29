# Stitch Cut Bend Feature - UI and Selection Fixes

## Issues Resolved

### Issue 1: Unwanted Joint Type Dropdown
**Problem:**
- UI showed "Stitch style" dropdown with options: EDGE, BUTT, BUTT2
- User reported: "The UI should not have a drop down for joint type"
- Confusing because feature should work automatically

**Root Cause:**
The precondition included:
```featurescript
annotation { "Name" : "Stitch style", "Default" : SMJointStyle.EDGE }
definition.stitchStyle is SMJointStyle;
```

**Fix:**
- Removed `stitchStyle` parameter from precondition entirely
- Hardcoded to always use `SMJointStyle.EDGE` for rip segments
- Updated attribute creation to use `canBeEdited : false`
- Changed parameter references to "entity" (not user-configurable)

**Code Changes:**
```featurescript
// In applyJointAttributesToSegments():
// OLD: var ripStyle = definition.stitchStyle;
// NEW: var ripStyle = SMJointStyle.EDGE;

// In createNewRipAttribute():
// OLD: "parameterIdInFeature" : "stitchStyle"
// NEW: "parameterIdInFeature" : "entity"
// OLD: "canBeEdited" : true
// NEW: "canBeEdited" : false
```

### Issue 2: Edge Selection Not Working
**Problem:**
- Feature always reported "no sheet metal edge selected"
- Modify Joint works fine with same edges
- User reported: "you've incorrectly copied their pattern"

**Root Cause:**
Stitch cut bend used restrictive filter:
```featurescript
"Filter" : SheetMetalDefinitionEntityType.EDGE && ...
```

While Modify Joint uses:
```featurescript
"Filter" : (SheetMetalDefinitionEntityType.FACE || SheetMetalDefinitionEntityType.EDGE) && ...
```

**Fix:**
Updated filter to match Modify Joint exactly:
```featurescript
annotation { "Name" : "Joint edge",
            "Filter" : (SheetMetalDefinitionEntityType.FACE || SheetMetalDefinitionEntityType.EDGE) && AllowFlattenedGeometry.YES && ModifiableEntityOnly.YES,
            "MaxNumberOfPicks" : 1 }
```

This allows the feature to accept both edge and face selections, matching Modify Joint behavior.

## Final UI Parameters

### User-Visible Parameters:
1. **Joint edge** - Select edge or face joint
2. **Use model bend radius** - Checkbox (default: true)
3. **Bend radius** - Length input (if not using model default)
4. **Use model K Factor** - Checkbox (default: true)
5. **K Factor** - Number input (if not using model default)
6. **Bridge width** - Length input (width of bend segments)
7. **Spacing type** - Dropdown (Equal, Distance, Best Fit)
8. **Instance count** - Number (for Equal/Distance)
9. **Additional spacing parameters** - Varies by spacing type

### Hidden/Automatic Behavior:
- **Bridge joint type**: Always BEND (not shown)
- **Stitch joint type**: Always RIP (not shown)
- **Rip style**: Always EDGE (not shown)

## Pattern Generated

```
Input edge:  |-----------------------------------|

Output:      |BEND|RIP|BEND|RIP|BEND|RIP|BEND|
             Bridge   Bridge   Bridge   Bridge
                  ^Stitch ^Stitch ^Stitch
```

- **Bridges**: BEND attributes (connections between stitches)
- **Stitches**: RIP attributes with EDGE style (cuts/perforations)

## Testing Checklist

- [ ] UI no longer shows "Stitch style" dropdown
- [ ] Edge selection works with sheet metal edges
- [ ] Face selection works with sheet metal faces (if applicable)
- [ ] Bridge segments get BEND attributes
- [ ] Stitch segments get RIP attributes with EDGE style
- [ ] Flat pattern generates correctly
- [ ] Feature behavior matches user expectations

## Comparison with Modify Joint

| Aspect | Modify Joint | Stitch Cut Bend (Fixed) |
|--------|--------------|------------------------|
| Filter | `(FACE \|\| EDGE)` | `(FACE \|\| EDGE)` ✅ |
| Joint type selection | Yes (BEND, RIP, TANGENT) | No (automatic) ✅ |
| Behavior | User chooses target type | Auto: BEND for bridges, RIP for stitches ✅ |

The stitch cut bend feature now follows the same selection pattern as Modify Joint while automatically determining joint types based on the spacing logic.
