# Triad Transform Multi-Copy Feature

## Overview

The Triad Transform feature has been enhanced with a new "Multi-copy mode" that allows users to place multiple copies of selected bodies at different positions and rotations using a freeform, interactive workflow. This enhancement borrows heavily from the Routing Curve feature's manipulator logic and UI patterns.

## How to Use

### Basic Multi-Copy Workflow

1. **Select Bodies**: Choose the bodies you want to transform/copy
2. **Enable Multi-Copy Mode**: Check the "Multi-copy mode" checkbox in the feature dialog
3. **Position First Copy**: 
   - Use the triad manipulator to position and rotate where you want the first copy
   - Drag the translation arrows to move
   - Drag the rotation arcs to rotate
4. **Place Copy**: Click the "Place copy" button to create an instance at the current manipulator position/rotation
5. **Place Additional Copies**:
   - Continue dragging the manipulator to a new position/rotation
   - Click "Place copy" again
   - Repeat as needed

### Modifying Placed Instances

- **Select Instance**: Click on any of the point manipulators (small dots) representing placed instances
- **Adjust Transform**: When an instance is selected, the triad manipulator loads that instance's transform
- **Update Position/Rotation**: Drag the manipulator to adjust the selected instance
- **Delete Instance**: Use the delete button in the instances array in the feature dialog

### Key Features

- **Visual Feedback**: Point manipulators appear at each placed instance location
- **Instance Selection**: Click any point manipulator to focus and tweak that instance
- **Live Updates**: Moving the manipulator while an instance is selected updates that instance in real-time
- **Array Management**: All instances appear in an expandable array in the feature dialog
- **Individual Parameters**: Each instance has its own X/Y/Z translation and rotation values that can be edited manually

## Technical Implementation

### Architecture

The multi-copy feature follows the established pattern from the Routing Curve feature:

1. **Instance Storage**: Each placed copy is stored as an instance in the `instances` array
2. **Point Manipulators**: Visual point manipulators allow clicking to select instances
3. **Button UI**: The "Place copy" button uses the `isButton` predicate pattern
4. **Edit Logic**: Button clicks and array management handled via `triadTransformEditLogic` function
5. **Index Management**: Automatic reordering and deduplication of instance indices on add/delete

### Instance Data Structure

Each instance contains:
- `index`: Unique identifier for the instance
- `dx`, `dy`, `dz`: Translation offsets from base coordinate system
- `rx`, `ry`, `rz`: Rotation angles around X, Y, Z axes
- `rotationMatrix`: 3x3 rotation matrix for the transform

### Key Functions

- `addInstanceManipulators()`: Creates point manipulators for all placed instances
- `triadTransformEditLogic()`: Handles button clicks, instance selection, and array management
- `triadTransformManipulatorChange()`: Updates transforms when manipulators are moved
- `shiftIndicesForInstances()`: Maintains sequential instance indices
- `deduplicateIndicesForInstances()`: Ensures unique instance indices

## Behavior Details

### Mode Interactions

- **Multi-Copy Mode ON**: Only saved instances create geometry; manipulator is for placement only
- **Multi-Copy Mode OFF + Copy Parts ON**: Creates single copy at current manipulator position
- **Multi-Copy Mode OFF + Copy Parts OFF**: Transforms original bodies in-place

### Instance Synchronization

When an instance is selected:
1. Its transform parameters are loaded into the main manipulator
2. Moving the manipulator updates both the main parameters and the selected instance
3. The selected instance is highlighted in the instances array

### Advanced Features Compatibility

Multi-copy mode is fully compatible with existing features:
- **Advanced Placement**: Custom reference coordinate systems work with all instances
- **Geometry Snapping**: Manipulator can snap to reference entities while placing instances
- **Reference Coordinate System**: All instances use the same base coordinate system

## Comparison to Routing Curve

The implementation borrows these patterns from Routing Curve:

| Pattern | Routing Curve | Triad Transform Multi-Copy |
|---------|---------------|----------------------------|
| Button UI | "Process inputs" button | "Place copy" button |
| Array Items | Points array | Instances array |
| Index Parameter | pointIndex | instanceIndex |
| Manipulators | Point manipulators for curve points | Point manipulators for instances |
| Edit Logic | routingCurveEditLogic | triadTransformEditLogic |
| Selection | Click point to edit | Click instance to edit |
| Item Management | Add/delete points | Add/delete instances |

## Usage Tips

1. **Preview Before Placing**: Position the manipulator exactly where you want before clicking "Place copy"
2. **Tweak After Placement**: You can always click an instance point and adjust it later
3. **Manual Entry**: For precise placement, you can manually edit instance parameters in the array
4. **Delete Unwanted**: Use the array delete button to remove instances you don't want
5. **Base Coordinate System**: All instances are positioned relative to the base coordinate system (centroid by default, or custom reference)

## Version Information

- **Feature Version**: 2837
- **Implementation Date**: 2025
- **Pattern Source**: Based on Routing Curve (routingCurve.fs)
