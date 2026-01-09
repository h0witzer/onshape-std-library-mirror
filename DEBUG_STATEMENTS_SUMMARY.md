# Debug Statements Added to Sheet Metal Tab Modified Feature

## Overview
Comprehensive debug and diagnostic statements have been added to `custom-features/sheetMetalTabModified.fs` to help diagnose direction solving bugs and track the flow of tab geometry placement.

## Import Added
- Added `import(path : "onshape/std/debug.fs", version : "2837.0");` to enable debug visualization and println functionality

## Debug Sections Added

### 1. Main Feature Function (sheetMetalTab)
**Location:** Lines 52-143
**Debug Information:**
- Feature start/end markers with separator lines
- Tool creation initiation
- Union entities count and visualization (blue color)
- Union bodies count
- Error tracking for missing union entities
- Subtract bodies count
- Model partition count
- Processing status for each model partition
- Success/failure status of tab application
- Temporary body cleanup tracking
- Sheet metal attribute assignment tracking
- Geometry update tracking

### 2. Tool Creation Function (createTools)
**Location:** Lines 420-443
**Debug Information:**
- Section marker: "=== CREATE TOOLS DEBUG ==="
- Tool faces count
- Visual debug of input tools (blue color)
- Error message for missing faces
- Extraction confirmation for face count
- Created tool bodies count
- Visual debug of created bodies (cyan color)

### 3. Apply Tab Function (applyTab)
**Location:** Lines 141-198
**Debug Information:**
- Section marker: "=== APPLY TAB DEBUG ==="
- Union entities array size
- Tab bodies count
- Error message for missing tab bodies
- Visual debug of tab bodies (red color)
- Visual debug of union query (blue color)
- Per-tab processing with index tracking
- Visual debug of tracked tab body (magenta color)
- Coincident walls discovery confirmation
- Visual debug of coincident walls (green color)
- Boolean operation status for each tab
- Success/NO_OP status reporting

### 4. Find Coincident Sheet Metal Walls Function (findCoincidentSheetMetalWalls)
**Location:** Lines 224-258
**Debug Information:**
- Section marker: "=== FIND COINCIDENT SHEET METAL WALLS DEBUG ==="
- Visual debug of tab body (red color)
- Visual debug of union query (blue color)
- Initial coincident faces count
- Alignment attempt notification
- Alignment result (true/false)
- Re-collection of coincident faces after alignment
- Post-alignment coincident faces count
- Error message for failure to find coincident faces
- Visual debug of final result query (green color)

### 5. Collect Coincident Faces Function (collectCoincidentFaces)
**Location:** Lines 263-295
**Debug Information:**
- Section marker: "=== COLLECT COINCIDENT FACES DEBUG ==="
- Visual debug of tab body (magenta color)
- Visual debug of union query (yellow color)
- Total collisions detected count
- Per-collision type reporting with index
- Skip notification for ClashType.NONE
- Confirmation of face addition to coincident list
- Visual debug of each target face (green color)
- Total coincident faces found count

### 6. Try Align Tab Body With Opposite Wall Function (tryAlignTabBodyWithOppositeWall)
**Location:** Lines 298-415
**Critical Direction Solving Debug - Most Comprehensive:**
- Section marker: "=== TRY ALIGN TAB BODY WITH OPPOSITE WALL DEBUG ==="
- Visual debug of tab faces (red color)
- Visual debug of union query (blue color)
- Distance evaluation result
- Distance between tab and union (value)
- Distance side0 and side1 points
- Zero tolerance check result
- Model parameters retrieval confirmation
- Model front thickness value
- Model back thickness value
- Total thickness value
- Thickness difference calculation and value
- Tolerance check result
- Tab face array size
- Distance side0 index value
- Index bounds check result
- Reference face selection index
- Visual debug of reference face (orange color)
- Tangent plane evaluation result
- Tangent plane origin vector
- Tangent plane normal vector
- Visual debug of tangent plane (purple color)
- Direction vector (unnormalized) calculation
- Direction magnitude value
- Zero magnitude check result
- Direction vector (normalized) calculation
- Visual debug of direction vector (green color)
- Dot product (direction · normal) calculation
- Offset sign determination (+1 or -1)
- Final offset distance calculation and value
- Offset face operation notification with distance
- Flip orientation operation notification
- Alignment success confirmation
- Visual debug of aligned tab body (cyan color)

### 7. Subtract Tab Function (subtractTab)
**Location:** Lines 560-658
**Debug Information:**
- Section marker: "=== SUBTRACT TAB DEBUG ==="
- Model parameters retrieval confirmation
- Model front thickness value
- Model back thickness value
- Model minimal clearance value
- Boolean offset from definition
- Thickening operation notification
- Visual debug of tab body faces (cyan color)
- Thickened body creation confirmation
- Visual debug of thickened body (magenta color)
- Union part faces for collision check
- Visual debug of union part faces (yellow color)
- Corresponding joint entities retrieval
- Visual debug of adjacent edges (orange color)
- Derip candidates identification
- Visual debug of corresponding entities (purple color)
- Derip candidates count
- Derip operation attempt and result
- Sheet metal subtract faces count
- Non-sheet metal queries empty status
- Offset application with distance value
- Visual debug of offset body (red color)
- Sheet metal subtraction execution
- Solid subtraction execution
- Minimal clearance warning check
- Cleanup of thickened bodies
- Completion message

### 8. Boolean One Tab Group Function (booleanOneTabGroup)
**Location:** Lines 665-762
**Debug Information:**
- Section marker: "=== BOOLEAN ONE TAB GROUP DEBUG ==="
- Visual debug of wall bodies (blue color)
- Visual debug of coincident walls (cyan color)
- Corner break tracking collection
- Subtract tab function call
- Pattern copy creation for boolean union
- Visual debug of tool bodies (magenta color)
- Boolean union attempt notification
- Boolean union success message
- Boolean union failure analysis
- Visual debug of union complement faces (yellow color)
- Collision count with union complement
- Per-collision type reporting with index
- Visual debug of collision tool (red color)
- Visual debug of collision target (orange color)
- Error geometry addition confirmation
- Tab collision error message
- Tab merge failure error message
- Cleanup of copied tool bodies
- Corner break remapping
- Boolean operation status result

## Color Coding Scheme
The debug statements use different colors to visually distinguish different types of entities:
- **RED**: Tab bodies and error conditions
- **BLUE**: Union queries and wall bodies
- **CYAN**: Tab body faces and aligned bodies
- **MAGENTA**: Tab bodies and thickened bodies
- **YELLOW**: Union part faces and union complement
- **GREEN**: Coincident walls and direction vectors
- **ORANGE**: Reference faces and collision targets
- **PURPLE**: Tangent planes and corresponding entities

## Usage
When the sheet metal tab feature is executed in Onshape, these debug statements will:
1. Print detailed information to the console log
2. Visualize geometry entities with colored highlights (only visible during feature editing)
3. Track the flow of execution through each function
4. Display critical calculations for direction solving
5. Report errors and warnings with context

## Key Areas for Bug Diagnosis
The most detailed debugging is in the `tryAlignTabBodyWithOppositeWall` function, which includes:
- Complete distance calculation tracking
- Direction vector computation and normalization
- Dot product calculation for offset sign determination
- Tangent plane evaluation
- All intermediate values and final offset distance

This should help identify why tab geometry is being placed in the wrong direction by showing:
1. The actual distance measurements
2. The computed direction vectors
3. The tangent plane normals
4. The offset calculations
5. Whether the alignment is succeeding or failing at each step
