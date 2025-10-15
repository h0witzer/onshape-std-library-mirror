FeatureScript 1224;
import(path : "onshape/std/geometry.fs", version : "1224.0");

annotation { "Feature Type Name" : "Perforate sheet metal bend" }
export const smPerforateBend = defineFeature(function(context is Context, id is Id, definition is map)
    precondition {
        
        annotation { "Name" : "Sheet metal model", "Filter" : EntityType.BODY && AllowFlattenedGeometry.YES, "MaxNumberOfPicks" : 1 }
        definition.body is Query;
        
        annotation { "Name" : "Bend lines", "Filter" : EntityType.BODY && BodyType.WIRE && AllowFlattenedGeometry.YES}
        definition.bendLines is Query;
        
        annotation { "Name" : "Perforation grade" }
        isReal(definition.perforationGrade, {(unitless):[0, 0.8, 1]} as RealBoundSpec);
        
        annotation { "Name" : "Force minimum tab width", "Default" : false  }
        definition.forceMinimumTabWidth is boolean;
        
        if (definition.forceMinimumTabWidth) {
            annotation { "Name" : "Minimum tab width" }
            isLength(definition.minimumTabWidth, {(millimeter):[0, 5, 10000]} as LengthBoundSpec);
        }
        
        annotation { "Name" : "Force minimum slot width", "Default" : false  }
        definition.forceMinimumSlotWidth is boolean;
        
        if (definition.forceMinimumSlotWidth) {
            annotation { "Name" : "Minimum slot width" }
            isLength(definition.minimumSlotWidth, {(millimeter):[0, 3, 10000]} as LengthBoundSpec);
        }
        
    } {

        if (evaluateQuery(context, definition.body) == []){
            throw regenError("Select sheet metal model", ["body"]);
        }
        if (evaluateQuery(context, definition.bendLines) == []){
            throw regenError("Select bend lines", ["bendLines"]);
        }

        var smattributes = getSmObjectTypeAttributes(context, qUnion(getSMDefinitionEntities(context, definition.body)), SMObjectType.MODEL)[0];
        var sheetThickness = smattributes.frontThickness is undefined ? smattributes.backThickness.value : (smattributes.backThickness is undefined ? smattributes.frontThickness.value : smattributes.frontThickness.value + smattributes.backThickness.value);
        
        var evaluatedCenterlines is array = evaluateQuery(context, definition.bendLines);
        var noSketches = 1;
        const sketchGroupId = id + "sketch";
         
        for (var i = 0; i < size(evaluatedCenterlines); i+=1) {
            
            var entityAssociations = getSMAssociationAttributes(context, evaluatedCenterlines[i]);

            var attributeQueries = [];
            
            for (var attribute in entityAssociations) {
                attributeQueries = append(attributeQueries, qAttributeQuery(attribute));
            }
                        
            // find bend faces (top/bottom)
            var flatFaces is Query = qSheetMetalFlatFilter(qParallelPlanes(qUnion(attributeQueries), vector(0, 0, 1)), SMFlatType.YES);
        
            // find aligned edges
            var entityAssociationQueries is Query = qUnion(attributeQueries);
            var onlyEdgesQueries is Query = qEntityFilter(entityAssociationQueries,EntityType.EDGE);
            var alignedEdges is Query = qSheetMetalFlatFilter(onlyEdgesQueries,SMFlatType.YES);
            var edges is array = evaluateQuery(context, alignedEdges);
            
            // calculate distances to bend centerline        
            var distances = [];
            for (var edge in edges) {
                var dist is DistanceResult = evDistance(context, { "side0" : edge, "side1" : evaluatedCenterlines[i] });
                distances = append(distances, dist.distance);
            }
    
            // use minimum distance as our perforation slot radius (1/2 slot width); possibly overwrite with forced minimum slot width
            var bendAllowanceHalf is ValueWithUnits= min(distances);
            var slotRadius = bendAllowanceHalf;
            if (definition.forceMinimumSlotWidth && ((slotRadius*2) < definition.minimumSlotWidth)) {
                slotRadius = definition.minimumSlotWidth/2;
            }
            
            // find endpoints of bend centerline
            var endPoints is Query = qAdjacent(evaluatedCenterlines[i], AdjacencyType.VERTEX, EntityType.VERTEX);
            var startPosition is Vector = evVertexPoint(context, {"vertex" : qNthElement(endPoints, 0)});
            var endPosition is Vector = evVertexPoint(context, {"vertex" : qNthElement(endPoints, 1)});
            
            // extract top plane only
            var topFace is Query =  qContainsPoint(flatFaces, startPosition);
            
            // project start-/endPosition onto sketch coordinate system (2D vectors)
            var startPositionInSketchCsys is Vector = worldToPlane(evPlane(context,{"face":topFace}),startPosition);
            var endPositionInSketchCsys is Vector = worldToPlane(evPlane(context,{"face":topFace}),endPosition);
            
            // get bend edge length
            var bendLength is ValueWithUnits = norm(endPositionInSketchCsys-startPositionInSketchCsys); //evLength(context, {"entities" : evaluatedCenterlines[i]});
            
            // define tab width
            var tabWidth = sheetThickness;
            if (tabWidth <= slotRadius){
                tabWidth = slotRadius+0.02*millimeter;
            }
            if (definition.forceMinimumTabWidth && (definition.minimumTabWidth > tabWidth)) {
                tabWidth = definition.minimumTabWidth;
            }
            
            // calculate ideal slot length
            var idealSlotLength = definition.perforationGrade * (tabWidth / (1-definition.perforationGrade));
            
            // calculate fitting slot count
            var slotCount = floor((bendLength-tabWidth) / (tabWidth+idealSlotLength));
            if (slotCount < 0){
                slotCount = 0;
            }
            
            // calculate actual slot length
            var slotLength = 0;
            if (slotCount > 0){
                slotLength = ((bendLength-tabWidth)/slotCount)-tabWidth;                
            }
            
            // calculate normalized bend direction vector, project onto sketch plane
            var bendDirectionVector = normalize(endPositionInSketchCsys - startPositionInSketchCsys);
            
            // make new sketch for perforation pattern
            setExternalDisambiguation(context, sketchGroupId + unstableIdComponent(i), evaluatedCenterlines[i]);
            var sketch1 = newSketch(context, sketchGroupId + unstableIdComponent(i), {"sketchPlane" : topFace});
            
            if ((slotCount == 0) && (bendLength >= (2*tabWidth+2*slotRadius+0.02*millimeter))){
                // bend length too small for regular slot pattern, but still large enough to fit
                // a shorter slot down to a hole with slotRadius

                noSketches = 0;

                skArc(sketch1, "arc1", {
                        "start" : startPositionInSketchCsys+(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)+(bendDirectionVector*(tabWidth+slotRadius)),
                        "mid" : startPositionInSketchCsys+(bendDirectionVector*tabWidth),
                        "end" : startPositionInSketchCsys-(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)+(bendDirectionVector*(tabWidth+slotRadius))
                });
                skArc(sketch1, "arc2", {
                        "start" : endPositionInSketchCsys+(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)-(bendDirectionVector*(tabWidth+slotRadius)),
                        "mid" : endPositionInSketchCsys-(bendDirectionVector*tabWidth),
                        "end" : endPositionInSketchCsys-(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)-(bendDirectionVector*(tabWidth+slotRadius))
                });
                skLineSegment(sketch1, "line1", {
                        "start" : startPositionInSketchCsys+(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)+(bendDirectionVector*(tabWidth+slotRadius)),
                        "end" : endPositionInSketchCsys+(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)-(bendDirectionVector*(tabWidth+slotRadius))
                });
                skLineSegment(sketch1, "line2", {
                        "start" : startPositionInSketchCsys-(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)+(bendDirectionVector*(tabWidth+slotRadius)),
                        "end" : endPositionInSketchCsys-(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)-(bendDirectionVector*(tabWidth+slotRadius))
                });
                
            } else if ((slotCount == 0) && (bendLength < (2*tabWidth+2*slotRadius+0.02*millimeter)) && ((bendLength-sheetThickness) >= tabWidth)){
                // bend length too small to even fit a hole with slotRadius, marking the bend endpoints only at 1/2 sheet thickness depth
 
                noSketches = 0;
                
                if (slotRadius > bendAllowanceHalf){
                    slotRadius = bendAllowanceHalf;
                }

                skArc(sketch1, "arc1", {
                        "start" : startPositionInSketchCsys+(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)-(bendDirectionVector*(slotRadius-sheetThickness/2)),
                        "mid" : startPositionInSketchCsys+(bendDirectionVector*slotRadius)-(bendDirectionVector*(slotRadius-sheetThickness/2)),
                        "end" : startPositionInSketchCsys-(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)-(bendDirectionVector*(slotRadius-sheetThickness/2))
                });
                skLineSegment(sketch1, "line1", {
                        "start" : startPositionInSketchCsys+(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius),
                        "end" : startPositionInSketchCsys-(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)
                });
                skArc(sketch1, "arc2", {
                        "start" : endPositionInSketchCsys+(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)+(bendDirectionVector*(slotRadius-sheetThickness/2)),
                        "mid" : endPositionInSketchCsys-(bendDirectionVector*slotRadius)+(bendDirectionVector*(slotRadius-sheetThickness/2)),
                        "end" : endPositionInSketchCsys-(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)+(bendDirectionVector*(slotRadius-sheetThickness/2))
                });
                skLineSegment(sketch1, "line2", {
                        "start" : endPositionInSketchCsys+(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius),
                        "end" : endPositionInSketchCsys-(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)
                });
                
            } else if (slotCount != 0){
                // regular slot pattern
                
                noSketches = 0;

                for (var slotNo = 0; slotNo < slotCount; slotNo += 1){
                    skArc(sketch1, "arc1"~toString(slotNo), {
                            "start" : startPositionInSketchCsys+(bendDirectionVector*tabWidth)+(bendDirectionVector*(slotLength+tabWidth)*slotNo)+(bendDirectionVector*slotRadius)+(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius),
                            "mid" : startPositionInSketchCsys+(bendDirectionVector*tabWidth)+(bendDirectionVector*(slotLength+tabWidth)*slotNo),
                            "end" : startPositionInSketchCsys+(bendDirectionVector*tabWidth)+(bendDirectionVector*(slotLength+tabWidth)*slotNo)+(bendDirectionVector*slotRadius)-(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)
                    });
                    skArc(sketch1, "arc2"~toString(slotNo), {
                            "start" : startPositionInSketchCsys+(bendDirectionVector*tabWidth)+(bendDirectionVector*(slotLength+tabWidth)*slotNo)+(bendDirectionVector*slotLength)-(bendDirectionVector*slotRadius)+(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius),
                            "mid" : startPositionInSketchCsys+(bendDirectionVector*tabWidth)+(bendDirectionVector*(slotLength+tabWidth)*slotNo)+(bendDirectionVector*slotLength),
                            "end" : startPositionInSketchCsys+(bendDirectionVector*tabWidth)+(bendDirectionVector*(slotLength+tabWidth)*slotNo)+(bendDirectionVector*slotLength)-(bendDirectionVector*slotRadius)-(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)
                    });
                    skLineSegment(sketch1, "line1"~toString(slotNo), {
                            "start" : startPositionInSketchCsys+(bendDirectionVector*tabWidth)+(bendDirectionVector*(slotLength+tabWidth)*slotNo)+(bendDirectionVector*slotRadius)+(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius),
                            "end" : startPositionInSketchCsys+(bendDirectionVector*tabWidth)+(bendDirectionVector*(slotLength+tabWidth)*slotNo)+(bendDirectionVector*slotLength)-(bendDirectionVector*slotRadius)+(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)
                    });
                    skLineSegment(sketch1, "line2"~toString(slotNo), {
                            "start" : startPositionInSketchCsys+(bendDirectionVector*tabWidth)+(bendDirectionVector*(slotLength+tabWidth)*slotNo)+(bendDirectionVector*slotRadius)-(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius),
                            "end" : startPositionInSketchCsys+(bendDirectionVector*tabWidth)+(bendDirectionVector*(slotLength+tabWidth)*slotNo)+(bendDirectionVector*slotLength)-(bendDirectionVector*slotRadius)-(vector(bendDirectionVector[1],-bendDirectionVector[0])*slotRadius)
                    });
                }
            }
  
            skSolve(sketch1);
        
        }
        
        if (noSketches != 1){

            extrude(context, id+"extrude", {
                "entities" : qSketchRegion(sketchGroupId),
                "endBound" : BoundingType.THROUGH_ALL,
            	"domain" : OperationDomain.FLAT,
            }); 
           
            opDeleteBodies(context, id+"delete", {
                       "entities" : qCreatedBy(sketchGroupId, EntityType.BODY)
            });
            
        }
    });
    
    
    
