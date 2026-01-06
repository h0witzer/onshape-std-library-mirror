# Plane-Based FFD Visual Guide

## Concept Overview

### Standard FFD (Point-Based)
```
Traditional FFD with 3×3×3 lattice (27 control points):

     •---•---•
    /|  /|  /|
   • • • • • •
  /|/|/|/|/|/|
 •-•-•-•-•-•-•
 | | | | | | |
 •-•-•-•-•-•-•
 | | | | | | |
 •-•-•-•-•-•-•
```

Each • is a control point that can be moved individually.
With 8 spans in each direction, you'd have 729 control points!

### Plane-Based FFD (This Implementation)
```
Plane-based FFD with U_DIRECTION, 5 planes:

Plane 4 (top)     •---•
                 /   /|
Plane 3         •---• |
               /   /| •
Plane 2       •---• |/
(middle)     /   /| •
Plane 1     •---• |/
           /   /| •
Plane 0   •---• |/
(bottom)      •/

Each plane has 4 control points (2×2 grid)
Total: 5 planes × 4 points = 20 control points
All 4 points on a plane move together!
```

## Direction Mapping

### S_DIRECTION (X-axis)
```
Planes perpendicular to X-axis:

    Plane 0   Plane 1   Plane 2
        |         |         |
        v         v         v
        •---•     •---•     •---•
        |   |     |   |     |   |
        •---•     •---•     •---•
        
View from above (looking down Z-axis)
Planes stacked along X-axis
```

### T_DIRECTION (Y-axis)
```
Planes perpendicular to Y-axis:

        Plane 2
           •---•
        Plane 1
           •---•
        Plane 0
           •---•
           
View from side (looking along X-axis)
Planes stacked along Y-axis
```

### U_DIRECTION (Z-axis)
```
Planes perpendicular to Z-axis:

    Plane 2    •---•  (top)
                  
    Plane 1    •---•  (middle)
                  
    Plane 0    •---•  (bottom)
    
View from front (looking along Y-axis)
Planes stacked along Z-axis
```

## Transformation Examples

### Translation
```
Before:                After translating Plane 1 to the right:

Plane 2  •---•         Plane 2  •---•
         |   |                  |   |
         •---•                  •---•
                                    
Plane 1  •---•         Plane 1      •---•
         |   |                      |   |
         •---•                      •---•
                                    
Plane 0  •---•         Plane 0  •---•
         |   |                  |   |
         •---•                  •---•

All 4 points on Plane 1 moved together →
```

### Rotation
```
Before:                After rotating Plane 2 around Z-axis:

Plane 2  •---•         Plane 2    •
         |   |                    /|
         •---•                   • •
                                  \|
                                   •
                                   
Plane 1  •---•         Plane 1  •---•
         |   |                  |   |
         •---•                  •---•

Plane 2 rotated 45° around its center
All 4 corner points rotated together
```

### Combined (Taper)
```
Before (straight):     After (tapered):

Plane 3  •---•         Plane 3    •-•
         |   |                    | |
         •---•                    •-•
                                  
Plane 2  •---•         Plane 2  •---•
         |   |                  |   |
         •---•                  •---•
                                  
Plane 1  •---•         Plane 1  •---•
         |   |                  |   |
         •---•                  •---•
                                  
Plane 0  •---•         Plane 0  •---•
         |   |                  |   |
         •---•                  •---•

Top plane scaled inward → tapered shape
```

## Workflow Diagram

```
┌─────────────────────────────────────────────────────────┐
│ 1. Select Surface(s)                                    │
│    - Choose one or more NURBS surfaces to deform       │
└────────────────┬────────────────────────────────────────┘
                 │
                 v
┌─────────────────────────────────────────────────────────┐
│ 2. Choose Manipulation Direction                       │
│    ┌─────────────────────────────────────────────┐    │
│    │  ○ S Direction (X-axis)                     │    │
│    │  ○ T Direction (Y-axis)                     │    │
│    │  ● U Direction (Z-axis)  [selected]         │    │
│    └─────────────────────────────────────────────┘    │
└────────────────┬────────────────────────────────────────┘
                 │
                 v
┌─────────────────────────────────────────────────────────┐
│ 3. Set Number of Planes                                │
│    Planes: [  3  ]  (2 to 12)                          │
│    = 2 spans in U direction                            │
└────────────────┬────────────────────────────────────────┘
                 │
                 v
┌─────────────────────────────────────────────────────────┐
│ 4. Enable "Edit Planes" ☑                              │
│    - Plane centers become selectable                   │
└────────────────┬────────────────────────────────────────┘
                 │
                 v
┌─────────────────────────────────────────────────────────┐
│ 5. Select a Plane                                      │
│    Click on green plane center point:                  │
│                                                         │
│         •  ← Plane 2 (top)                             │
│                                                         │
│         ○  ← Plane 1 (middle) [SELECTED]               │
│                                                         │
│         •  ← Plane 0 (bottom)                          │
└────────────────┬────────────────────────────────────────┘
                 │
                 v
┌─────────────────────────────────────────────────────────┐
│ 6. Manipulate Plane                                    │
│    ┌────────────────────────────────────────┐         │
│    │  Drag triad to translate:              │         │
│    │         ↑ Z                             │         │
│    │         |                               │         │
│    │    Y ←─┼─→ X                            │         │
│    │         |                               │         │
│    └────────────────────────────────────────┘         │
│    OR adjust in parameter panel:                      │
│    Translation X: [  0 mm  ]                          │
│    Translation Y: [ 10 mm  ] ← moved up              │
│    Translation Z: [  0 mm  ]                          │
│    Rotation:      [  0 deg ]                          │
└────────────────┬────────────────────────────────────────┘
                 │
                 v
┌─────────────────────────────────────────────────────────┐
│ 7. Result                                              │
│    - All 4 control points on Plane 1 moved up 10mm    │
│    - Surface smoothly deforms following the lattice   │
│    - Can repeat for other planes                      │
└─────────────────────────────────────────────────────────┘
```

## Under the Hood: How It Works

### Step-by-Step Process

```
1. INPUT SURFACES
   ┌──────┐  ┌──────┐
   │ Surf │  │ Surf │
   │  1   │  │  2   │
   └──────┘  └──────┘

2. EXTRACT B-SPLINE CONTROL POINTS
   • • • •    • • • •
   • • • •    • • • •
   • • • •    • • • •

3. COMPUTE UNIFIED BOUNDING BOX
   ┌────────────────┐
   │  • • • • • • • │
   │  • • • • • • • │
   │  • • • • • • • │
   └────────────────┘

4. BUILD FFD LATTICE (U_DIRECTION, 3 planes)
   
   Plane 2: •──•  (top)
            │  │
            •──•
   
   Plane 1: •──•  (middle)
            │  │
            •──•
   
   Plane 0: •──•  (bottom)
            │  │
            •──•

5. APPLY PLANE TRANSFORMATIONS
   
   User moves Plane 1 → right
   
   Plane 2: •──•        •──•
            │  │        │  │
            •──•        •──•
   
   Plane 1: •──•    →      •──•  (moved)
            │  │            │  │
            •──•            •──•
   
   Plane 0: •──•        •──•
            │  │        │  │
            •──•        •──•

6. DEFORM SURFACES
   
   For each surface control point P:
   
   a) Convert P to parametric coordinates (s,t,u)
      in lattice space
   
   b) Evaluate trivariate Bernstein polynomial:
      P' = Σ B_i,l(s) * B_j,m(t) * B_k,n(u) * P_ijk
      where P_ijk are the (transformed) lattice points
   
   c) Result is deformed point P'

7. CREATE OUTPUT SURFACES
   ┌─────╱┐  ┌─────╱┐
   │ Surf│  │ Surf│
   │  1' │  │  2' │
   └─────┘  └─────┘
   (deformed)
```

## Key Advantages Visualized

### Indexing Stability

**Old FFD (point-based):**
```
3×3×3 lattice = 27 points

Point [1,1,1] is at index: 1×9 + 1×3 + 1 = 13

Change to 4×3×3 lattice = 36 points

Point [1,1,1] is now at index: 1×9 + 1×3 + 1 = 13 (still same!)
But point [2,1,1] is at: 2×9 + 1×3 + 1 = 22
Previously [2,1,1] was at: 2×9 + 1×3 + 1 = 22 (same!)

Actually stable in this case, but complex to track!
```

**New FFD (plane-based):**
```
3 planes in U direction

Plane 0 = bottom
Plane 1 = middle  
Plane 2 = top

Change to 5 planes:

Plane 0 = bottom  (same!)
Plane 1 = ...
Plane 2 = middle  (was 1)
Plane 3 = ...
Plane 4 = top     (was 2)

Simple plane numbering, easy to understand!
```

## Common Use Cases

### 1. Taper
```
┌────┐     ┌───┐
│    │  →  │   │
│    │     │   │
│    │     │   │
└────┘     └───┘

Direction: U (vertical)
Planes: 4
Transform top plane: scale inward
```

### 2. Twist
```
┌────┐     ╱────╲
│    │  →  │    │
│    │     ╲────╱
└────┘     └────┘

Direction: U (vertical)
Planes: 6
Transform planes: progressive rotation
```

### 3. Bend
```
│    │     ╱
│    │  →  │
│    │     ╲

Direction: S (horizontal)
Planes: 5
Transform middle planes: sideways translation
```

### 4. Wave
```
────────    ∿∿∿∿∿∿∿

Direction: S (along length)
Planes: 8
Transform planes: alternating up/down translation
```

This visual guide demonstrates how the plane-based approach simplifies
FFD manipulation while maintaining mathematical rigor!
