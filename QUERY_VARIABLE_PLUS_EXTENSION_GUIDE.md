# Query Variable Plus: Adding New Query Types

This guide outlines how to extend **Query Variable Plus** with additional query types. It contrasts the Plus implementation with the standard **Query Variable** feature so future updates stay consistent with the existing patterns.

## Key differences vs. Query Variable
- **Expanded selection set**: Plus already supports `MATCHING_BODIES`, `ADJACENT`, `GEOMETRY`, and `LOAD_FROM_DERIVE` selection types that are not present in the base feature. The SelectionType enum and its lowercase label map include these entries, and any new type must follow the same pattern. 【F:custom-features/queryVariablePlus.fs†L25-L90】【F:queryVariable.fs†L23-L73】
- **Additional parameter blocks**: New selection types often require custom inputs in both `initialQueryPredicate` and `additionalQueryPredicate` (for array items). For example, adjacency selections add seed entities, adjacency type, and result entity options, while geometry selections capture a geometry type filter. Matching bodies reuse the body-seed branch used by OWNED_BY/EDGE_CONVEXITY. 【F:custom-features/queryVariablePlus.fs†L158-L240】【F:custom-features/queryVariablePlus.fs†L270-L342】
- **Custom query builders**: The Plus feature maps new selection types to dedicated helper functions such as `adjacencySelection` and `qMatchingBodies`, keeping `mapSelectionTypeToQuery` readable. Any new selection should have a similar mapper entry and, if needed, a focused helper for validation or pre/post-processing. 【F:custom-features/queryVariablePlus.fs†L517-L629】

## Derived Part Studio Support

Query Variable Plus now supports **persisting query variables across derived part studios**. This feature enables you to:
1. Save query variable names as attributes on entities in the source part studio
2. Reconstruct those query variables in a derived part studio by reading the attributes

### Saving Query Variables for Derived Studios

When creating a query variable, enable the **"Save for derived studios"** option. This will:
- Attach the query variable name as an attribute to all entities in the query
- Persist through derive operations, allowing reconstruction in the derived part studio

**Example workflow:**
1. In the source part studio, create a Query Variable+ with name "myEdges"
2. Select edges using any selection type (e.g., "Parallel edges")
3. Enable **"Save for derived studios"** checkbox
4. When these entities are brought into another part studio via a derive feature, they will carry the "myEdges" attribute

### Loading Query Variables from Derived Features

In a derived part studio, you can reconstruct query variables from the source:

1. Create a new Query Variable+ feature
2. Set **Selection type** to **"Load from derive feature"**
3. Select the **derive feature** that brought in the entities
4. Enter the **query variable name** to reconstruct (e.g., "myEdges")
5. The feature will find all entities from that derive feature with the matching query variable name attribute

**Important notes:**
- The original query variable must have been created with "Save for derived studios" enabled
- The attribute name used is `"queryVariableName"` and stores the query variable's name as a string
- Attributes persist through transformations, patterns, and other modifications in the derive process
- Multiple query variables can be saved and loaded from the same derived entities

### Use Cases
- **Propagating selections**: Select specific faces/edges in a source part studio and reference them in assemblies or derived contexts
- **Maintaining design intent**: Keep track of critical geometry features across part studio boundaries
- **Automated workflows**: Build parametric systems where derived studios automatically reference specific entities from source studios

## Implementation checklist for a new selection type
1. **Add the enum entry**
   - Insert the new selection type in `SelectionType` and the `SelectionTypeToLowercaseName` map so UI labels and computed parameters stay synchronized. Maintain the descriptive annotation style used by Plus. 【F:custom-features/queryVariablePlus.fs†L25-L90】
2. **Collect inputs in both predicates**
   - Extend `initialQueryPredicate` with annotated parameters (queries, enums, toggles) required by the new selection. Mirror the same fields in `additionalQueryPredicate`, prefixed with `addQ`. Reuse existing filters (`AllowMeshGeometry`, `AllowFlattenedGeometry`, entity filters) to match related cases when possible. 【F:custom-features/queryVariablePlus.fs†L158-L240】【F:custom-features/queryVariablePlus.fs†L270-L342】
3. **Map the selection to a query**
   - Update `mapSelectionTypeToQuery` with a switch arm that translates the new type into a Query expression. If the logic is involved (validation, clustering, or branching outputs), create a helper alongside `adjacencySelection`/`qMatchingBodies`/`loadFromDeriveSelection` to keep the switch concise. 【F:custom-features/queryVariablePlus.fs†L517-L629】
4. **Handle additional-query metadata**
   - The UI shows the lowercase selection name for each additional query via `faultyArrayParameterId` and `SelectionTypeToLowercaseName`. Ensure your new selection's lowercase label reads well in "`#booleanOperation of #selection`" strings. No extra changes are needed if the enum map is updated. 【F:custom-features/queryVariablePlus.fs†L465-L497】
5. **Reuse evaluation and highlighting behavior**
   - New selections automatically benefit from the existing evaluation flow (`evaluateOnUse`, robust query batching, debug highlighting). Avoid adding special-case rendering; instead, rely on the shared plumbing unless the query builder truly requires it. 【F:custom-features/queryVariablePlus.fs†L499-L515】
6. **Validate against the base feature**
   - Confirm the new selection type is specific to Plus (or intentionally differs from the base) by comparing against the standard `queryVariable` file. This helps avoid regressions when future syncs occur. 【F:queryVariable.fs†L23-L187】

## Tips for smooth integrations
- Keep parameter names descriptive and aligned with existing patterns to maintain readability across features.
- When a selection type can target different entity kinds (faces/edges/vertices), provide enums similar to `AdjacentResultType` and map them to concrete entity filters in a helper function.
- Favor existing query constructors (`qAdjacent`, `qGeometry`, `qMatching`) where possible; wrap them only to add validation or defaulting behavior.
- For attribute-based features, use descriptive attribute names and provide clear error messages when attributes are not found.

Following this checklist keeps Query Variable Plus consistent with its current extensions while making future additions easy to review and maintain.
