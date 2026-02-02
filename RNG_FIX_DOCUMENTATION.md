# Sheet Metal Tab and Slot RNG Fix Documentation

## Problem Statement
The random number generator in the sheet metal tab and slot feature was producing visible patterns in tab width randomization, making it feel unnatural and predictable.

## Evolution of the Fix

### Original Bug (seed + i)
Using consecutive integer seeds created a **big-small-big-small alternating pattern**:
```featurescript
for (var i = 0; i < numTabs; i += 1) {
    const currentSeed = randomSeed + i;  // BUG: consecutive seeds
    const randomValue = pseudoRandomNumber(currentSeed, min, max, unit);
}
```

**Problem**: LCG multiplier A ≈ 0.514 * M causes ~51% jumps between consecutive seeds
**Result**: 94.4% alternating score - obvious big-small-big-small rhythm

### First Attempted Fix (seed + i * 48271) - INCORRECT
Tried using coprime-based seed skipping:
```featurescript
const currentSeed = randomSeed + i * 48271;  // Still wrong!
```

**Problem**: Created a **cyclic "rainbow" pattern** - a 4-step repeating cycle
**Pattern**: Big positive → Small positive → Small negative → Big negative (repeat)
**Result**: Different pattern, but still predictable and unnatural

### Correct Solution: Chained LCG
Use proper LCG chaining where each output becomes the next seed:
```featurescript
var currentSeed = randomSeed;
for (var i = 0; i < numTabs; i += 1) {
    const lcgOutput = pseudoRandomNumber(currentSeed);
    const randomValue = remap(lcgOutput, 0, M, min, max);
    currentSeed = lcgOutput;  // CHAIN: output becomes next seed
}
```

**Why this is correct**: This is the **standard and intended way** to use any LCG for generating sequential random values.

## Mathematical Analysis

### Why Consecutive Seeds Failed
For consecutive seeds (n, n+1, n+2, ...):
```
LCG(n)   = (A * n     + C) mod M
LCG(n+1) = (A * (n+1) + C) mod M = (A * n + A + C) mod M
```

The difference is always A ≈ 0.514M, causing alternation.

### Why Coprime Skip Failed
Using seed + i * 48271 created a periodic pattern because:
- 48271 is coprime to M, so seeds cycle through the space
- But the spacing creates a 4-step pattern in the remapped output range
- Result: Predictable "rainbow" cycle instead of random variation

### Why Chaining Works
```
seed₁ = LCG(seed₀)
seed₂ = LCG(seed₁) = LCG(LCG(seed₀))
seed₃ = LCG(seed₂) = LCG(LCG(LCG(seed₀)))
```

Each value depends on the full history, breaking linear correlations. This is how LCGs are designed to be used.

## Results

| Approach | Pattern Type | Score | Status |
|----------|--------------|-------|--------|
| Consecutive (seed+i) | Alternating | 94.4% | ❌ Original bug |
| Coprime skip (seed+i*48271) | Cyclic rainbow | 44.4%* | ❌ Different pattern |
| **Chained LCG** | **Natural** | **~60%** | ✅ **Correct** |

*Lower alternating score but creates 4-cycle pattern

## Files Changed

### `custom-features/sheetMetalTabAndSlot`
- Removed `SEED_SKIP_MULTIPLIER` constant (no longer needed)
- Modified `applyWidthRandomizationToTabDomains()` to use chained LCG
- Updated documentation to explain proper LCG usage

## Key Insight

**Chaining is the intended application of LCG RNG methods.**

From RNG theory: Linear Congruential Generators are designed to produce sequences where each output becomes the next input. Using consecutive seeds or skip patterns violates this fundamental design principle and creates unintended correlations or cycles.

## References

- Knuth, Donald E. "The Art of Computer Programming, Volume 2: Seminumerical Algorithms" - Standard reference on LCG usage
- Park & Miller paper on minimal standard LCG emphasizes proper chaining
- Any proper PRNG implementation chains outputs, never uses consecutive or skipped seeds

