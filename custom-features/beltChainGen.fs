FeatureScript 2045;
import(path : "onshape/std/geometry.fs", version : "2045.0");

ImageNamespace::import(path : "f948b7418338508f83e16de9", version : "16664cd396a8d2389ca3c60a");


icon::import(path : "511bc593ab9f1b436ceab435", version : "1d1fd0c07f500deedde5eb76");

Link::import(path : "9e1d98f167a22ac31f56336a", version : "20a834d8913fe40dfe4d5b0a");
Link::import(path : "9e1d98f167a22ac31f56336a", version : "20a834d8913fe40dfe4d5b0a");


//Featurescript derived from Joshua Wang's Configurable Parts Featurescripts: https://cad.onshape.com/documents/b273b67c06b86b78b01b6f3a/v/56fb8fa8d7d90b925d8d9775/e/2fd838311a32255a728a8449
export enum genType
{
    annotation { "Name" : "Belt" }
    BELT,
    annotation { "Name" : "Chain" }
    CHAIN,
}

export enum beltType
{
    annotation { "Name" : "HTD 5mm" }
    HTD5,
    annotation { "Name" : "GT2 3mm" }
    GT2_3,
    annotation { "Name" : "RT25" }
    RT25
}

const beltDims = {
        beltType.HTD5 : {
            d1 : 1.16 * millimeter,
            d2 : 0.57 * millimeter,
            d2_nt : 2.08 * millimeter,
            d3 : 1.16 * millimeter,
            r1 : 1.49 * millimeter,
            r2 : 0.42 * millimeter,
            pitch : 5 * millimeter,
            name : "5M" },
        beltType.GT2_3 : {
            d1 : 0.81 * millimeter,
            d2 : 0.38 * millimeter,
            d2_nt : 1.22 * millimeter,
            d3 : 0.73 * millimeter,
            r1 : 0.87 * millimeter,
            r2 : 0.26 * millimeter,
            pitch : 3 * millimeter,
            name : "3M" },
        beltType.RT25 : {
            d1 : 0.7112 * millimeter,
            d2 : 0.5588 * millimeter,
            d2_nt : 2.413 * millimeter,
            d3 : 1.09 * millimeter,
            r1 : 1.8 * millimeter,
            r2 : 0.529082 * millimeter,
            pitch : 6.35 * millimeter,
            name : "RT25" }
    };

export enum chainType
{
    // annotation {"Name" : "03B (5mm)"}
    // I3B,
    // annotation {"Name" : "04B (6mm)"}
    // I4B,
    // annotation {"Name" : "05B (8mm)"}
    // I5B,
    // annotation {"Name" : "Plastic (8mm)"}
    // P8,
    annotation { "Name" : "#25 (0.25in)" }
    A25,
    annotation { "Name" : "#35 (0.375in)" }
    A35
    // annotation {"Name" : "#40 (0.5in)"}
    // A40,
    // annotation {"Name" : "#50 (0.625in)"}
    // A50,
    // annotation {"Name" : "#60 (0.75in)"}
    // A60
}

export const chainDims = {
        // chainType.I3B : {
        //     d : 4.1 * millimeter,
        //     w : 7.4 * millimeter,
        //     m : 0.4 * gram,
        //     pitch : 5 * millimeter,
        //     conf : Link::Pitch_conf._03B,
        //     name : "03B"},
        // chainType.I4B : {
        //     d : 5 * millimeter,
        //     w : 7.4 * millimeter,
        //     m : 0.72 * gram,
        //     pitch : 6 * millimeter,
        //     conf : Link::Pitch_conf._04B,
        //     name : "04B"},
        // chainType.I5B : {
        //     d : 7.1 * millimeter,
        //     w : 8.6 * millimeter,
        //     m : 1.44 * gram,
        //     pitch : 8 * millimeter,
        //     conf : Link::Pitch_conf._05B,
        //     name : "05B"},
        // chainType.P8 : {
        //     d : 6.5 * millimeter,
        //     w : 9 * millimeter,
        //     m : 0.42 * gram,
        //     pitch : 8 * millimeter,
        //     conf : Link::Pitch_conf.Plastic,
        //     name : "Plastic"},
        chainType.A25 : {
            d : 0.237 * inch,
            w : 0.359 * inch,
            m : 0.83 * gram,
            pitch : 0.25 * inch,
            conf : Link::Pitch_conf._25,
            name : "#25" },
        chainType.A35 : {
            d : 0.356 * inch,
            w : 0.472 * inch,
            m : 3.33 * gram,
            pitch : 0.375 * inch,
            conf : Link::Pitch_conf._35,
            name : "#35" }
        // chainType.A40 : {
        //     d : 0.475 * inch,
        //     w : 0.646 * inch,
        //     m : 7.62 * gram,
        //     pitch : 0.5 * inch,
        //     conf : Link::Pitch_conf._40,
        //     name : "#40"},
        // chainType.A50 : {
        //     d : 0.594 * inch,
        //     w : 0.803 * inch,
        //     m : 16 * gram,
        //     pitch : 0.625 * inch,
        //     conf : Link::Pitch_conf._50,
        //     name : "#50"},
        // chainType.A60 : {
        //     d : 0.713 * inch,
        //     w : 0.996 * inch,
        //     m : 30.1 * gram,
        //     pitch : 0.75 * inch,
        //     conf : Link::Pitch_conf._60,
        //     name : "#60"}
    };
    
    
export enum OffsetType
{
    annotation { "Name" : "Entity" }
    ENTITY,
    annotation { "Name" : "Blind" }
    BLIND,
}


annotation { "Feature Type Name" : "Belt & Chain Gen", "Feature Name Template" : "#_name", "Icon" : icon::BLOB_DATA,
        "Feature Type Description" : "Creates a belt or chain. Developed by Jonathan Mi & Andrew Card and derived from Joshua Wang's Configurable Parts Featurescripts. For more information, see https://www.frcdesign.org/resources/featurescripts/","Description Image" : ImageNamespace::BLOB_DATA }

export const BeltChainGen = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Generator type", "UIHint" : ["HORIZONTAL_ENUM", "REMEMBER_PREVIOUS_VALUE"] }
        definition.gen is genType;

        annotation { "Group Name" : "Belt/Chain", "Collapsed By Default" : false}
        {
            if (definition.gen == genType.BELT)
            {
                annotation { "Name" : "Pitch", "UIHint" : UIHint.SHOW_LABEL, "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE] }
                definition.pitch is beltType;
    
                //RT25 belts only come in 1/2in width and no double side
                if (definition.pitch != beltType.RT25)
                {
                    annotation { "Name" : "Double-sided", "Default" : false }
                    definition.d is boolean;
    
                    annotation { "Name" : "Width", "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE] }
                    isLength(definition.width, { (millimeter) : [0, 9, 10000] } as LengthBoundSpec);
                }
            }
            else
            {
                annotation { "Name" : "Pitch", "UIHint" : UIHint.SHOW_LABEL, "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE] }
                definition.chainPitch is chainType;
            }
    
            annotation { "Name" : "Generate teeth/links", "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE] }
            definition.teeth is boolean;
        }

        annotation { "Name" : "Starting offset","Default" : false, "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE] }
        definition.startingOffset is boolean;
        
        if(definition.startingOffset)
        {
            annotation { "Group Name" : "Starting offset", "Collapsed By Default" : false, "Driving Parameter" : "startingOffset"}
            {
                annotation { "Name" : "Starting offset bound", "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE] }
                definition.offsetBound is OffsetType;
                
                if(definition.offsetBound == OffsetType.ENTITY)
                {
                    annotation { "Name" : "Entity", "Filter" : BodyType.MATE_CONNECTOR || (EntityType.FACE), "MaxNumberOfPicks" : 1 } //
                    definition.refPlane is Query;
                }  
                annotation { "Name" : "Offset distance", "Default" : 0*inch}
                isLength(definition.offsetDistance, { (inch) : [-10000, 0, 10000] } as LengthBoundSpec);
                annotation { "Name" : "Opposite direction", "UIHint": "OPPOSITE_DIRECTION" }
                definition.flipOffset is boolean;
                
            }
        }
        
        annotation { "Group Name" : "Selections", "Collapsed By Default" : false}
        {
            annotation { "Name" : "Guides (Clockwise Order)", "Item name" : "Guide", "Driven query" : "c", "Item label template" : "#c" }
            definition.guides is array;
            for (var guide in definition.guides)
            {
                annotation { "Name" : "Circle", "Filter" : GeometryType.CIRCLE && SketchObject.YES, "MaxNumberOfPicks" : 1 }
                guide.c is Query;
    
                annotation { "Name" : "Inside / Outside", "Default" : true }
                guide.i is boolean;
    
                if (definition.gen == genType.CHAIN || definition.d || guide.i) //tooth/round option is NOT possible for a pulley on the outside of a single sided belt.
                {
                    annotation { "Name" : "Toothed / Round", "Default" : true }
                    guide.t is boolean;
                }
            }
        }
        
        // annotation { "Group Name" : "Advanced Options", "Collapsed By Default" : true, "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE] }
        // {
            annotation { "Name" : "Mate connectors", "Default" : false, "UIHint" : [UIHint.REMEMBER_PREVIOUS_VALUE] }
            definition.m is boolean;
            
            annotation { "Name" : "Disable belt validation", "Default" : false}
            definition.disableBeltValidation is boolean;
            
        // }
    }
    {
        if (definition.gen == genType.BELT)
        {

            var bd = beltDims[definition.pitch];
            var d = definition.d;
            var w = definition.width;

            if (definition.pitch == beltType.RT25)
            {
                d = false;
                w = 0.5 * inch;
            }

            var gs = definition.guides;
            var d1 = bd.d1;

            //inside thickness of belt depends on if teeth or no teeth (bandaid fix wahhhhhh)
            var d2 = bd.d2;
            if (!definition.teeth)
            {
                d2 += bd.d2_nt;
            }

            var d3 = bd.d3;
            var r1 = bd.r1;
            var r2 = bd.r2;
            var beltSize = bd.name;
            var pitch = bd.pitch;
            if (!gs[0].i)
            {
                throw regenError("First guide must be inside.");
            }

            //plane for checking coplanarness of entries
            var pSkEntities = evOwnerSketchPlane(context, { "entity" : gs[0].c });

            // use reference plane if selected (to place the belt)
            var p;
            if(definition.startingOffset)
            {
                if(definition.offsetBound == OffsetType.ENTITY)
                {
                    var pRef = evPlane(context, { "face" : definition.refPlane });
                    p = pRef;
                    definition.offsetDistance = definition.flipOffset ? -definition.offsetDistance : definition.offsetDistance;
                    p.origin += pRef.normal * definition.offsetDistance;
                }
                else //blind offset
                {
                    var basePlane = pSkEntities; // plane to offset from
                    var offsetDist = definition.offsetDistance;
                    if(definition.flipOffset)
                    {
                        offsetDist = -offsetDist;
                    }
            
                    // Create a new plane offset along normal
                    p = plane(
                        basePlane.origin + basePlane.normal * offsetDist,
                        basePlane.normal
            );
                }
            }
            else
            {
                p = pSkEntities;
            }

            //Error Checking

            //Check if guides are coplanar
            var twoIn = false;
            for (var i = 1; i < size(gs); i += 1)
            {
                if (!coplanarPlanes(pSkEntities, evOwnerSketchPlane(context, { "entity" : gs[i].c })))
                {
                    throw regenError("All guides must be coplanar.");
                }
                twoIn ||= gs[i].i;
            }

            //Two guides at minimum
            if (!twoIn)
            {
                throw regenError("There must be at least two inside guides.");
            }

            //Reference plane must be parallel with guide plane
            if (!parallelVectors(p.normal, pSkEntities.normal))
            {
                throw regenError("Reference plane must be parallel with guide entities");
            }



            //begin shit that I don't understand
            var cs = [];
            var rs = [];
            var rs1 = [];
            var rs2 = [];
            for (var g in gs)
            {
                cs = append(cs, worldToPlane(p, evCurveDefinition(context, { "edge" : g.c }).coordSystem.origin));

                var adder = 0 * inch;

                if (!g.t)
                {
                    if (g.i || d)
                    {
                        adder += d2;
                        if (definition.teeth)
                        {
                            adder += bd.d2_nt;
                        }
                    }
                }

                println(adder);

                rs = append(rs, evCurveDefinition(context, { "edge" : g.c }).radius + (g.i || d ? 0 * meter : d1) + adder);
                rs1 = append(rs1, last(rs) + (g.i ? 1 : -1) * (d ? d2 : d1));
                rs2 = append(rs2, last(rs) + (g.i ? -d2 : d2));
            }
            var angs = [];
            var ps = [];
            var ps1 = [];
            var ps2 = [];
            for (var i = 0; i < size(cs); i += 1)
            {
                var ip = (i + 1) % size(cs);
                var d = cs[ip] - cs[i];
                var a = atan2(d[1], d[0]);
                var a1 = acos((rs[i] - rs[ip]) / norm(d));
                var a2 = acos((rs[i] + rs[ip]) / norm(d));
                if (gs[i].i && gs[ip].i)
                {
                    angs = append(append(angs, a + a1), a + a1);
                    ps = append(ps, cs[i] + rs[i] * vector(cos(a + a1), sin(a + a1)));
                    ps1 = append(ps1, cs[i] + rs1[i] * vector(cos(a + a1), sin(a + a1)));
                    ps2 = append(ps2, cs[i] + rs2[i] * vector(cos(a + a1), sin(a + a1)));
                    ps = append(ps, cs[ip] + rs[ip] * vector(cos(a + a1), sin(a + a1)));
                    ps1 = append(ps1, cs[ip] + rs1[ip] * vector(cos(a + a1), sin(a + a1)));
                    ps2 = append(ps2, cs[ip] + rs2[ip] * vector(cos(a + a1), sin(a + a1)));
                }
                else if (gs[i].i && !gs[ip].i)
                {
                    angs = append(append(angs, a + a2), a + a2 + PI * radian);
                    ps = append(ps, cs[i] + rs[i] * vector(cos(a + a2), sin(a + a2)));
                    ps1 = append(ps1, cs[i] + rs1[i] * vector(cos(a + a2), sin(a + a2)));
                    ps2 = append(ps2, cs[i] + rs2[i] * vector(cos(a + a2), sin(a + a2)));
                    ps = append(ps, cs[ip] - rs[ip] * vector(cos(a + a2), sin(a + a2)));
                    ps1 = append(ps1, cs[ip] - rs1[ip] * vector(cos(a + a2), sin(a + a2)));
                    ps2 = append(ps2, cs[ip] - rs2[ip] * vector(cos(a + a2), sin(a + a2)));
                }
                else if (!gs[i].i && gs[ip].i)
                {
                    angs = append(append(angs, a - a2), a - a2 + PI * radian);
                    ps = append(ps, cs[i] + rs[i] * vector(cos(a - a2), sin(a - a2)));
                    ps1 = append(ps1, cs[i] + rs1[i] * vector(cos(a - a2), sin(a - a2)));
                    ps2 = append(ps2, cs[i] + rs2[i] * vector(cos(a - a2), sin(a - a2)));
                    ps = append(ps, cs[ip] - rs[ip] * vector(cos(a - a2), sin(a - a2)));
                    ps1 = append(ps1, cs[ip] - rs1[ip] * vector(cos(a - a2), sin(a - a2)));
                    ps2 = append(ps2, cs[ip] - rs2[ip] * vector(cos(a - a2), sin(a - a2)));
                }
                else
                {
                    angs = append(append(angs, a - a1), a - a1);
                    ps = append(ps, cs[i] + rs[i] * vector(cos(a - a1), sin(a - a1)));
                    ps1 = append(ps1, cs[i] + rs1[i] * vector(cos(a - a1), sin(a - a1)));
                    ps2 = append(ps2, cs[i] + rs2[i] * vector(cos(a - a1), sin(a - a1)));
                    ps = append(ps, cs[ip] + rs[ip] * vector(cos(a - a1), sin(a - a1)));
                    ps1 = append(ps1, cs[ip] + rs1[ip] * vector(cos(a - a1), sin(a - a1)));
                    ps2 = append(ps2, cs[ip] + rs2[ip] * vector(cos(a - a1), sin(a - a1)));
                }
            }

            //Create the sketch i think
            angs = append(subArray(angs, 1, size(angs)), angs[0]);
            var sk = newSketchOnPlane(context, id + "sketch1", { "sketchPlane" : p });
            var ls = [0 * meter];
            for (var i = 0; i < size(cs); i += 1)
            {
                skLineSegment(sk, "1l" ~ i, { "start" : ps1[2 * i], "end" : ps1[2 * i + 1] });
                skLineSegment(sk, "2l" ~ i, { "start" : ps2[2 * i], "end" : ps2[2 * i + 1] });
                ls = append(ls, last(ls) + norm(ps[2 * i + 1] - ps[2 * i]));
                var ad = (angs[2 * i + 1] - angs[2 * i]) / radian;
                var ip = (i + 1) % size(cs);
                var am = gs[ip].i ? (angs[2 * i + 1] + (PI - ad / 2) % PI * radian) : (angs[2 * i] + (PI + ad / 2) % PI * radian);
                skArc(sk, "1a" ~ i, { "start" : ps1[2 * i + 1], "mid" : cs[ip] + rs1[ip] * vector(cos(am), sin(am)), "end" : ps1[2 * ip] });
                skArc(sk, "2a" ~ i, { "start" : ps2[2 * i + 1], "mid" : cs[ip] + rs2[ip] * vector(cos(am), sin(am)), "end" : ps2[2 * ip] });
                ls = append(ls, last(ls) + rs[ip] * (gs[ip].i ? (2 * PI - ad) % (2 * PI) : (2 * PI + ad) % (2 * PI)));
            }

            //show the pitch length in a pop up box
            reportFeatureInfo(context, id, "Pitch Length: " ~ roundToPrecision((last(ls) / millimeter), 3) ~ " mm");

            //Extrude Teeth
            var nt = (last(ls) / pitch % 1 < 0.1) ? floor(last(ls) / pitch) : ceil(last(ls) / pitch);
            if (definition.teeth)
            {
                for (var i = 0; i < nt; i += 1)
                {
                    for (var j = 0; j < size(ps); j += 1)
                    {
                        if (i * last(ls) / nt < ls[j + 1])
                        {
                            var jp = floor((j + 1) / 2) % size(cs);
                            if (j % 2 == 0)
                            {
                                var v = normalize(ps[j + 1] - ps[j]);
                                skCircle(sk, "1c" ~ i, { "center" : cs[jp] + (ps[j] - cs[jp]) * (1 + (gs[jp].i ? -d3 : d3) / rs[jp]) + v * (i * last(ls) / nt - ls[j]), "radius" : r1 });
                                if (definition.d)
                                {
                                    skCircle(sk, "2c" ~ i, { "center" : cs[jp] + (ps[j] - cs[jp]) * (1 + (gs[jp].i ? d3 : -d3) / rs[jp]) + v * (i * last(ls) / nt - ls[j]), "radius" : r1 });
                                }

                            }
                            else
                            {
                                var a = angs[j - 1] + (i * last(ls) / nt - ls[j]) * (gs[jp].i ? -1 : 1) * radian / rs[jp];
                                skCircle(sk, "1c" ~ i, { "center" : cs[jp] + (rs[jp] + (gs[jp].i ? -d3 : d3)) * vector(cos(a), sin(a)), "radius" : r1 });
                                if (definition.d)
                                {
                                    skCircle(sk, "2c" ~ i, { "center" : cs[jp] + (rs[jp] + (gs[jp].i ? d3 : -d3)) * vector(cos(a), sin(a)), "radius" : r1 });
                                }
                            }
                            break;
                        }
                    }
                }
            }
            skSolve(sk);
            var skq = qSubtraction(qSketchRegion(id + "sketch1"), qContainsPoint(qSketchRegion(id + "sketch1"), planeToWorld(p, cs[0])));

            //extrude belt
            opExtrude(context, id + "extrude1", { "entities" : skq, "direction" : p.normal, "endBound" : BoundingType.BLIND, "endDepth" : w / 2, "startBound" : BoundingType.BLIND, "startDepth" : w / 2 });

            //set feature name
            var toothCountDecimal = roundToPrecision((last(ls) / pitch),1);
            var pitchLength is string = "";
            if (definition.pitch == beltType.RT25) //display RT belt pitch in inch
            {
                pitchLength = roundToPrecision((last(ls) / inch), 2) ~ "in";
            }
            else
            {
                pitchLength = roundToPrecision((last(ls) / millimeter), 2) ~ "mm";
            }
            var featureName is string = "";
            if (definition.d) //display DS for double sided belt
            {
                featureName = nt ~ "T " ~ beltSize ~ " DS Belt - " ~ pitchLength;
            }
            else
            {
                featureName = nt ~ "T " ~ beltSize ~ " Belt - " ~ pitchLength;
            }

            setFeatureComputedParameter(context, id, {
                        "name" : "_name",
                        "value" : featureName
                    });
                    
                    
            if ( !definition.disableBeltValidation)
            {
                var toothError = abs((last(ls) / pitch) - round(last(ls) / pitch));

                if(toothError>0.1)
                {
                    reportFeatureWarning(context, id, "Warning: Belt pitch length is far from a whole tooth number belt, are you sure your center distance is correct?");
                }
            }

            //set part properties
            var partName is string = "";
            if (definition.pitch == beltType.RT25)
            {
                partName = nt ~ "T " ~ beltSize ~ " " ~ roundToPrecision((w / inch), 1) ~ "in Wide Belt";
            }
            else
            {
                if (definition.d)
                {
                    partName = nt ~ "T " ~ beltSize ~ " " ~ roundToPrecision((w / millimeter), 1) ~ "mm Wide DS Belt";
                }
                else
                {
                    partName = nt ~ "T " ~ beltSize ~ " " ~ roundToPrecision((w / millimeter), 1) ~ "mm Wide Belt";
                }
            }

            setProperty(context, { "entities" : qCreatedBy(id + "extrude1", EntityType.BODY), "propertyType" : PropertyType.NAME, "value" : partName });

            setProperty(context, { "entities" : qCreatedBy(id + "extrude1", EntityType.BODY), "propertyType" : PropertyType.APPEARANCE, "value" : color(0.2, 0.2, 0.2) });

            setProperty(context, { "entities" : qCreatedBy(id + "extrude1", EntityType.BODY), "propertyType" : PropertyType.MATERIAL,
                        "value" : material("Neoprene", 1.5 * gram / centimeter ^ 3) });
            opDeleteBodies(context, id + "delete1", { "entities" : qCreatedBy(id + "sketch1") });
            var cq = qCreatedBy(id + "extrude1", EntityType.BODY);

            //Fillet the teeth
            if (definition.teeth)
            {
                var eq = qParallelEdges(qCreatedBy(id + "extrude1", EntityType.EDGE), p.normal);
                opFillet(context, id + "fillet1", { "entities" : eq, "radius" : r2 });
            }


            //optionally show mate connectors
            if (definition.m)
            {
                //calculate the plane offset (most scuffed code ever)
                var vectorBetweenPlanes = vector(p.origin - pSkEntities.origin);
                var planeOffset = dot(vectorBetweenPlanes, p.normal);

                var mq = qCreatedBy(id + "extrude1", EntityType.BODY);
                for (var i = 0; i < size(gs); i += 1)
                {
                    opMateConnector(context, id + ("mate" ~ i), { "coordSystem" : evCurveDefinition(context, { "edge" : gs[i].c }).coordSystem, "owner" : mq });
                    opTransform(context, id + ("transform" ~ i), {
                                "bodies" : qCreatedBy(id + ("mate" ~ i), EntityType.BODY),
                                "transform" : transform(evMateConnector(context, { "mateConnector" : qCreatedBy(id + ("mate" ~ i)) }).zAxis * planeOffset)
                                // "transform" : transform(evMateConnector(context, { "mateConnector" : definition.refPlane }).zAxis * planeOffset)
                            });

                }
            }
        }
        /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        //MOST CURSED EVER IMPLEMENTATION OF THIS FEATURESCRIPT IM SO SORRY

        ////////////
        else
        {
            var cd = chainDims[definition.chainPitch];
            var d = cd.d;
            var w = cd.w;
            var chainName = cd.name;
            var pitch = cd.pitch;
            var gs = definition.guides;

            //ERROR CHECKING
            if (!gs[0].i)
            {
                throw regenError("First guide must be inside.");
            }

            //plane for checking coplanarness of entries
            var pSkEntities = evOwnerSketchPlane(context, { "entity" : gs[0].c });

            //reference plane (to place the belt)
            var p;
            if(definition.startingOffset)
            {
                if(definition.offsetBound == OffsetType.ENTITY)
                {
                    var pRef = evPlane(context, { "face" : definition.refPlane });
                    p = pRef;
                    definition.offsetDistance = definition.flipOffset ? -definition.offsetDistance : definition.offsetDistance;
                    p.origin += pRef.normal * definition.offsetDistance;
                }
                else //blind offset
                {
                    var basePlane = pSkEntities; // plane to offset from
                    var offsetDist = definition.offsetDistance;
                    if(definition.flipOffset)
                    {
                        offsetDist = -offsetDist;
                    }
            
                    // Create a new plane offset along normal
                    p = plane(
                        basePlane.origin + basePlane.normal * offsetDist,
                        basePlane.normal
            );
                }
            }
            else
            {
                p = pSkEntities;
            }

            var twoIn = false;
            for (var i = 1; i < size(gs); i += 1)
            {
                if (!coplanarPlanes(pSkEntities, evOwnerSketchPlane(context, { "entity" : gs[i].c })))
                {
                    throw regenError("All guides must be coplanar.");
                }
                twoIn ||= gs[i].i;
            }
            if (!twoIn)
            {
                throw regenError("There must be at least two inside guides.");
            }

            //Reference plane must be parallel with guide plane
            if (!parallelVectors(p.normal, pSkEntities.normal))
            {
                throw regenError("Reference plane must be parallel with guide entities");
            }


            var cs = [];
            var rs = [];
            var fs = [];
            var rs1 = [];
            var rs2 = [];
            for (var g in gs)
            {
                cs = append(cs, worldToPlane(p, evCurveDefinition(context, { "edge" : g.c }).coordSystem.origin));
                rs = append(rs, evCurveDefinition(context, { "edge" : g.c }).radius + (g.t ? 0 * meter : d / 2));
                fs = append(fs, pitch * radian / (2 * asin(pitch / (2 * last(rs))) * last(rs)));
                rs1 = append(rs1, last(rs) + (g.i ? d / 2 : -d / 2));
                rs2 = append(rs2, last(rs) + (g.i ? -d / 2 : d / 2));
            }
            var angs = [];
            var ps = [];
            var ps1 = [];
            var ps2 = [];
            for (var i = 0; i < size(cs); i += 1)
            {
                var ip = (i + 1) % size(cs);
                var d = cs[ip] - cs[i];
                var a = atan2(d[1], d[0]);
                var a1 = acos((rs[i] - rs[ip]) / norm(d));
                var a2 = acos((rs[i] + rs[ip]) / norm(d));
                if (gs[i].i && gs[ip].i)
                {
                    angs = append(append(angs, a + a1), a + a1);
                    ps = append(ps, cs[i] + rs[i] * vector(cos(a + a1), sin(a + a1)));
                    ps1 = append(ps1, cs[i] + rs1[i] * vector(cos(a + a1), sin(a + a1)));
                    ps2 = append(ps2, cs[i] + rs2[i] * vector(cos(a + a1), sin(a + a1)));
                    ps = append(ps, cs[ip] + rs[ip] * vector(cos(a + a1), sin(a + a1)));
                    ps1 = append(ps1, cs[ip] + rs1[ip] * vector(cos(a + a1), sin(a + a1)));
                    ps2 = append(ps2, cs[ip] + rs2[ip] * vector(cos(a + a1), sin(a + a1)));
                }
                else if (gs[i].i && !gs[ip].i)
                {
                    angs = append(append(angs, a + a2), a + a2 + PI * radian);
                    ps = append(ps, cs[i] + rs[i] * vector(cos(a + a2), sin(a + a2)));
                    ps1 = append(ps1, cs[i] + rs1[i] * vector(cos(a + a2), sin(a + a2)));
                    ps2 = append(ps2, cs[i] + rs2[i] * vector(cos(a + a2), sin(a + a2)));
                    ps = append(ps, cs[ip] - rs[ip] * vector(cos(a + a2), sin(a + a2)));
                    ps1 = append(ps1, cs[ip] - rs1[ip] * vector(cos(a + a2), sin(a + a2)));
                    ps2 = append(ps2, cs[ip] - rs2[ip] * vector(cos(a + a2), sin(a + a2)));
                }
                else if (!gs[i].i && gs[ip].i)
                {
                    angs = append(append(angs, a - a2), a - a2 + PI * radian);
                    ps = append(ps, cs[i] + rs[i] * vector(cos(a - a2), sin(a - a2)));
                    ps1 = append(ps1, cs[i] + rs1[i] * vector(cos(a - a2), sin(a - a2)));
                    ps2 = append(ps2, cs[i] + rs2[i] * vector(cos(a - a2), sin(a - a2)));
                    ps = append(ps, cs[ip] - rs[ip] * vector(cos(a - a2), sin(a - a2)));
                    ps1 = append(ps1, cs[ip] - rs1[ip] * vector(cos(a - a2), sin(a - a2)));
                    ps2 = append(ps2, cs[ip] - rs2[ip] * vector(cos(a - a2), sin(a - a2)));
                }
                else if (!gs[i].i && !gs[ip].i)
                {
                    angs = append(append(angs, a - a1), a - a1);
                    ps = append(ps, cs[i] + rs[i] * vector(cos(a - a1), sin(a - a1)));
                    ps1 = append(ps1, cs[i] + rs1[i] * vector(cos(a - a1), sin(a - a1)));
                    ps2 = append(ps2, cs[i] + rs2[i] * vector(cos(a - a1), sin(a - a1)));
                    ps = append(ps, cs[ip] + rs[ip] * vector(cos(a - a1), sin(a - a1)));
                    ps1 = append(ps1, cs[ip] + rs1[ip] * vector(cos(a - a1), sin(a - a1)));
                    ps2 = append(ps2, cs[ip] + rs2[ip] * vector(cos(a - a1), sin(a - a1)));
                }
            }
            angs = append(subArray(angs, 1, size(angs)), angs[0]);
            var sk = newSketchOnPlane(context, id + "sketch1", { "sketchPlane" : p });
            var ls = [0 * meter];
            for (var i = 0; i < size(cs); i += 1)
            {
                skLineSegment(sk, "1l" ~ i, { "start" : ps1[2 * i], "end" : ps1[2 * i + 1] });
                skLineSegment(sk, "2l" ~ i, { "start" : ps2[2 * i], "end" : ps2[2 * i + 1] });
                ls = append(ls, last(ls) + norm(ps[2 * i + 1] - ps[2 * i]));
                var ad = (angs[2 * i + 1] - angs[2 * i]) / radian;
                var ip = (i + 1) % size(cs);
                var am = gs[ip].i ? (angs[2 * i + 1] + (PI - ad / 2) % PI * radian) : (angs[2 * i] + (PI + ad / 2) % PI * radian);
                skArc(sk, "1a" ~ i, { "start" : ps1[2 * i + 1], "mid" : cs[ip] + rs1[ip] * vector(cos(am), sin(am)), "end" : ps1[2 * ip] });
                skArc(sk, "2a" ~ i, { "start" : ps2[2 * i + 1], "mid" : cs[ip] + rs2[ip] * vector(cos(am), sin(am)), "end" : ps2[2 * ip] });
                ls = append(ls, last(ls) + fs[ip] * rs[ip] * (gs[ip].i ? (2 * PI - ad) % (2 * PI) : (2 * PI + ad) % (2 * PI)));
            }
            reportFeatureInfo(context, id, "Pitch Length: " ~ roundToPrecision((last(ls) / inch),3) ~ " in");
            var nt = (last(ls) / pitch % 1 < 0.1) ? floor(last(ls) / pitch) : ceil(last(ls) / pitch);

            if (!definition.teeth)
            {
                skSolve(sk);
                var skq = qContainsPoint(qSketchRegion(id + "sketch1"), planeToWorld(p, ps[0]));
                opExtrude(context, id + "extrude1", { "entities" : skq, "direction" : p.normal, "endBound" : BoundingType.BLIND, "endDepth" : w / 2, "startBound" : BoundingType.BLIND, "startDepth" : w / 2 });
                setProperty(context, { "entities" : qCreatedBy(id + "extrude1", EntityType.BODY), "propertyType" : PropertyType.NAME, "value" : nt ~ "L " ~ chainName ~ " Chain" });
                setProperty(context, { "entities" : qCreatedBy(id + "extrude1", EntityType.BODY), "propertyType" : PropertyType.APPEARANCE, "value" : color(0.5, 0.5, 0.5) });
                setProperty(context, { "entities" : qCreatedBy(id + "extrude1", EntityType.BODY), "propertyType" : PropertyType.MATERIAL,
                            "value" : material("Steel", 7.8 * gram / centimeter ^ 3) });
                setProperty(context, { "entities" : qCreatedBy(id + "extrude1", EntityType.BODY), "propertyType" : PropertyType.MASS_OVERRIDE, "value" : nt * cd.m });
                opDeleteBodies(context, id + "delete1", { "entities" : qCreatedBy(id + "sketch1") });
            }
            else
            {
                var ts = [];
                for (var i = 0; i < nt; i += 1)
                {
                    for (var j = 0; j < size(ps); j += 1)
                    {
                        if (i * last(ls) / nt < ls[j + 1])
                        {
                            var jp = floor((j + 1) / 2) % size(cs);
                            if (j % 2 == 0)
                            {
                                var v = normalize(ps[j + 1] - ps[j]);
                                ts = append(ts, planeToWorld(p, ps[j] + v * (i * last(ls) / nt - ls[j])));

                            }
                            else
                            {
                                var a = angs[j - 1] + (i * last(ls) / nt - ls[j]) * (gs[jp].i ? -1 : 1) * radian / (fs[jp] * rs[jp]);
                                ts = append(ts, planeToWorld(p, cs[jp] + rs[jp] * vector(cos(a), sin(a))));
                            }
                            break;
                        }
                    }
                }
                var ins = newInstantiator(id + "instantiator1");
                var cq = qNothing();
                for (var i = 0; i < nt - nt % 2; i += 1)
                {
                    var ip = (i + 1) % nt;
                    if (i % 2 == 0)
                    {
                        cq = qUnion(cq, addInstance(ins, Link::build, { "configuration" : { "Pitch" : cd.conf, "Type" : Link::Type_conf.Inner },
                                        "transform" : toWorld(coordSystem(ts[i], ts[ip] - ts[i], p.normal)) }));
                    }
                    else
                    {
                        cq = qUnion(cq, addInstance(ins, Link::build, { "configuration" : { "Pitch" : cd.conf, "Type" : Link::Type_conf.Outer },
                                        "transform" : toWorld(coordSystem(ts[i], ts[ip] - ts[i], p.normal)) }));
                    }
                }
                if (nt % 2 == 1)
                {
                    cq = qUnion(cq, addInstance(ins, Link::build, { "configuration" : { "Pitch" : cd.conf, "Type" : Link::Type_conf.Half },
                                    "transform" : toWorld(coordSystem(last(ts), ts[0] - last(ts), p.normal)) }));
                }
                instantiate(context, ins);
                opCreateCompositePart(context, id + "part1", { "bodies" : cq, "closed" : true });
                setProperty(context, { "entities" : qCompositePartsContaining(cq), "propertyType" : PropertyType.NAME, "value" : nt ~ "L " ~ chainName ~ " Chain" });
                setProperty(context, { "entities" : qCompositePartsContaining(cq), "propertyType" : PropertyType.MATERIAL,
                            "value" : material("Steel", 7.8 * gram / centimeter ^ 3) });
                setProperty(context, { "entities" : qCompositePartsContaining(cq), "propertyType" : PropertyType.MASS_OVERRIDE, "value" : nt * cd.m });
            }

            //set feature name
            var featureName is string = "";

            // For features to display a '#' you must use '##' :((((((( (took too long to figure out)
            if (definition.chainPitch == chainType.A25 || definition.chainPitch == chainType.A35)
            {
                featureName = nt ~ "L " ~ "#" ~ chainName ~ " Chain - " ~ roundToPrecision((last(ls) / inch), 2) ~ " in";
            }
            else
            {
                featureName = nt ~ "L " ~ chainName ~ " Chain - " ~ roundToPrecision((last(ls) / millimeter), 2) ~ " mm";
            }

            setFeatureComputedParameter(context, id, {
                        "name" : "_name",
                        "value" : featureName
                    });

            //optionally show mate connectors
            if (definition.m)
            {
                //calculate the plane offset (most scuffed code ever)
                var vectorBetweenPlanes = vector(p.origin - pSkEntities.origin);
                var planeOffset = dot(vectorBetweenPlanes, p.normal);

                var mq = definition.teeth ? qCreatedBy(id + "part1", EntityType.BODY) : qCreatedBy(id + "extrude1", EntityType.BODY);
                for (var i = 0; i < size(gs); i += 1)
                {
                    opMateConnector(context, id + ("mate" ~ i), { "coordSystem" : evCurveDefinition(context, { "edge" : gs[i].c }).coordSystem, "owner" : mq });
                    opTransform(context, id + ("transform" ~ i), {
                                "bodies" : qCreatedBy(id + ("mate" ~ i), EntityType.BODY),
                                "transform" : transform(evMateConnector(context, { "mateConnector" : qCreatedBy(id + ("mate" ~ i)) }).zAxis * planeOffset)
                            });

                }
            }
        }
    });
