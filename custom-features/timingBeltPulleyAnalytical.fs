FeatureScript 1803;
import(path : "onshape/std/geometry.fs", version : "1803.0");

annotation { "Feature Type Name" : "Timing Belt Pulley" }
export const timingBeltPulley = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Tooth Profile" }
        definition.toothProfile is ToothProfile;

        annotation { "Name" : "Tooth Count" }
        isInteger(definition.toothCount, TOOTH_COUNT_BOUNDS);

        annotation { "Name" : "Origin", "Filter" : BodyType.MATE_CONNECTOR, "MaxNumberOfPicks" : 1 }
        definition.origin is Query;

        annotation { "Name" : "Center Hole" }
        definition.centerHole is boolean;

        if (definition.centerHole)
        {
            annotation { "Name" : "Hole diameter" }
            isLength(definition.centerHoleDia, CENTERHOLE_BOUNDS);
        }

        annotation { "Name" : "Generate Body" }
        definition.generateBody is boolean;

        if (definition.generateBody)
        {
            annotation { "Name" : "Thickness" }
            isLength(definition.thickness, THICKNESS_BOUNDS);
            
            annotation { "Name" : "Symmetric" }
            definition.symmetric is boolean;
            


        }


    }
    {

        var origin = WORLD_COORD_SYSTEM;
        if (!isQueryEmpty(context, definition.origin))
        {
            origin = evMateConnector(context, {
                        "mateConnector" : evaluateQuery(context, definition.origin)[0]
                    });
        }

        // Extract parameters from profile selection
        var params = ToothProfileDefinitions[definition.toothProfile];

        var PD = definition.toothCount * params["P"] / PI;
        var P = params["P"];
        var RAB = params["R3"];
        var RBC = params["R2"];
        var RCD = params["R1"];
        var b = params["b"];
        var h = params["h"];
        var PLD = params["PLD"];
        var t = definition.toothCount;
        var alpha = 2 * P / PD * radian;


        var AX = (-2 * RAB ^ 2 * (P * t / (2 * PI) - PLD + RAB - h) * sin(radian * PI * b / (P * t)) ^ 2 + RAB ^ 2 * (P * t / (2 * PI) - PLD + RAB - h) + RAB ^ 2 * sqrt(RAB ^ 2 - 2 * RAB * RBC + RBC ^ 2 + 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 4 - 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 2) - 2 * RAB * RBC * (P * t / (2 * PI) - PLD + RAB - h) * sin(radian * PI * b / (P * t)) ^ 2 + RAB * RBC * (P * t / (2 * PI) - PLD + RAB - h) + RAB * RBC * sqrt(RAB ^ 2 - 2 * RAB * RBC + RBC ^ 2 + 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 4 - 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 2) + 256 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 18 - 1152 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 16 + 2112 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 14 + 256 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 12 * cos(radian * PI * b / (P * t)) ^ 6 - 2016 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 12 - 384 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 10 * cos(radian * PI * b / (P * t)) ^ 6 + 1056 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 10 + 192 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 8 * cos(radian * PI * b / (P * t)) ^ 6 - 288 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 8 - 32 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 6 * cos(radian * PI * b / (P * t)) ^ 6 + 32 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 6) * sin(radian * 2 * PI * b / (P * t)) / (RAB ^ 2 - RBC ^ 2);
        var AY = (4 * RAB ^ 2 * (P * t / (2 * PI) - PLD + RAB - h) * sin(radian * PI * b / (P * t)) ^ 4 - 4 * RAB ^ 2 * (P * t / (2 * PI) - PLD + RAB - h) * sin(radian * PI * b / (P * t)) ^ 2 + RAB ^ 2 * (P * t / (2 * PI) - PLD + RAB - h) - 2 * RAB ^ 2 * sqrt(RAB ^ 2 - 2 * RAB * RBC + RBC ^ 2 + 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 4 - 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 2) * sin(radian * PI * b / (P * t)) ^ 2 + RAB ^ 2 * sqrt(RAB ^ 2 - 2 * RAB * RBC + RBC ^ 2 + 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 4 - 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 2) + 4 * RAB * RBC * (P * t / (2 * PI) - PLD + RAB - h) * sin(radian * PI * b / (P * t)) ^ 4 - 4 * RAB * RBC * (P * t / (2 * PI) - PLD + RAB - h) * sin(radian * PI * b / (P * t)) ^ 2 - 2 * RAB * RBC * sqrt(RAB ^ 2 - 2 * RAB * RBC + RBC ^ 2 + 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 4 - 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 2) * sin(radian * PI * b / (P * t)) ^ 2 + RAB * RBC * sqrt(RAB ^ 2 - 2 * RAB * RBC + RBC ^ 2 + 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 4 - 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 2) - RBC ^ 2 * (P * t / (2 * PI) - PLD + RAB - h) - 512 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 20 + 2560 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 18 - 5248 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 16 - 512 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 14 * cos(radian * PI * b / (P * t)) ^ 6 + 5632 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 14 + 1024 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 12 * cos(radian * PI * b / (P * t)) ^ 6 - 3328 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 12 - 640 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 10 * cos(radian * PI * b / (P * t)) ^ 6 + 1024 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 10 + 128 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 8 * cos(radian * PI * b / (P * t)) ^ 6 - 128 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 8) / (RAB ^ 2 - RBC ^ 2);
        var BX = (2 * RAB ^ 2 * (P * t / (2 * PI) - PLD + RAB - h) * sin(radian * PI * b / (P * t)) ^ 2 - RAB ^ 2 * (P * t / (2 * PI) - PLD + RAB - h) - RAB ^ 2 * sqrt(RAB ^ 2 - 2 * RAB * RBC + RBC ^ 2 + 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 4 - 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 2) + 2 * RAB * RBC * (P * t / (2 * PI) - PLD + RAB - h) * sin(radian * PI * b / (P * t)) ^ 2 - RAB * RBC * (P * t / (2 * PI) - PLD + RAB - h) - RAB * RBC * sqrt(RAB ^ 2 - 2 * RAB * RBC + RBC ^ 2 + 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 4 - 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 2) - 256 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 18 + 1152 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 16 - 2112 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 14 - 256 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 12 * cos(radian * PI * b / (P * t)) ^ 6 + 2016 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 12 + 384 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 10 * cos(radian * PI * b / (P * t)) ^ 6 - 1056 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 10 - 192 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 8 * cos(radian * PI * b / (P * t)) ^ 6 + 288 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 8 + 32 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 6 * cos(radian * PI * b / (P * t)) ^ 6 - 32 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 6) * sin(radian * 2 * PI * b / (P * t)) / (RAB ^ 2 - RBC ^ 2);
        var BY = (4 * RAB ^ 2 * (P * t / (2 * PI) - PLD + RAB - h) * sin(radian * PI * b / (P * t)) ^ 4 - 4 * RAB ^ 2 * (P * t / (2 * PI) - PLD + RAB - h) * sin(radian * PI * b / (P * t)) ^ 2 + RAB ^ 2 * (P * t / (2 * PI) - PLD + RAB - h) - 2 * RAB ^ 2 * sqrt(RAB ^ 2 - 2 * RAB * RBC + RBC ^ 2 + 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 4 - 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 2) * sin(radian * PI * b / (P * t)) ^ 2 + RAB ^ 2 * sqrt(RAB ^ 2 - 2 * RAB * RBC + RBC ^ 2 + 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 4 - 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 2) + 4 * RAB * RBC * (P * t / (2 * PI) - PLD + RAB - h) * sin(radian * PI * b / (P * t)) ^ 4 - 4 * RAB * RBC * (P * t / (2 * PI) - PLD + RAB - h) * sin(radian * PI * b / (P * t)) ^ 2 - 2 * RAB * RBC * sqrt(RAB ^ 2 - 2 * RAB * RBC + RBC ^ 2 + 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 4 - 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 2) * sin(radian * PI * b / (P * t)) ^ 2 + RAB * RBC * sqrt(RAB ^ 2 - 2 * RAB * RBC + RBC ^ 2 + 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 4 - 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 2) - RBC ^ 2 * (P * t / (2 * PI) - PLD + RAB - h) - 512 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 20 + 2560 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 18 - 5248 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 16 - 512 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 14 * cos(radian * PI * b / (P * t)) ^ 6 + 5632 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 14 + 1024 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 12 * cos(radian * PI * b / (P * t)) ^ 6 - 3328 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 12 - 640 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 10 * cos(radian * PI * b / (P * t)) ^ 6 + 1024 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 10 + 128 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 8 * cos(radian * PI * b / (P * t)) ^ 6 - 128 * (P * t / (2 * PI) - PLD + RAB - h) ^ 3 * sin(radian * PI * b / (P * t)) ^ 8) / (RAB ^ 2 - RBC ^ 2);
        var ABX = 0 * meter;
        var ABY = P * t / (2 * PI) - PLD + RAB - h;
        var BCX = (-P * t / (2 * PI) + PLD - RAB + h + 2 * (P * t / (2 * PI) - PLD + RAB - h) * sin(radian * PI * b / (P * t)) ^ 2 - sqrt(RAB ^ 2 - 2 * RAB * RBC + RBC ^ 2 + 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 4 - 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 2)) * sin(radian * 2 * PI * b / (P * t));
        var BCY = (P * t / (2 * PI) - PLD + RAB - h - 2 * (P * t / (2 * PI) - PLD + RAB - h) * sin(radian * PI * b / (P * t)) ^ 2 + sqrt(RAB ^ 2 - 2 * RAB * RBC + RBC ^ 2 + 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 4 - 4 * (P * t / (2 * PI) - PLD + RAB - h) ^ 2 * sin(radian * PI * b / (P * t)) ^ 2)) * cos(radian * 2 * PI * b / (P * t));
        var CDX = (4 * BCX ^ 2 + 4 * BCY ^ 2 - 8 * BCY * (-BCX * sqrt(-(4 * BCX ^ 2 + 4 * BCY ^ 2 - P ^ 2 * t ^ 2 / PI ^ 2 + 4 * P * PLD * t / PI - 4 * P * RBC * t / PI - 4 * PLD ^ 2 + 8 * PLD * RBC - 4 * RBC ^ 2) * (4 * BCX ^ 2 + 4 * BCY ^ 2 - P ^ 2 * t ^ 2 / PI ^ 2 + 4 * P * PLD * t / PI + 4 * P * RBC * t / PI + 8 * P * RCD * t / PI - 4 * PLD ^ 2 - 8 * PLD * RBC - 16 * PLD * RCD - 4 * RBC ^ 2 - 16 * RBC * RCD - 16 * RCD ^ 2)) / (8 * (BCX ^ 2 + BCY ^ 2)) + BCY * (4 * BCX ^ 2 + 4 * BCY ^ 2 + P ^ 2 * t ^ 2 / PI ^ 2 - 4 * P * PLD * t / PI - 4 * P * RCD * t / PI + 4 * PLD ^ 2 + 8 * PLD * RCD - 4 * RBC ^ 2 - 8 * RBC * RCD) / (8 * (BCX ^ 2 + BCY ^ 2))) + P ^ 2 * t ^ 2 / PI ^ 2 - 4 * P * PLD * t / PI - 4 * P * RCD * t / PI + 4 * PLD ^ 2 + 8 * PLD * RCD - 4 * RBC ^ 2 - 8 * RBC * RCD) / (8 * BCX);
        var CDY = -BCX * sqrt(-(4 * BCX ^ 2 + 4 * BCY ^ 2 - P ^ 2 * t ^ 2 / PI ^ 2 + 4 * P * PLD * t / PI - 4 * P * RBC * t / PI - 4 * PLD ^ 2 + 8 * PLD * RBC - 4 * RBC ^ 2) * (4 * BCX ^ 2 + 4 * BCY ^ 2 - P ^ 2 * t ^ 2 / PI ^ 2 + 4 * P * PLD * t / PI + 4 * P * RBC * t / PI + 8 * P * RCD * t / PI - 4 * PLD ^ 2 - 8 * PLD * RBC - 16 * PLD * RCD - 4 * RBC ^ 2 - 16 * RBC * RCD - 16 * RCD ^ 2)) / (8 * (BCX ^ 2 + BCY ^ 2)) + BCY * (4 * BCX ^ 2 + 4 * BCY ^ 2 + P ^ 2 * t ^ 2 / PI ^ 2 - 4 * P * PLD * t / PI - 4 * P * RCD * t / PI + 4 * PLD ^ 2 + 8 * PLD * RCD - 4 * RBC ^ 2 - 8 * RBC * RCD) / (8 * (BCX ^ 2 + BCY ^ 2));
        var CX = CDX - RCD * (-BCX + CDX) / (RBC + RCD);
        var CY = CDY - RCD * (-BCY + CDY) / (RBC + RCD);
        var CDR = sqrt(CDX ^ 2 + CDY ^ 2);
        var DX = CDX * (CDR + RCD) / CDR;
        var DY = CDY * (CDR + RCD) / CDR;
        var TANG = -PI / t;
        var EX = -sqrt(DX ^ 2 + DY ^ 2) * sin(radian * 2 * TANG + asin(DX / sqrt(DX ^ 2 + DY ^ 2)));
        var EY = sqrt(DX ^ 2 + DY ^ 2) * cos(radian * 2 * TANG + asin(DX / sqrt(DX ^ 2 + DY ^ 2)));
        var FX = -sqrt(CX ^ 2 + CY ^ 2) * sin(radian * 2 * TANG + asin(CX / sqrt(CX ^ 2 + CY ^ 2)));
        var FY = sqrt(CX ^ 2 + CY ^ 2) * cos(radian * 2 * TANG + asin(CX / sqrt(CX ^ 2 + CY ^ 2)));
        var EFX = -sqrt(CDX ^ 2 + CDY ^ 2) * sin(radian * 2 * TANG + asin(CDX / sqrt(CDX ^ 2 + CDY ^ 2)));
        var EFY = sqrt(CDX ^ 2 + CDY ^ 2) * cos(radian * 2 * TANG + asin(CDX / sqrt(CDX ^ 2 + CDY ^ 2)));
        var GX = -sqrt(BX ^ 2 + BY ^ 2) * sin(radian * 2 * TANG + asin(BX / sqrt(BX ^ 2 + BY ^ 2)));
        var GY = sqrt(BX ^ 2 + BY ^ 2) * cos(radian * 2 * TANG + asin(BX / sqrt(BX ^ 2 + BY ^ 2)));
        var FGX = -sqrt(BCX ^ 2 + BCY ^ 2) * sin(radian * 2 * TANG + asin(BCX / sqrt(BCX ^ 2 + BCY ^ 2)));
        var FGY = sqrt(BCX ^ 2 + BCY ^ 2) * cos(radian * 2 * TANG + asin(BCX / sqrt(BCX ^ 2 + BCY ^ 2)));


        // Create vectors of points
        var A = vector(AX, AY);
        var B = vector(BX, BY);
        var AB = vector(ABX, ABY);
        var C = vector(CX, CY);
        var BC = vector(BCX, BCY);
        var D = vector(DX, DY);
        var CD = vector(CDX, CDY);
        var E = vector(EX, EY);
        var F = vector(FX, FY);
        var EF = vector(EFX, EFY);
        var FG = vector(FGX, FGY);
        var G = vector(GX, GY);

        // Calculate midpoints of arcs since that's how Onshape accepts arcs
        var ABM = getArcMidPointShorter(A, B, AB);
        var BCM = getArcMidPointShorter(B, C, BC);
        var CDM = getArcMidPointShorter(C, D, CD);
        var DEM = getArcMidPointShorter(D, E, vector(0, 0) * meter);
        var EFM = getArcMidPointShorter(E, F, EF);
        var FGM = getArcMidPointShorter(F, G, FG);


        var pulleySketch = newSketchOnPlane(context, id + "pulleySketch", {
                "sketchPlane" : plane(origin)
            });

        if (definition.centerHole)
        {
            if (definition.centerHoleDia / 2 > norm(ABM))
            {
                throw regenError("Center Hole Diameter is too large. Value is: " ~ toString(definition.centerHoleDia) ~ ". Max value is: " ~ toString(2 * norm(ABM)), ["centerHoleDia"]);
            }
            skCircle(pulleySketch, "centerHole", {
                        "center" : vector(0, 0) * millimeter,
                        "radius" : definition.centerHoleDia / 2
                    });
        }

        for (var i = 0; i < t; i += 1)
        {
            var iString = toString(i);
            var rMat = [[cos(i * alpha), -sin(i * alpha)], [sin(i * alpha), cos(i * alpha)]] as Matrix;
            skArc(pulleySketch, "arcAB" ~ iString, {
                        "start" : rMat * A,
                        "mid" : rMat * ABM,
                        "end" : rMat * B
                    });
            skArc(pulleySketch, "arcBC" ~ iString, {
                        "start" : rMat * B,
                        "mid" : rMat * BCM,
                        "end" : rMat * C
                    });
            skArc(pulleySketch, "arcCD" ~ iString, {
                        "start" : rMat * C,
                        "mid" : rMat * CDM,
                        "end" : rMat * D
                    });
            skArc(pulleySketch, "arcDE" ~ iString, {
                        "start" : rMat * D,
                        "mid" : rMat * DEM,
                        "end" : rMat * E
                    });
            skArc(pulleySketch, "arcEF" ~ iString, {
                        "start" : rMat * E,
                        "mid" : rMat * EFM,
                        "end" : rMat * F
                    });
            skArc(pulleySketch, "arcFG" ~ iString, {
                        "start" : rMat * F,
                        "mid" : rMat * FGM,
                        "end" : rMat * G
                    });
        }

        skSolve(pulleySketch);

        if (definition.generateBody)
        {
            var skYAxis = cross(origin.zAxis, origin.xAxis);
            var regionToExtrude = qContainsPoint(qSketchRegion(id + "pulleySketch"), origin.origin + skYAxis * norm(ABM) * (1 - 1e-5));
            
            var extrudeDist = definition.thickness;
            if (definition.symmetric){
                extrudeDist = extrudeDist / 2;
            }
            
            opExtrude(context, id + "extrude1", {
                            "entities" : regionToExtrude,
                            "direction" : evOwnerSketchPlane(context, { "entity" : regionToExtrude }).normal,
                            "endBound" : BoundingType.BLIND,
                            "endDepth" : extrudeDist
                        });
            if (definition.symmetric){
                opExtrude(context, id + "extrude2", {
                            "entities" : regionToExtrude,
                            "direction" : -1*evOwnerSketchPlane(context, { "entity" : regionToExtrude }).normal,
                            "endBound" : BoundingType.BLIND,
                            "endDepth" : extrudeDist
                        });
                opBoolean(context, id + "boolean1", {
                        "tools" : qUnion([qCreatedBy(id + "extrude1", EntityType.BODY), qCreatedBy(id + "extrude2", EntityType.BODY)]),
                        "operationType" : BooleanOperationType.UNION
                });
            }
            
            opDeleteBodies(context, id + "pulleySketchDelete", {
                    "entities" : qCreatedBy(id + "pulleySketch")
            });
        }

    });

function getArcMidPointShorter(start is Vector, end is Vector, center is Vector)
{
    var radius = norm(start - center);
    var mid_vec = ((start - center) + (end - center)) / 2;
    var mv_radius = norm(mid_vec);
    return center + radius * mid_vec / mv_radius;
}



const TOOTH_COUNT_BOUNDS =
{
            (unitless) : [3, 14, 1e5]
        } as IntegerBoundSpec;

const THICKNESS_BOUNDS =
{
            (meter) : [0, 0.0010, 500],
            (centimeter) : .10,
            (millimeter) : 10,
            (inch) : 0.05,
            (foot) : 0.005,
            (yard) : 0.00125
        } as LengthBoundSpec;

const CENTERHOLE_BOUNDS =
{
            (meter) : [1e-5, 0.005, 500],
            (centimeter) : .5,
            (millimeter) : 5.0,
            (inch) : 0.125
        } as LengthBoundSpec;

export enum ToothProfile
{
    annotation { "Name" : "GT2-3M" }
    GT2_3M,
    annotation { "Name" : "GT2-2M" }
    GT2_2M
}

const ToothProfileDefinitions = {
        // See References/gt_belt_diagram.jpg for variable associations
        (ToothProfile.GT2_2M) : {
            "P" : 2 * millimeter,
            "R1" : 0.15 * millimeter,
            "R2" : 1 * millimeter,
            "R3" : 0.555 * millimeter,
            "b" : 0.4 * millimeter,
            "H" : 1.38 * millimeter,
            "h" : 0.75 * millimeter,
            "i" : 0.63 * millimeter,
            "PLD" : 0.254 * millimeter
        },
        // See References/gt_belt_diagram.jpg for variable associations
        (ToothProfile.GT2_3M) : {
            "P" : 3 * millimeter,
            "R1" : 0.25 * millimeter,
            "R2" : 1.52 * millimeter,
            "R3" : 0.85 * millimeter,
            "b" : 0.61 * millimeter,
            "H" : 2.4 * millimeter,
            "h" : 1.14 * millimeter,
            "i" : 1.26 * millimeter,
            "PLD" : 0.381 * millimeter
        },
    };
