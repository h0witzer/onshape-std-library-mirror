# FeatureScript Id and String Concatenation Guide

## Critical Rules for Id Construction

### The Problem
FeatureScript has specific rules for how Ids and strings interact with concatenation operators.

### Key Operators

1. **`~` (tilde)**: String concatenation operator
   - Concatenates two strings
   - Concatenates string with number (converts number to string)
   - Example: `"hello" ~ "world"` → `"helloworld"`
   - Example: `"item" ~ 5` → `"item5"`

2. **`+` (plus)**: Addition/Id concatenation operator  
   - Adds two numbers
   - Adds a string to an Id to create a new Id
   - **CANNOT** add string to number directly
   - Example: `id + "suffix"` → new Id with "suffix" appended
   - Example: `5 + 3` → `8`

### Common Patterns

#### ✅ CORRECT: Adding string literals to Id
```featurescript
const newId = id + "myOperation";
```

#### ✅ CORRECT: Adding string-with-number to Id
```featurescript
const newId = id + "split" + cellIndex;
```
This works because:
1. `id + "split"` creates a new Id
2. That Id + `cellIndex` (number) is valid Id construction

#### ❌ WRONG: Using tilde with Id
```featurescript
const newId = id + "split" ~ cellIndex;  // SYNTAX ERROR
```
The `~` operator doesn't work properly in this context.

#### ❌ WRONG: Trying to add string and number
```featurescript
const newId = id + ("split" + cellIndex);  // ERROR: "Can not add string and number"
```
Inside the parentheses, you're trying to use `+` to concatenate string and number, which fails.

#### ✅ CORRECT: Use tilde inside, plus outside
```featurescript
const newId = id + ("split" ~ cellIndex);
```
This works because:
1. `"split" ~ cellIndex` creates a string "splitN"
2. `id + "splitN"` adds that string to the Id

### Best Practice for Id Construction with Counters

**Recommended pattern:**
```featurescript
const newId = id + "operation" + counter;
```

This is the simplest and most reliable pattern:
- No parentheses needed
- Works with numeric counters
- Clear and readable

**Why it works:**
- FeatureScript allows `Id + string + number` to create Id with concatenated suffix
- The operators are evaluated left-to-right
- `id + "operation"` creates new Id, then adding `counter` appends it

### Examples from Real Code

```featurescript
// Slot generation
const slotId = id + "slot" + slotCounter;
generateSlotForBodyPair(context, slotId, ...);

// Split operations  
opSplitPart(context, id + "split" + cellIndex, {...});
const splitBodies = qCreatedBy(id + "split" + cellIndex, EntityType.BODY);

// Cleanup operations
opDeleteBodies(context, id + "cleanup" + index, {...});
```

### Debugging Id Issues

If you see errors like:
- "Call function(..., string, ...) does not match function(..., Id, ...)"
- "Can not add string and number"

Check your Id construction:
1. Are you using `~` with Ids? → Change to `+`
2. Are you trying to add string + number in parentheses? → Remove parentheses or use `~`
3. Is the pattern `id + "string" + number`? → This is correct

### Summary

**Simple rule of thumb:**
- Use `+` to work with Ids
- Use `~` only when you need to explicitly create a string first
- Pattern `id + "operation" + counter` works reliably for Id construction with counters
