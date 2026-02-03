# Debug Sheet Metal Bend Reliefs Feature

## Purpose
This custom feature visualizes bend relief corners and sheet metal structure to help understand how bend reliefs are placed and interact with walls vs rips.

## Usage

1. Select a sheet metal part that has bend reliefs (create some using standard sheet metal features)
2. Enable/disable visualization options:
   - **Show master body edges**: Highlights all master body edges in BLUE
   - **Show corners**: Highlights corners by type with color coding
   - **Show bend relief details**: Prints detailed information about each bend relief

3. Run the feature to see visual highlighting and console output

## Color Legend

### Edges
- **BLUE**: Master body edges (definition entities)
- **GREEN**: BEND edges
- **YELLOW**: RIP edges

### Corners
- **RED**: BEND_END corners WITH bend relief
- **ORANGE**: BEND_END corners WITHOUT bend relief
- **CYAN**: OPEN corners
- **MAGENTA**: CLOSED corners

## Output Information

### Console Output
The feature prints comprehensive information:
- Total master body entities (edges, faces, vertices)
- Edge categorization by joint type
- Corner type distribution
- Bend relief details (style, scale, depth)
- Adjacency analysis (what edges meet at bend relief corners)

### Key Insights
The adjacency analysis shows:
- **"BEND meets WALL"**: Standard bend relief configuration
- **"BEND meets RIP"**: Unusual configuration (what we're investigating)

## How This Helps

This feature lets you:
1. **See working examples**: Apply it to manually created bend reliefs to see the pattern
2. **Understand topology**: See what edges and corners are involved
3. **Compare configurations**: Check if bend-to-wall differs from bend-to-rip
4. **Debug issues**: Identify why reliefs might not appear in certain situations

## Example Workflow

1. Create a sheet metal flange or bend that has bend reliefs
2. Apply this debug feature to that part
3. Look at the RED vertices (bend reliefs)
4. Check console output to see "BEND meets WALL" configuration
5. Compare with stitch cut bend results to see differences

## Technical Details

The feature uses:
- `getSMDefinitionEntities()` to get master body entities
- `evCornerType()` to identify corner types
- `getCornerAttribute()` to check for bend relief attributes
- `getJointAttribute()` to identify edge types
- `debug()` to highlight entities visually

This provides complete visibility into the sheet metal structure and relief configuration.
