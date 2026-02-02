# RNG Fix Summary - Quick Reference

## The Issue
Sheet metal tab and slot feature produced unnatural patterns in tab widths.

## Evolution of Solutions

### ORIGINAL (seed + i) - WRONG ❌
```featurescript
const currentSeed = randomSeed + i;
```
**Problem**: Big-small-big-small alternating (94.4% alternating score)

### FIRST FIX (seed + i * 48271) - STILL WRONG ❌
```featurescript
const currentSeed = randomSeed + i * 48271;
```
**Problem**: Created cyclic "rainbow" pattern (4-step cycle repeating)

### CORRECT FIX (Chained LCG) - RIGHT ✅
```featurescript
var currentSeed = randomSeed;
for (...) {
    const lcgOutput = pseudoRandomNumber(currentSeed);
    const randomValue = remap(lcgOutput, 0, M, min, max);
    currentSeed = lcgOutput;  // Chain: output becomes next seed
}
```
**Why**: This is the **intended application of LCG RNG methods**

## Visual Comparison

### ORIGINAL (Alternating):
```
Tab  0: BIG+
Tab  1: SMALL-
Tab  2: BIG+
Tab  3: SMALL-
Tab  4: BIG+
Tab  5: SMALL-
```
Pattern: Obvious rhythmic alternation

### COPRIME SKIP (Rainbow Cycle):
```
Tab  0: BIG+
Tab  1: SMALL+
Tab  2: SMALL-
Tab  3: BIG-
Tab  4: BIG+      ← Cycle repeats every 4
Tab  5: SMALL+
Tab  6: SMALL-
Tab  7: BIG-
```
Pattern: Predictable 4-step cycle

### CHAINED LCG (Natural):
```
Tab  0: BIG+
Tab  1: MED-
Tab  2: BIG-
Tab  3: MED-
Tab  4: BIG+
Tab  5: MED+
Tab  6: BIG-
Tab  7: SMALL+
```
Pattern: Natural variation, no obvious cycle

## Key Insight

**Chaining is the intended application of all LCG RNG methods.**

In RNG theory, Linear Congruential Generators are designed to be used as a sequence where each output becomes the next seed. Using consecutive seeds or skip patterns violates this design and creates unintended correlations or cycles.

## Technical Details
See `RNG_FIX_DOCUMENTATION.md` for complete analysis.

## Files Modified
- `custom-features/sheetMetalTabAndSlot` - Proper chained LCG implementation
- `RNG_FIX_DOCUMENTATION.md` - Updated technical explanation
- `RNG_FIX_SUMMARY.md` - This quick reference
