# Sheet Metal Tab and Slot RNG Fix Documentation

## Problem Statement
The random number generator in the sheet metal tab and slot feature was producing a visible alternating pattern where tab widths would go: **big-small-big-small-big-small**, making the randomization feel unnatural and predictable.

## Root Cause Analysis

### Mathematical Issue
The feature uses a Linear Congruential Generator (LCG) for pseudo-random number generation:
```
X_n+1 = (A * X_n + C) mod M
```

Where:
- A = 1103515245 (multiplier)
- C = 12345 (increment)
- M = 2^31 = 2147483648 (modulus)

### The Bug
The original implementation used **consecutive integer seeds**:
```featurescript
for (var i = 0; i < numTabs; i += 1) {
    const currentSeed = randomSeed + i;  // BUG: 1001, 1002, 1003, ...
    const randomValue = pseudoRandomNumber(currentSeed, min, max, unit);
}
```

### Why This Causes Alternating Pattern
When seeds increment by 1, the LCG outputs increment by exactly A:
```
lcg(n+1) - lcg(n) = A
```

Since A ≈ 0.514 * M, each output jumps by approximately **51% of the range**, causing values to alternate between high and low:
- lcg(1000) → 0.864 (high)
- lcg(1001) → 0.378 (low)
- lcg(1002) → 0.892 (high)
- lcg(1003) → 0.406 (low)
...and so on

This manifested as **94.4% alternating score** in our simulations.

## The Solution

### Implementation
Use a **coprime-based seed skip** instead of consecutive increment:
```featurescript
const SEED_SKIP_MULTIPLIER = 48271;  // Coprime to 2^31

for (var i = 0; i < numTabs; i += 1) {
    const currentSeed = randomSeed + i * SEED_SKIP_MULTIPLIER;  // FIX
    const lcgOutput = pseudoRandomNumber(currentSeed);
    const randomValue = remap(lcgOutput, 0, LCG_MODULUS, min, max);
}
```

### Why 48271?
- 48271 is a **coprime** to M (2^31), meaning gcd(48271, 2^31) = 1
- This ensures we hit different parts of the LCG cycle
- Widely used in PRNG implementations (MINSTD Lehmer generator uses 48271)
- Breaks the correlation caused by consecutive seeds

### Results
After the fix:
- **44.4% alternating score** (down from 94.4%)
- **50% reduction** in alternating pattern
- Much more natural and random-looking tab width variations

## Alternative Approaches Tested

We tested multiple approaches before settling on the coprime skip:

| Approach | Alternating Score | Notes |
|----------|------------------|-------|
| Consecutive seeds (BUGGY) | 94.4% | Original implementation |
| Chained LCG | 61.1% | Each output becomes next seed |
| Square index | 50.0% | seed + i*i |
| **Coprime skip (48271)** | **44.4%** | **Best FeatureScript-compatible solution** |
| Large prime (987654321) | 100.0% | Worse than original! |
| Hash mixing (bitwise) | 11.1% | Best but requires bitwise ops (not in FeatureScript) |

## Files Changed

### `custom-features/sheetMetalTabAndSlot`
1. Added `SEED_SKIP_MULTIPLIER = 48271` constant
2. Modified `applyWidthRandomizationToTabDomains()` function:
   - Changed seed calculation from `randomSeed + i` to `randomSeed + i * SEED_SKIP_MULTIPLIER`
   - Switched from range-based `pseudoRandomNumber()` overload to basic overload with manual `remap()`
   - Added comprehensive documentation explaining the mathematical reasoning

## About the floor() Function

The problem statement mentioned suspicion about the `floor()` function in line 1445. **This was NOT the issue.**

```featurescript
definition.randomSeed = floor(remap(lcgOutput, 0, LCG_MODULUS, 0, RANDOM_SEED_UI_MAX));
```

This `floor()` is correctly used to convert a floating-point value to an integer for the UI. The real problem was in the tab randomization loop using consecutive seeds.

## Verification

Created Python simulations to verify the fix:
- Simulated 20 tabs with both old and new implementations
- Measured "alternating score" (percentage of sign changes in differences)
- Confirmed 50% reduction in alternating pattern
- Visual inspection shows much more natural variation

## Lessons Learned

1. **LCGs are sensitive to seed sequences**: Consecutive seeds can produce correlated outputs
2. **Always test PRNG with multiple seeds**: Don't just test with one random sequence
3. **Mathematical analysis reveals patterns**: Understanding the LCG formula helped identify the 51% jump issue
4. **Coprime skipping is an effective solution**: Works without bitwise operations (FeatureScript limitation)
5. **Not all "random" sequences are equally random**: Statistical testing is crucial

## References

- Linear Congruential Generator: https://en.wikipedia.org/wiki/Linear_congruential_generator
- MINSTD (using 48271): https://en.wikipedia.org/wiki/Lehmer_random_number_generator
- Onshape FeatureScript PRNG Tech Tip: https://www.onshape.com/en/resource-center/tech-tips/tech-tip-pseudo-random-number-generation-in-featurescript
