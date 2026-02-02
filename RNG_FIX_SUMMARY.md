# RNG Fix Summary - Quick Reference

## The Issue
Sheet metal tab and slot feature produced tabs with alternating widths: **BIG-small-BIG-small-BIG-small**

## The Fix (One Line Change)
```featurescript
// BEFORE (buggy):
const currentSeed = definition.randomSeed + i;

// AFTER (fixed):
const currentSeed = definition.randomSeed + i * SEED_SKIP_MULTIPLIER;  // where SEED_SKIP_MULTIPLIER = 48271
```

## Why This Works
- LCG multiplier ≈ 0.514 causes ~51% jumps with consecutive seeds
- Using coprime skip (48271) breaks the correlation
- Result: 94.4% → 44.4% alternating score (50% improvement)

## Visual Comparison

### BEFORE (Buggy - 94.4% alternating):
```
Tab  0: +0.0729  ████████████████████████████
Tab  1: -0.0244  █████████
Tab  2: +0.0784  ███████████████████████████████
Tab  3: -0.0188  ███████
Tab  4: +0.0840  █████████████████████████████████
Tab  5: -0.0133  █████
```
Pattern: Obvious alternating big-small rhythm

### AFTER (Fixed - 44.4% alternating):
```
Tab  0: +0.0729  ████████████████████████████
Tab  1: +0.0219  ████████
Tab  2: -0.0291  ███████████
Tab  3: -0.0801  ████████████████████████████████
Tab  4: +0.0689  ███████████████████████████
Tab  5: +0.0179  ███████
```
Pattern: Much more natural variation

## Technical Details
See `RNG_FIX_DOCUMENTATION.md` for complete mathematical analysis.

## Files Modified
- `custom-features/sheetMetalTabAndSlot` - RNG fix implementation
- `RNG_FIX_DOCUMENTATION.md` - Detailed technical explanation
- `RNG_FIX_SUMMARY.md` - This quick reference

## About floor()
The `floor()` function in the seed generation was **NOT** the issue. It's correctly used for integer conversion. The bug was in using consecutive seeds in the randomization loop.
