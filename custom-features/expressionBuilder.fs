FeatureScript 2892;
/**
 * Expression Builder - Custom Feature
 *
 * Provides a structured UI for composing mathematical expressions and storing
 * the result as a named context variable. Eliminates the need to remember
 * Onshape expression syntax (#variable, function names, unit requirements) by
 * guiding the user through dropdowns and typed numeric fields.
 *
 * Stored variables are readable in any downstream Part Studio field that
 * accepts an expression (type  #variableName  in that field).
 *
 * Expression modes:
 *   ARITHMETIC    - Simple two-operand expression:   A op B
 *   MATH_FUNCTION - Standard math function:          fn(A)  or fn(A, B)
 *   CHAIN         - Up to four operands chained:     A op B op C [op D]
 *
 * Each operand can be:
 *   LITERAL  - a typed numeric field (length, angle, or pure number)
 *   VARIABLE - a text field holding the name of an existing context variable
 */

import(path : "onshape/std/common.fs", version : "2892.0");
import(path : "onshape/std/feature.fs", version : "2892.0");
import(path : "onshape/std/valueBounds.fs", version : "2892.0");
import(path : "onshape/std/math.fs", version : "2892.0");
import(path : "onshape/std/units.fs", version : "2892.0");
import(path : "onshape/std/string.fs", version : "2892.0");
import(path : "onshape/std/error.fs", version : "2892.0");
import(path : "onshape/std/containers.fs", version : "2892.0");

// ---------------------------------------------------------------------------
// Enumerations
// ---------------------------------------------------------------------------

/**
 * The data type of the output variable.
 * Controls which literal-value fields are shown for operands in ARITHMETIC
 * and CHAIN modes (and for compatible MATH_FUNCTION operands).
 */
export enum ExpressionOutputType
{
    annotation { "Name" : "Length" }
    LENGTH,
    annotation { "Name" : "Angle" }
    ANGLE,
    annotation { "Name" : "Number" }
    NUMBER
}

/**
 * Top-level expression structure.
 *   ARITHMETIC    – two operands joined by one operator (A op B)
 *   MATH_FUNCTION – a named math function applied to one or two operands
 *   CHAIN         – two to four operands joined by independent operators
 */
export enum ExpressionBuilderMode
{
    annotation { "Name" : "Arithmetic  (A op B)" }
    ARITHMETIC,
    annotation { "Name" : "Math function  f(A)" }
    MATH_FUNCTION,
    annotation { "Name" : "Chain  (A op B op C...)" }
    CHAIN
}

/**
 * Binary arithmetic operators used in ARITHMETIC and CHAIN modes.
 * Note: MULTIPLY and DIVIDE treat operand B as a dimensionless scalar so
 * that length * scalar = length (rather than length * length = area).
 */
export enum ArithmeticOperation
{
    annotation { "Name" : "Add  (A + B)" }
    ADD,
    annotation { "Name" : "Subtract  (A - B)" }
    SUBTRACT,
    annotation { "Name" : "Multiply  (A * B)" }
    MULTIPLY,
    annotation { "Name" : "Divide  (A / B)" }
    DIVIDE
}

/**
 * Supported single- and dual-argument math functions.
 * Trig functions (SIN, COS, TAN) expect an angle operand.
 * Inverse trig functions (ASIN, ACOS, ATAN, ATAN2) return an angle.
 * All others operate on values whose type matches the declared output type.
 */
export enum MathFunctionType
{
    annotation { "Name" : "Absolute value  |A|" }
    ABS,
    annotation { "Name" : "Square root  sqrt(A) - Number or even-exponent units" }
    SQRT,
    annotation { "Name" : "Floor  floor(A) - Number only" }
    FLOOR,
    annotation { "Name" : "Ceiling  ceil(A) - Number only" }
    CEIL,
    annotation { "Name" : "Round - Number only" }
    ROUND,
    annotation { "Name" : "Sine  sin(A) - A is an angle" }
    SIN,
    annotation { "Name" : "Cosine  cos(A) - A is an angle" }
    COS,
    annotation { "Name" : "Tangent  tan(A) - A is an angle" }
    TAN,
    annotation { "Name" : "Arcsine  asin(A) - returns angle" }
    ASIN,
    annotation { "Name" : "Arccosine  acos(A) - returns angle" }
    ACOS,
    annotation { "Name" : "Arctangent  atan(A) - returns angle" }
    ATAN,
    annotation { "Name" : "Arctangent2  atan2(A, B) - returns angle" }
    ATAN2,
    annotation { "Name" : "Natural log  ln(A) - Number only" }
    LOG,
    annotation { "Name" : "Log base 10  log10(A) - Number only" }
    LOG10,
    annotation { "Name" : "Exponential  exp(A) - Number only" }
    EXP,
    annotation { "Name" : "Minimum  min(A, B)" }
    MIN,
    annotation { "Name" : "Maximum  max(A, B)" }
    MAX
}

/**
 * Where an operand value comes from.
 *   LITERAL  – user enters a numeric value directly in the panel
 *   VARIABLE – user enters the name of an existing context variable to read
 */
export enum OperandInputMode
{
    annotation { "Name" : "Enter a value" }
    LITERAL,
    annotation { "Name" : "Read from variable" }
    VARIABLE
}

/**
 * How many terms the CHAIN mode contains.
 */
export enum ChainLength
{
    annotation { "Name" : "2 terms" }
    TWO,
    annotation { "Name" : "3 terms" }
    THREE,
    annotation { "Name" : "4 terms" }
    FOUR
}

// ---------------------------------------------------------------------------
// Shared numeric bounds
// ---------------------------------------------------------------------------

/** Bounds for dimensionless scalar multipliers / divisors and pure-number operands. */
const EXPRESSION_BUILDER_NUMBER_BOUNDS =
{
    (unitless) : [-1e12, 0, 1e12]
} as RealBoundSpec;

/** Bounds for dimensionless scalars used as the right-hand side of multiply / divide. */
const EXPRESSION_BUILDER_SCALAR_BOUNDS =
{
    (unitless) : [-1e12, 1, 1e12]
} as RealBoundSpec;

// ---------------------------------------------------------------------------
// Feature definition
// ---------------------------------------------------------------------------

/**
 * Expression Builder feature.
 *
 * Builds a mathematical expression from structured UI inputs and stores the
 * result as a named context variable.  The variable can be referenced anywhere
 * in the Part Studio that accepts an expression by typing  #variableName.
 *
 * @param definition {{
 *      @field variableName {string}           : Name of the output variable (no spaces; becomes #variableName).
 *      @field variableDescription {string}    : Optional human-readable description stored alongside the variable.
 *      @field outputType {ExpressionOutputType} : Declared type of the result (LENGTH, ANGLE, or NUMBER).
 *      @field expressionMode {ExpressionBuilderMode} : How the expression is structured.
 *
 *      // ARITHMETIC mode fields
 *      @field operandAMode {OperandInputMode}  : Source for operand A.
 *      @field operandAVariableName {string}    : Variable name to read for operand A (VARIABLE mode).
 *      @field operandALength {ValueWithUnits}  : Literal length for operand A.
 *      @field operandAAngle {ValueWithUnits}   : Literal angle for operand A.
 *      @field operandANumber {number}          : Literal number for operand A.
 *      @field arithmeticOperation {ArithmeticOperation} : The operator to apply.
 *      @field operandBMode {OperandInputMode}  : Source for operand B (ADD / SUBTRACT).
 *      @field operandBVariableName {string}    : Variable name to read for operand B (ADD / SUBTRACT, VARIABLE mode).
 *      @field operandBLength {ValueWithUnits}  : Literal length for operand B (ADD / SUBTRACT).
 *      @field operandBAngle {ValueWithUnits}   : Literal angle for operand B (ADD / SUBTRACT).
 *      @field operandBNumber {number}          : Literal number for operand B (ADD / SUBTRACT or NUMBER output).
 *      @field operandBScalarMode {OperandInputMode} : Source for scalar operand B (MULTIPLY / DIVIDE).
 *      @field operandBScalarVariableName {string}  : Variable name for scalar operand B (MULTIPLY / DIVIDE, VARIABLE mode).
 *      @field operandBScalar {number}          : Literal scalar for operand B (MULTIPLY / DIVIDE).
 *
 *      // MATH_FUNCTION mode fields
 *      @field mathFunction {MathFunctionType}  : The function to apply.
 *      @field funcOperandAMode {OperandInputMode} : Source for the primary function argument.
 *      @field funcOperandAVariableName {string}: Variable name for the primary argument (VARIABLE mode).
 *      @field funcOperandAAngle {ValueWithUnits}: Literal angle for the primary argument (trig functions).
 *      @field funcOperandANumber {number}       : Literal number for the primary argument (inverse trig / NUMBER output).
 *      @field funcOperandALength {ValueWithUnits}: Literal length for the primary argument (LENGTH output).
 *      @field funcOperandBMode {OperandInputMode}: Source for the secondary function argument (MIN, MAX, ATAN2).
 *      @field funcOperandBVariableName {string} : Variable name for secondary argument.
 *      @field funcOperandBAngle {ValueWithUnits}: Literal angle for secondary argument.
 *      @field funcOperandBNumber {number}       : Literal number for secondary argument.
 *      @field funcOperandBLength {ValueWithUnits}: Literal length for secondary argument.
 *
 *      // CHAIN mode fields
 *      @field chainLength {ChainLength}         : Number of terms (TWO, THREE, or FOUR).
 *      @field chainTerm1Mode … chainTerm4Mode {OperandInputMode}: Source for each term.
 *      @field chainTerm1VariableName … chainTerm4VariableName {string}: Variable names for each term.
 *      @field chainTerm1Length … chainTerm4Length {ValueWithUnits}: Literal lengths for each term.
 *      @field chainTerm1Angle … chainTerm4Angle {ValueWithUnits}: Literal angles for each term.
 *      @field chainTerm1Number … chainTerm4Number {number}: Literal numbers for each term.
 *      @field chainOp1 … chainOp3 {ArithmeticOperation}: Operators between consecutive terms.
 * }}
 */
annotation { "Feature Type Name" : "Expression Builder",
             "Feature Name Template" : "###variableName",
             "UIHint" : UIHint.NO_PREVIEW_PROVIDED,
             "Feature Type Description" : "Compose a mathematical expression and store the result as a named variable readable anywhere in the Part Studio with the #variableName syntax." }
export const expressionBuilder = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        // ---------------------------------------------------------------
        // Output variable identity
        // ---------------------------------------------------------------
        annotation { "Name" : "Variable name",
                     "Description" : "Name of the output variable. Use #variableName in any expression field to reference it.",
                     "UIHint" : [UIHint.VARIABLE_NAME, UIHint.UNCONFIGURABLE],
                     "MaxLength" : 256 }
        definition.variableName is string;

        annotation { "Name" : "Description",
                     "Description" : "Optional human-readable label stored with the variable.",
                     "MaxLength" : 256 }
        definition.variableDescription is string;

        // ---------------------------------------------------------------
        // Output type and expression structure
        // ---------------------------------------------------------------
        annotation { "Name" : "Output type",
                     "Description" : "The data type of the computed result. Controls which literal-value fields are shown.",
                     "UIHint" : UIHint.HORIZONTAL_ENUM }
        definition.outputType is ExpressionOutputType;

        annotation { "Name" : "Expression mode",
                     "Description" : "How to structure the expression: two-operand arithmetic, a named function, or a chain of up to four operands.",
                     "UIHint" : UIHint.HORIZONTAL_ENUM }
        definition.expressionMode is ExpressionBuilderMode;

        // ===============================================================
        // ARITHMETIC MODE  (A op B)
        // ===============================================================
        if (definition.expressionMode == ExpressionBuilderMode.ARITHMETIC)
        {
            // --- Operand A ---
            annotation { "Group Name" : "Operand A", "Collapsed By Default" : false }
            {
                annotation { "Name" : "Source",
                             "Description" : "Enter a typed value, or read from an existing context variable.",
                             "UIHint" : UIHint.HORIZONTAL_ENUM }
                definition.operandAMode is OperandInputMode;

                if (definition.operandAMode == OperandInputMode.VARIABLE)
                {
                    annotation { "Name" : "Variable name",
                                 "Description" : "Name of the context variable to read (do not include #).",
                                 "MaxLength" : 256 }
                    definition.operandAVariableName is string;
                }
                else
                {
                    if (definition.outputType == ExpressionOutputType.LENGTH)
                    {
                        annotation { "Name" : "Value" }
                        isLength(definition.operandALength, ZERO_DEFAULT_LENGTH_BOUNDS);
                    }
                    else if (definition.outputType == ExpressionOutputType.ANGLE)
                    {
                        annotation { "Name" : "Value" }
                        isAngle(definition.operandAAngle, ANGLE_360_ZERO_DEFAULT_BOUNDS);
                    }
                    else
                    {
                        annotation { "Name" : "Value" }
                        isReal(definition.operandANumber, EXPRESSION_BUILDER_NUMBER_BOUNDS);
                    }
                }
            }

            // --- Operator ---
            annotation { "Name" : "Operation",
                         "Description" : "Arithmetic operator. For Multiply and Divide, operand B is treated as a dimensionless scalar.",
                         "UIHint" : UIHint.HORIZONTAL_ENUM }
            definition.arithmeticOperation is ArithmeticOperation;

            // --- Operand B ---
            annotation { "Group Name" : "Operand B", "Collapsed By Default" : false }
            {
                // Multiply and Divide: B is always a dimensionless scalar so the
                // output preserves the units of A (length × scalar = length, etc.)
                if (definition.arithmeticOperation == ArithmeticOperation.MULTIPLY ||
                    definition.arithmeticOperation == ArithmeticOperation.DIVIDE)
                {
                    annotation { "Name" : "Source",
                                 "Description" : "Dimensionless scalar multiplier or divisor.",
                                 "UIHint" : UIHint.HORIZONTAL_ENUM }
                    definition.operandBScalarMode is OperandInputMode;

                    if (definition.operandBScalarMode == OperandInputMode.VARIABLE)
                    {
                        annotation { "Name" : "Variable name",
                                     "Description" : "Name of the context variable holding the scalar (do not include #).",
                                     "MaxLength" : 256 }
                        definition.operandBScalarVariableName is string;
                    }
                    else
                    {
                        annotation { "Name" : "Scalar value",
                                     "Description" : "Dimensionless multiplier or divisor." }
                        isReal(definition.operandBScalar, EXPRESSION_BUILDER_SCALAR_BOUNDS);
                    }
                }
                else
                {
                    // Add / Subtract: B has the same type as A
                    annotation { "Name" : "Source",
                                 "Description" : "Enter a typed value, or read from an existing context variable.",
                                 "UIHint" : UIHint.HORIZONTAL_ENUM }
                    definition.operandBMode is OperandInputMode;

                    if (definition.operandBMode == OperandInputMode.VARIABLE)
                    {
                        annotation { "Name" : "Variable name",
                                     "Description" : "Name of the context variable to read (do not include #).",
                                     "MaxLength" : 256 }
                        definition.operandBVariableName is string;
                    }
                    else
                    {
                        if (definition.outputType == ExpressionOutputType.LENGTH)
                        {
                            annotation { "Name" : "Value" }
                            isLength(definition.operandBLength, ZERO_DEFAULT_LENGTH_BOUNDS);
                        }
                        else if (definition.outputType == ExpressionOutputType.ANGLE)
                        {
                            annotation { "Name" : "Value" }
                            isAngle(definition.operandBAngle, ANGLE_360_ZERO_DEFAULT_BOUNDS);
                        }
                        else
                        {
                            annotation { "Name" : "Value" }
                            isReal(definition.operandBNumber, EXPRESSION_BUILDER_NUMBER_BOUNDS);
                        }
                    }
                }
            }
        }

        // ===============================================================
        // MATH_FUNCTION MODE  ( fn(A)  or  fn(A, B) )
        // ===============================================================
        else if (definition.expressionMode == ExpressionBuilderMode.MATH_FUNCTION)
        {
            annotation { "Name" : "Function",
                         "Description" : "SIN / COS / TAN expect an angle input. ASIN / ACOS / ATAN / ATAN2 return an angle. MIN, MAX, and ATAN2 require a second operand B." }
            definition.mathFunction is MathFunctionType;

            // --- Primary operand (A) ---
            annotation { "Group Name" : "Operand A", "Collapsed By Default" : false }
            {
                annotation { "Name" : "Source",
                             "Description" : "Enter a typed value, or read from an existing context variable.",
                             "UIHint" : UIHint.HORIZONTAL_ENUM }
                definition.funcOperandAMode is OperandInputMode;

                if (definition.funcOperandAMode == OperandInputMode.VARIABLE)
                {
                    annotation { "Name" : "Variable name",
                                 "Description" : "Name of the context variable to read (do not include #).",
                                 "MaxLength" : 256 }
                    definition.funcOperandAVariableName is string;
                }
                else
                {
                    // The input type for operand A is determined by the selected function
                    // first, then by the declared output type for general-purpose functions.
                    //
                    // Angle input: SIN / COS / TAN always require an angle operand.
                    // Any other function whose output type is ANGLE (e.g. ABS on an angle)
                    // also takes an angle — except inverse-trig (ASIN / ACOS / ATAN / ATAN2)
                    // which return an angle but accept a dimensionless number as input.
                    if ((definition.mathFunction == MathFunctionType.SIN ||
                         definition.mathFunction == MathFunctionType.COS ||
                         definition.mathFunction == MathFunctionType.TAN) ||
                        (definition.outputType == ExpressionOutputType.ANGLE &&
                         definition.mathFunction != MathFunctionType.ASIN &&
                         definition.mathFunction != MathFunctionType.ACOS &&
                         definition.mathFunction != MathFunctionType.ATAN &&
                         definition.mathFunction != MathFunctionType.ATAN2))
                    {
                        annotation { "Name" : "Angle value",
                                     "Description" : "Input angle for the trigonometric function." }
                        isAngle(definition.funcOperandAAngle, ANGLE_360_ZERO_DEFAULT_BOUNDS);
                    }
                    else if (definition.outputType == ExpressionOutputType.LENGTH)
                    {
                        annotation { "Name" : "Length value" }
                        isLength(definition.funcOperandALength, ZERO_DEFAULT_LENGTH_BOUNDS);
                    }
                    else
                    {
                        // Covers ASIN / ACOS / ATAN / ATAN2 (dimensionless ratio input)
                        // and any remaining function whose output type is NUMBER.
                        annotation { "Name" : "Number value",
                                     "Description" : "Dimensionless ratio (asin / acos expect -1 to 1)." }
                        isReal(definition.funcOperandANumber, EXPRESSION_BUILDER_NUMBER_BOUNDS);
                    }
                }
            }

            // --- Secondary operand (B) — only for binary functions ---
            if (definition.mathFunction == MathFunctionType.MIN ||
                definition.mathFunction == MathFunctionType.MAX ||
                definition.mathFunction == MathFunctionType.ATAN2)
            {
                annotation { "Group Name" : "Operand B", "Collapsed By Default" : false }
                {
                    annotation { "Name" : "Source",
                                 "Description" : "Enter a typed value, or read from an existing context variable.",
                                 "UIHint" : UIHint.HORIZONTAL_ENUM }
                    definition.funcOperandBMode is OperandInputMode;

                    if (definition.funcOperandBMode == OperandInputMode.VARIABLE)
                    {
                        annotation { "Name" : "Variable name",
                                     "Description" : "Name of the context variable to read (do not include #).",
                                     "MaxLength" : 256 }
                        definition.funcOperandBVariableName is string;
                    }
                    else
                    {
                        // ATAN2(y, x) - both arguments are dimensionless ratios.
                        // For MIN / MAX, the second operand matches the declared output type.
                        if (definition.mathFunction == MathFunctionType.ATAN2 ||
                            definition.outputType == ExpressionOutputType.NUMBER)
                        {
                            annotation { "Name" : "Number value",
                                         "Description" : "Second dimensionless argument for atan2(A, B)." }
                            isReal(definition.funcOperandBNumber, EXPRESSION_BUILDER_NUMBER_BOUNDS);
                        }
                        else if (definition.outputType == ExpressionOutputType.LENGTH)
                        {
                            annotation { "Name" : "Length value" }
                            isLength(definition.funcOperandBLength, ZERO_DEFAULT_LENGTH_BOUNDS);
                        }
                        else
                        {
                            annotation { "Name" : "Angle value" }
                            isAngle(definition.funcOperandBAngle, ANGLE_360_ZERO_DEFAULT_BOUNDS);
                        }
                    }
                }
            }
        }

        // ===============================================================
        // CHAIN MODE  ( A op1 B  [op2 C  [op3 D]] )
        // ===============================================================
        else if (definition.expressionMode == ExpressionBuilderMode.CHAIN)
        {
            annotation { "Name" : "Number of terms",
                         "Description" : "How many operands to chain together.",
                         "UIHint" : UIHint.HORIZONTAL_ENUM }
            definition.chainLength is ChainLength;

            // --- Term 1 ---
            annotation { "Group Name" : "Term 1", "Collapsed By Default" : false }
            {
                annotation { "Name" : "Source", "UIHint" : UIHint.HORIZONTAL_ENUM }
                definition.chainTerm1Mode is OperandInputMode;

                if (definition.chainTerm1Mode == OperandInputMode.VARIABLE)
                {
                    annotation { "Name" : "Variable name", "MaxLength" : 256 }
                    definition.chainTerm1VariableName is string;
                }
                else
                {
                    if (definition.outputType == ExpressionOutputType.LENGTH)
                    {
                        annotation { "Name" : "Value" }
                        isLength(definition.chainTerm1Length, ZERO_DEFAULT_LENGTH_BOUNDS);
                    }
                    else if (definition.outputType == ExpressionOutputType.ANGLE)
                    {
                        annotation { "Name" : "Value" }
                        isAngle(definition.chainTerm1Angle, ANGLE_360_ZERO_DEFAULT_BOUNDS);
                    }
                    else
                    {
                        annotation { "Name" : "Value" }
                        isReal(definition.chainTerm1Number, EXPRESSION_BUILDER_NUMBER_BOUNDS);
                    }
                }
            }

            // --- Operation 1 (between term 1 and term 2) ---
            annotation { "Name" : "Operation 1",
                         "Description" : "Operator applied between Term 1 and Term 2. Multiply / Divide treat the right-hand term as a dimensionless scalar.",
                         "UIHint" : UIHint.HORIZONTAL_ENUM }
            definition.chainOp1 is ArithmeticOperation;

            // --- Term 2 ---
            annotation { "Group Name" : "Term 2", "Collapsed By Default" : false }
            {
                annotation { "Name" : "Source", "UIHint" : UIHint.HORIZONTAL_ENUM }
                definition.chainTerm2Mode is OperandInputMode;

                if (definition.chainTerm2Mode == OperandInputMode.VARIABLE)
                {
                    annotation { "Name" : "Variable name", "MaxLength" : 256 }
                    definition.chainTerm2VariableName is string;
                }
                else
                {
                    if (definition.outputType == ExpressionOutputType.LENGTH)
                    {
                        annotation { "Name" : "Value" }
                        isLength(definition.chainTerm2Length, ZERO_DEFAULT_LENGTH_BOUNDS);
                    }
                    else if (definition.outputType == ExpressionOutputType.ANGLE)
                    {
                        annotation { "Name" : "Value" }
                        isAngle(definition.chainTerm2Angle, ANGLE_360_ZERO_DEFAULT_BOUNDS);
                    }
                    else
                    {
                        annotation { "Name" : "Value" }
                        isReal(definition.chainTerm2Number, EXPRESSION_BUILDER_NUMBER_BOUNDS);
                    }
                }
            }

            // --- Term 3 and Operation 2 (only when chainLength >= THREE) ---
            if (definition.chainLength == ChainLength.THREE ||
                definition.chainLength == ChainLength.FOUR)
            {
                annotation { "Name" : "Operation 2",
                             "Description" : "Operator applied between Term 2 and Term 3.",
                             "UIHint" : UIHint.HORIZONTAL_ENUM }
                definition.chainOp2 is ArithmeticOperation;

                annotation { "Group Name" : "Term 3", "Collapsed By Default" : false }
                {
                    annotation { "Name" : "Source", "UIHint" : UIHint.HORIZONTAL_ENUM }
                    definition.chainTerm3Mode is OperandInputMode;

                    if (definition.chainTerm3Mode == OperandInputMode.VARIABLE)
                    {
                        annotation { "Name" : "Variable name", "MaxLength" : 256 }
                        definition.chainTerm3VariableName is string;
                    }
                    else
                    {
                        if (definition.outputType == ExpressionOutputType.LENGTH)
                        {
                            annotation { "Name" : "Value" }
                            isLength(definition.chainTerm3Length, ZERO_DEFAULT_LENGTH_BOUNDS);
                        }
                        else if (definition.outputType == ExpressionOutputType.ANGLE)
                        {
                            annotation { "Name" : "Value" }
                            isAngle(definition.chainTerm3Angle, ANGLE_360_ZERO_DEFAULT_BOUNDS);
                        }
                        else
                        {
                            annotation { "Name" : "Value" }
                            isReal(definition.chainTerm3Number, EXPRESSION_BUILDER_NUMBER_BOUNDS);
                        }
                    }
                }
            }

            // --- Term 4 and Operation 3 (only when chainLength == FOUR) ---
            if (definition.chainLength == ChainLength.FOUR)
            {
                annotation { "Name" : "Operation 3",
                             "Description" : "Operator applied between Term 3 and Term 4.",
                             "UIHint" : UIHint.HORIZONTAL_ENUM }
                definition.chainOp3 is ArithmeticOperation;

                annotation { "Group Name" : "Term 4", "Collapsed By Default" : false }
                {
                    annotation { "Name" : "Source", "UIHint" : UIHint.HORIZONTAL_ENUM }
                    definition.chainTerm4Mode is OperandInputMode;

                    if (definition.chainTerm4Mode == OperandInputMode.VARIABLE)
                    {
                        annotation { "Name" : "Variable name", "MaxLength" : 256 }
                        definition.chainTerm4VariableName is string;
                    }
                    else
                    {
                        if (definition.outputType == ExpressionOutputType.LENGTH)
                        {
                            annotation { "Name" : "Value" }
                            isLength(definition.chainTerm4Length, ZERO_DEFAULT_LENGTH_BOUNDS);
                        }
                        else if (definition.outputType == ExpressionOutputType.ANGLE)
                        {
                            annotation { "Name" : "Value" }
                            isAngle(definition.chainTerm4Angle, ANGLE_360_ZERO_DEFAULT_BOUNDS);
                        }
                        else
                        {
                            annotation { "Name" : "Value" }
                            isReal(definition.chainTerm4Number, EXPRESSION_BUILDER_NUMBER_BOUNDS);
                        }
                    }
                }
            }
        }
    }

    // =========================================================================
    // Execution body
    // =========================================================================
    {
        // Validate variable name
        if (definition.variableName == "")
        {
            throw regenError("Variable name must not be empty.", ["variableName"]);
        }

        var result;

        if (definition.expressionMode == ExpressionBuilderMode.ARITHMETIC)
        {
            result = evaluateArithmeticExpression(context, definition);
        }
        else if (definition.expressionMode == ExpressionBuilderMode.MATH_FUNCTION)
        {
            result = evaluateMathFunctionExpression(context, definition);
        }
        else if (definition.expressionMode == ExpressionBuilderMode.CHAIN)
        {
            result = evaluateChainExpression(context, definition);
        }

        // Store the computed value as a named context variable
        setVariable(context, definition.variableName, result, definition.variableDescription);

        // Report the evaluated result and a copy-pasteable expression string to the user
        var expressionString = "";
        if (definition.expressionMode == ExpressionBuilderMode.ARITHMETIC)
        {
            expressionString = buildArithmeticExpressionString(definition);
        }
        else if (definition.expressionMode == ExpressionBuilderMode.MATH_FUNCTION)
        {
            expressionString = buildMathFunctionExpressionString(definition);
        }
        else if (definition.expressionMode == ExpressionBuilderMode.CHAIN)
        {
            expressionString = buildChainExpressionString(definition);
        }
        else
        {
            expressionString = "(unknown expression mode)";
        }

        const resultStr = formatResultValue(result, definition.outputType);
        reportFeatureInfo(context, id,
            "Result: " ~ resultStr ~
            "\nExpression: " ~ expressionString);
    });

// ---------------------------------------------------------------------------
// Operand resolution helpers
// ---------------------------------------------------------------------------

/**
 * Reads a named context variable and returns its value.
 * Throws a descriptive regeneration error when the variable is not found so
 * that users get a clear message rather than a generic crash.
 *
 * @param context        : Active build context.
 * @param variableName   : Name of the variable to retrieve (without the # prefix).
 * @param operandLabel   : Human-readable label used in the error message (e.g. "Operand A").
 * @returns              : The stored variable value.
 */
function readContextVariable(context is Context, variableName is string, operandLabel is string)
{
    const value = try(getVariable(context, variableName));
    if (value == undefined)
    {
        throw regenError("Cannot find variable '" ~ variableName ~ "' for " ~ operandLabel ~
                         ". Ensure the variable is defined earlier in the feature tree.");
    }
    return value;
}

/**
 * Resolves a typed operand for ARITHMETIC and CHAIN modes.
 * When mode is LITERAL the literal value stored in the definition is returned.
 * When mode is VARIABLE the named context variable is retrieved and returned.
 *
 * @param context        : Active build context.
 * @param operandMode    : OperandInputMode (LITERAL or VARIABLE).
 * @param variableName   : Context variable name (used when mode is VARIABLE).
 * @param literalValue   : Pre-read literal value from the definition (used when mode is LITERAL).
 * @param operandLabel   : Human-readable label for error messages.
 * @returns              : The resolved operand value (length, angle, or number).
 */
function resolveTypedOperand(context is Context, operandMode is OperandInputMode,
                             variableName is string, literalValue, operandLabel is string)
{
    if (operandMode == OperandInputMode.VARIABLE)
    {
        return readContextVariable(context, variableName, operandLabel);
    }
    return literalValue;
}

/**
 * Applies an ArithmeticOperation to two already-resolved operands.
 * For MULTIPLY and DIVIDE the right-hand operand is treated as a dimensionless
 * scalar (consistent with the UI labelling).
 *
 * @param leftOperand  : Left-hand operand value.
 * @param rightOperand : Right-hand operand value (scalar for multiply / divide).
 * @param operation    : ArithmeticOperation enum value.
 * @returns            : Computed result.
 */
function applyArithmeticOperation(leftOperand, rightOperand, operation is ArithmeticOperation)
{
    if (operation == ArithmeticOperation.ADD)
    {
        return leftOperand + rightOperand;
    }
    else if (operation == ArithmeticOperation.SUBTRACT)
    {
        return leftOperand - rightOperand;
    }
    else if (operation == ArithmeticOperation.MULTIPLY)
    {
        return leftOperand * rightOperand;
    }
    else // DIVIDE
    {
        if (rightOperand == 0)
        {
            throw regenError("Division by zero: the scalar divisor evaluated to 0.");
        }
        return leftOperand / rightOperand;
    }
}

// ---------------------------------------------------------------------------
// Mode evaluation functions
// ---------------------------------------------------------------------------

/**
 * Evaluates an ARITHMETIC mode expression (A op B) and returns the result.
 *
 * @param context    : Active build context.
 * @param definition : Feature definition map.
 * @returns          : Computed result value.
 */
function evaluateArithmeticExpression(context is Context, definition is map)
{
    // --- Resolve operand A ---
    var literalA;
    if (definition.outputType == ExpressionOutputType.LENGTH)
    {
        literalA = definition.operandALength;
    }
    else if (definition.outputType == ExpressionOutputType.ANGLE)
    {
        literalA = definition.operandAAngle;
    }
    else
    {
        literalA = definition.operandANumber;
    }

    const operandA = resolveTypedOperand(context,
                                         definition.operandAMode,
                                         definition.operandAVariableName,
                                         literalA,
                                         "Operand A");

    // --- Resolve operand B ---
    var operandB;
    const operation = definition.arithmeticOperation;

    if (operation == ArithmeticOperation.MULTIPLY || operation == ArithmeticOperation.DIVIDE)
    {
        // B is always a dimensionless scalar for multiply / divide
        operandB = resolveTypedOperand(context,
                                       definition.operandBScalarMode,
                                       definition.operandBScalarVariableName,
                                       definition.operandBScalar,
                                       "Operand B (scalar)");
    }
    else
    {
        // ADD or SUBTRACT: B matches the output type
        var literalB;
        if (definition.outputType == ExpressionOutputType.LENGTH)
        {
            literalB = definition.operandBLength;
        }
        else if (definition.outputType == ExpressionOutputType.ANGLE)
        {
            literalB = definition.operandBAngle;
        }
        else
        {
            literalB = definition.operandBNumber;
        }

        operandB = resolveTypedOperand(context,
                                       definition.operandBMode,
                                       definition.operandBVariableName,
                                       literalB,
                                       "Operand B");
    }

    return applyArithmeticOperation(operandA, operandB, operation);
}

/**
 * Evaluates a MATH_FUNCTION mode expression fn(A) or fn(A, B) and returns the result.
 *
 * @param context    : Active build context.
 * @param definition : Feature definition map.
 * @returns          : Computed result value.
 */
function evaluateMathFunctionExpression(context is Context, definition is map)
{
    const mathFunction = definition.mathFunction;

    // --- Resolve primary operand A ---
    // The literal field depends on the selected function (trig vs. inverse trig vs. generic)
    var literalA;
    if (mathFunction == MathFunctionType.SIN ||
        mathFunction == MathFunctionType.COS ||
        mathFunction == MathFunctionType.TAN)
    {
        literalA = definition.funcOperandAAngle;
    }
    else if (mathFunction == MathFunctionType.ASIN ||
             mathFunction == MathFunctionType.ACOS ||
             mathFunction == MathFunctionType.ATAN ||
             mathFunction == MathFunctionType.ATAN2)
    {
        literalA = definition.funcOperandANumber;
    }
    else if (definition.outputType == ExpressionOutputType.LENGTH)
    {
        literalA = definition.funcOperandALength;
    }
    else if (definition.outputType == ExpressionOutputType.ANGLE)
    {
        literalA = definition.funcOperandAAngle;
    }
    else
    {
        literalA = definition.funcOperandANumber;
    }

    const operandA = resolveTypedOperand(context,
                                         definition.funcOperandAMode,
                                         definition.funcOperandAVariableName,
                                         literalA,
                                         "Operand A");

    // Guard: FLOOR, CEIL, ROUND, LOG, LOG10, and EXP only accept dimensionless numbers.
    // SQRT accepts a dimensionless number or a ValueWithUnits whose unit exponents are all even.
    // When Output type is set to Length or Angle, these functions cannot be applied correctly.
    // Raise a clear error rather than letting FeatureScript crash with an opaque message.
    const numberOnlyFunctions = [
        MathFunctionType.FLOOR,
        MathFunctionType.CEIL,
        MathFunctionType.ROUND,
        MathFunctionType.LOG,
        MathFunctionType.LOG10,
        MathFunctionType.EXP,
        MathFunctionType.SQRT
    ];

    for (var numberOnlyFunction in numberOnlyFunctions)
    {
        if (mathFunction == numberOnlyFunction &&
            definition.outputType != ExpressionOutputType.NUMBER)
        {
            throw regenError("The selected math function requires a dimensionless number. "
                             ~ "Set Output type to Number and ensure the operand is a pure number.");
        }
    }

    // --- Apply single-argument functions ---
    if (mathFunction == MathFunctionType.ABS)
    {
        return abs(operandA);
    }
    else if (mathFunction == MathFunctionType.SQRT)
    {
        return sqrt(operandA);
    }
    else if (mathFunction == MathFunctionType.FLOOR)
    {
        return floor(operandA);
    }
    else if (mathFunction == MathFunctionType.CEIL)
    {
        return ceil(operandA);
    }
    else if (mathFunction == MathFunctionType.ROUND)
    {
        return round(operandA);
    }
    else if (mathFunction == MathFunctionType.SIN)
    {
        return sin(operandA);
    }
    else if (mathFunction == MathFunctionType.COS)
    {
        return cos(operandA);
    }
    else if (mathFunction == MathFunctionType.TAN)
    {
        return tan(operandA);
    }
    else if (mathFunction == MathFunctionType.ASIN)
    {
        return asin(operandA);
    }
    else if (mathFunction == MathFunctionType.ACOS)
    {
        return acos(operandA);
    }
    else if (mathFunction == MathFunctionType.ATAN)
    {
        return atan(operandA);
    }
    else if (mathFunction == MathFunctionType.LOG)
    {
        return log(operandA);
    }
    else if (mathFunction == MathFunctionType.LOG10)
    {
        return log10(operandA);
    }
    else if (mathFunction == MathFunctionType.EXP)
    {
        return exp(operandA);
    }

    // --- Resolve secondary operand B (binary functions) ---
    var literalB;
    if (mathFunction == MathFunctionType.ATAN2)
    {
        literalB = definition.funcOperandBNumber;
    }
    else if (definition.outputType == ExpressionOutputType.LENGTH)
    {
        literalB = definition.funcOperandBLength;
    }
    else if (definition.outputType == ExpressionOutputType.ANGLE)
    {
        literalB = definition.funcOperandBAngle;
    }
    else
    {
        literalB = definition.funcOperandBNumber;
    }

    const operandB = resolveTypedOperand(context,
                                         definition.funcOperandBMode,
                                         definition.funcOperandBVariableName,
                                         literalB,
                                         "Operand B");

    // --- Apply dual-argument functions ---
    if (mathFunction == MathFunctionType.ATAN2)
    {
        return atan2(operandA, operandB);
    }
    else if (mathFunction == MathFunctionType.MIN)
    {
        return min(operandA, operandB);
    }
    else // MAX
    {
        return max(operandA, operandB);
    }
}

/**
 * Resolves a single CHAIN mode term from the definition.
 * Returns the value of the term, reading from either a literal field or a
 * named context variable according to the term's mode setting.
 *
 * @param context     : Active build context.
 * @param definition  : Feature definition map.
 * @param termIndex   : 1-based term index (1–4).
 * @returns           : The resolved term value.
 */
function resolveChainTerm(context is Context, definition is map, termIndex is number)
{
    var termMode;
    var variableName;
    var literalValue;

    if (termIndex == 1)
    {
        termMode      = definition.chainTerm1Mode;
        variableName  = definition.chainTerm1VariableName;
        if (definition.outputType == ExpressionOutputType.LENGTH)
            literalValue = definition.chainTerm1Length;
        else if (definition.outputType == ExpressionOutputType.ANGLE)
            literalValue = definition.chainTerm1Angle;
        else
            literalValue = definition.chainTerm1Number;
    }
    else if (termIndex == 2)
    {
        termMode      = definition.chainTerm2Mode;
        variableName  = definition.chainTerm2VariableName;
        if (definition.outputType == ExpressionOutputType.LENGTH)
            literalValue = definition.chainTerm2Length;
        else if (definition.outputType == ExpressionOutputType.ANGLE)
            literalValue = definition.chainTerm2Angle;
        else
            literalValue = definition.chainTerm2Number;
    }
    else if (termIndex == 3)
    {
        termMode      = definition.chainTerm3Mode;
        variableName  = definition.chainTerm3VariableName;
        if (definition.outputType == ExpressionOutputType.LENGTH)
            literalValue = definition.chainTerm3Length;
        else if (definition.outputType == ExpressionOutputType.ANGLE)
            literalValue = definition.chainTerm3Angle;
        else
            literalValue = definition.chainTerm3Number;
    }
    else // termIndex == 4
    {
        termMode      = definition.chainTerm4Mode;
        variableName  = definition.chainTerm4VariableName;
        if (definition.outputType == ExpressionOutputType.LENGTH)
            literalValue = definition.chainTerm4Length;
        else if (definition.outputType == ExpressionOutputType.ANGLE)
            literalValue = definition.chainTerm4Angle;
        else
            literalValue = definition.chainTerm4Number;
    }

    return resolveTypedOperand(context, termMode, variableName, literalValue, "Term " ~ termIndex);
}

/**
 * Evaluates a CHAIN mode expression (A op1 B [op2 C [op3 D]]) and returns the result.
 * Operations are evaluated left-to-right with no implicit precedence.
 *
 * @param context    : Active build context.
 * @param definition : Feature definition map.
 * @returns          : Computed result value.
 */
function evaluateChainExpression(context is Context, definition is map)
{
    // Always at least two terms
    var accumulator = resolveChainTerm(context, definition, 1);
    var term2       = resolveChainTerm(context, definition, 2);
    accumulator = applyArithmeticOperation(accumulator, term2, definition.chainOp1);

    if (definition.chainLength == ChainLength.THREE || definition.chainLength == ChainLength.FOUR)
    {
        var term3 = resolveChainTerm(context, definition, 3);
        accumulator = applyArithmeticOperation(accumulator, term3, definition.chainOp2);
    }

    if (definition.chainLength == ChainLength.FOUR)
    {
        var term4 = resolveChainTerm(context, definition, 4);
        accumulator = applyArithmeticOperation(accumulator, term4, definition.chainOp3);
    }

    return accumulator;
}

// ---------------------------------------------------------------------------
// Report formatting helpers
// ---------------------------------------------------------------------------

/**
 * Formats the evaluated result value as a human-readable string for display
 * in the feature report panel.
 *   LENGTH  → value in millimeters,  e.g. "12.5 mm"
 *   ANGLE   → value in degrees,      e.g. "45.0 deg"
 *   NUMBER  → dimensionless number,  e.g. "3.14159"
 *
 * @param result     : The computed result value (ValueWithUnits for LENGTH/ANGLE, number for NUMBER).
 *                     Left untyped intentionally: FeatureScript has no union type and the same
 *                     function must accept both ValueWithUnits and plain number values.
 * @param outputType : ExpressionOutputType describing the result's type.
 * @returns          : Formatted string for display.
 */
function formatResultValue(result, outputType is ExpressionOutputType) returns string
{
    if (outputType == ExpressionOutputType.LENGTH)
    {
        return toString(result / millimeter) ~ " mm";
    }
    else if (outputType == ExpressionOutputType.ANGLE)
    {
        return toString(result / degree) ~ " deg";
    }
    else
    {
        return toString(result);
    }
}

/**
 * Formats a single literal value as a copy-pasteable Onshape expression fragment.
 *   LENGTH  → "X * mm"
 *   ANGLE   → "X * deg"
 *   NUMBER  → "X"
 *
 * @param literalValue : The literal value to format (ValueWithUnits for LENGTH/ANGLE, number for NUMBER).
 *                       Left untyped intentionally: FeatureScript has no union type and the same
 *                       function must accept both ValueWithUnits and plain number values.
 * @param literalType  : ExpressionOutputType indicating the value's units.
 * @returns            : Onshape expression syntax string for the literal.
 */
function formatLiteralAsExpression(literalValue, literalType is ExpressionOutputType) returns string
{
    if (literalType == ExpressionOutputType.LENGTH)
    {
        return toString(literalValue / millimeter) ~ " * mm";
    }
    else if (literalType == ExpressionOutputType.ANGLE)
    {
        return toString(literalValue / degree) ~ " * deg";
    }
    else
    {
        return toString(literalValue);
    }
}

/**
 * Formats a single operand as a copy-pasteable Onshape expression fragment.
 * Variable operands become "#name"; literal values are formatted with units.
 *
 * @param mode         : OperandInputMode (LITERAL or VARIABLE).
 * @param variableName : Context variable name string (used when mode is VARIABLE).
 * @param literalValue : Pre-selected literal value (ValueWithUnits or number; used when mode is LITERAL).
 *                       Left untyped intentionally: must accept both ValueWithUnits and plain number.
 * @param literalType  : ExpressionOutputType for the literal value's units.
 * @returns            : Expression fragment string, e.g. "#myVar", "5.0 * mm", "45.0 * deg".
 */
function formatOperandAsExpression(mode is OperandInputMode, variableName is string,
                                   literalValue, literalType is ExpressionOutputType) returns string
{
    if (mode == OperandInputMode.VARIABLE)
    {
        return "#" ~ variableName;
    }
    return formatLiteralAsExpression(literalValue, literalType);
}

/**
 * Returns the Onshape operator symbol string for an ArithmeticOperation.
 *
 * @param operation : ArithmeticOperation enum value.
 * @returns         : Operator string, e.g. " + ", " - ", " * ", " / ".
 */
function arithmeticOperationSymbol(operation is ArithmeticOperation) returns string
{
    if (operation == ArithmeticOperation.ADD)
    {
        return " + ";
    }
    else if (operation == ArithmeticOperation.SUBTRACT)
    {
        return " - ";
    }
    else if (operation == ArithmeticOperation.MULTIPLY)
    {
        return " * ";
    }
    else
    {
        return " / ";
    }
}

/**
 * Builds a copy-pasteable Onshape expression string for ARITHMETIC mode  (A op B).
 *
 * @param definition : Feature definition map.
 * @returns          : Expression string, e.g. "(5.0 * mm) + (3.0 * mm)".
 */
function buildArithmeticExpressionString(definition is map) returns string
{
    // --- Operand A ---
    var operandAStr;
    if (definition.outputType == ExpressionOutputType.LENGTH)
    {
        operandAStr = formatOperandAsExpression(definition.operandAMode,
                                               definition.operandAVariableName,
                                               definition.operandALength,
                                               ExpressionOutputType.LENGTH);
    }
    else if (definition.outputType == ExpressionOutputType.ANGLE)
    {
        operandAStr = formatOperandAsExpression(definition.operandAMode,
                                               definition.operandAVariableName,
                                               definition.operandAAngle,
                                               ExpressionOutputType.ANGLE);
    }
    else
    {
        operandAStr = formatOperandAsExpression(definition.operandAMode,
                                               definition.operandAVariableName,
                                               definition.operandANumber,
                                               ExpressionOutputType.NUMBER);
    }

    const operatorStr = arithmeticOperationSymbol(definition.arithmeticOperation);

    // --- Operand B ---
    var operandBStr;
    if (definition.arithmeticOperation == ArithmeticOperation.MULTIPLY ||
        definition.arithmeticOperation == ArithmeticOperation.DIVIDE)
    {
        // B is always a dimensionless scalar
        operandBStr = formatOperandAsExpression(definition.operandBScalarMode,
                                               definition.operandBScalarVariableName,
                                               definition.operandBScalar,
                                               ExpressionOutputType.NUMBER);
    }
    else if (definition.outputType == ExpressionOutputType.LENGTH)
    {
        operandBStr = formatOperandAsExpression(definition.operandBMode,
                                               definition.operandBVariableName,
                                               definition.operandBLength,
                                               ExpressionOutputType.LENGTH);
    }
    else if (definition.outputType == ExpressionOutputType.ANGLE)
    {
        operandBStr = formatOperandAsExpression(definition.operandBMode,
                                               definition.operandBVariableName,
                                               definition.operandBAngle,
                                               ExpressionOutputType.ANGLE);
    }
    else
    {
        operandBStr = formatOperandAsExpression(definition.operandBMode,
                                               definition.operandBVariableName,
                                               definition.operandBNumber,
                                               ExpressionOutputType.NUMBER);
    }

    return "(" ~ operandAStr ~ ")" ~ operatorStr ~ "(" ~ operandBStr ~ ")";
}

/**
 * Builds a copy-pasteable Onshape expression string for MATH_FUNCTION mode (fn(A) or fn(A, B)).
 * Reflects the same operand-type logic as evaluateMathFunctionExpression.
 *
 * @param definition : Feature definition map.
 * @returns          : Expression string, e.g. "sin(45.0 * deg)", "min(3.0, #myVar)".
 */
function buildMathFunctionExpressionString(definition is map) returns string
{
    const mathFunction = definition.mathFunction;

    // --- Determine function name string ---
    var functionName;
    if (mathFunction == MathFunctionType.ABS)        { functionName = "abs"; }
    else if (mathFunction == MathFunctionType.SQRT)  { functionName = "sqrt"; }
    else if (mathFunction == MathFunctionType.FLOOR) { functionName = "floor"; }
    else if (mathFunction == MathFunctionType.CEIL)  { functionName = "ceil"; }
    else if (mathFunction == MathFunctionType.ROUND) { functionName = "round"; }
    else if (mathFunction == MathFunctionType.SIN)   { functionName = "sin"; }
    else if (mathFunction == MathFunctionType.COS)   { functionName = "cos"; }
    else if (mathFunction == MathFunctionType.TAN)   { functionName = "tan"; }
    else if (mathFunction == MathFunctionType.ASIN)  { functionName = "asin"; }
    else if (mathFunction == MathFunctionType.ACOS)  { functionName = "acos"; }
    else if (mathFunction == MathFunctionType.ATAN)  { functionName = "atan"; }
    else if (mathFunction == MathFunctionType.ATAN2) { functionName = "atan2"; }
    else if (mathFunction == MathFunctionType.LOG)   { functionName = "log"; }
    else if (mathFunction == MathFunctionType.LOG10) { functionName = "log10"; }
    else if (mathFunction == MathFunctionType.EXP)   { functionName = "exp"; }
    else if (mathFunction == MathFunctionType.MIN)   { functionName = "min"; }
    else                                             { functionName = "max"; }

    // --- Format primary operand A ---
    // Operand type mirrors the merged precondition condition:
    //   (SIN/COS/TAN) OR (outputType==ANGLE and not inverse-trig) → ANGLE input
    //   outputType == LENGTH → LENGTH input
    //   everything else (inverse-trig, NUMBER output)            → NUMBER input
    var operandAStr;
    if ((mathFunction == MathFunctionType.SIN ||
         mathFunction == MathFunctionType.COS ||
         mathFunction == MathFunctionType.TAN) ||
        (definition.outputType == ExpressionOutputType.ANGLE &&
         mathFunction != MathFunctionType.ASIN &&
         mathFunction != MathFunctionType.ACOS &&
         mathFunction != MathFunctionType.ATAN &&
         mathFunction != MathFunctionType.ATAN2))
    {
        operandAStr = formatOperandAsExpression(definition.funcOperandAMode,
                                               definition.funcOperandAVariableName,
                                               definition.funcOperandAAngle,
                                               ExpressionOutputType.ANGLE);
    }
    else if (definition.outputType == ExpressionOutputType.LENGTH)
    {
        operandAStr = formatOperandAsExpression(definition.funcOperandAMode,
                                               definition.funcOperandAVariableName,
                                               definition.funcOperandALength,
                                               ExpressionOutputType.LENGTH);
    }
    else
    {
        operandAStr = formatOperandAsExpression(definition.funcOperandAMode,
                                               definition.funcOperandAVariableName,
                                               definition.funcOperandANumber,
                                               ExpressionOutputType.NUMBER);
    }

    // --- Single-argument functions ---
    if (mathFunction != MathFunctionType.ATAN2 &&
        mathFunction != MathFunctionType.MIN &&
        mathFunction != MathFunctionType.MAX)
    {
        return functionName ~ "(" ~ operandAStr ~ ")";
    }

    // --- Format secondary operand B (binary functions: ATAN2, MIN, MAX) ---
    // Operand type mirrors buildArithmeticExpressionString operand B logic:
    //   ATAN2 or NUMBER output → NUMBER
    //   LENGTH output          → LENGTH
    //   ANGLE output           → ANGLE
    var operandBStr;
    if (mathFunction == MathFunctionType.ATAN2 ||
        definition.outputType == ExpressionOutputType.NUMBER)
    {
        operandBStr = formatOperandAsExpression(definition.funcOperandBMode,
                                               definition.funcOperandBVariableName,
                                               definition.funcOperandBNumber,
                                               ExpressionOutputType.NUMBER);
    }
    else if (definition.outputType == ExpressionOutputType.LENGTH)
    {
        operandBStr = formatOperandAsExpression(definition.funcOperandBMode,
                                               definition.funcOperandBVariableName,
                                               definition.funcOperandBLength,
                                               ExpressionOutputType.LENGTH);
    }
    else
    {
        operandBStr = formatOperandAsExpression(definition.funcOperandBMode,
                                               definition.funcOperandBVariableName,
                                               definition.funcOperandBAngle,
                                               ExpressionOutputType.ANGLE);
    }

    return functionName ~ "(" ~ operandAStr ~ ", " ~ operandBStr ~ ")";
}

/**
 * Formats a single CHAIN mode term as a copy-pasteable expression fragment.
 * All chain terms share the feature's declared output type for unit formatting.
 *
 * @param definition : Feature definition map.
 * @param termIndex  : 1-based term index (1 to 4).
 * @returns          : Expression fragment string for the term.
 */
function buildChainTermExpression(definition is map, termIndex is number) returns string
{
    // All four vars are assigned in every branch of the termIndex if-else block below.
    // This untyped var pattern mirrors resolveChainTerm in this same file and is the
    // standard FeatureScript idiom when the same variable must hold different types
    // (ValueWithUnits for LENGTH/ANGLE, number for NUMBER) across branches.
    var termMode;
    var variableName;
    var literalValue;
    var literalType;

    if (termIndex == 1)
    {
        termMode     = definition.chainTerm1Mode;
        variableName = definition.chainTerm1VariableName;
        if (definition.outputType == ExpressionOutputType.LENGTH)
        {
            literalValue = definition.chainTerm1Length;
            literalType  = ExpressionOutputType.LENGTH;
        }
        else if (definition.outputType == ExpressionOutputType.ANGLE)
        {
            literalValue = definition.chainTerm1Angle;
            literalType  = ExpressionOutputType.ANGLE;
        }
        else
        {
            literalValue = definition.chainTerm1Number;
            literalType  = ExpressionOutputType.NUMBER;
        }
    }
    else if (termIndex == 2)
    {
        termMode     = definition.chainTerm2Mode;
        variableName = definition.chainTerm2VariableName;
        if (definition.outputType == ExpressionOutputType.LENGTH)
        {
            literalValue = definition.chainTerm2Length;
            literalType  = ExpressionOutputType.LENGTH;
        }
        else if (definition.outputType == ExpressionOutputType.ANGLE)
        {
            literalValue = definition.chainTerm2Angle;
            literalType  = ExpressionOutputType.ANGLE;
        }
        else
        {
            literalValue = definition.chainTerm2Number;
            literalType  = ExpressionOutputType.NUMBER;
        }
    }
    else if (termIndex == 3)
    {
        termMode     = definition.chainTerm3Mode;
        variableName = definition.chainTerm3VariableName;
        if (definition.outputType == ExpressionOutputType.LENGTH)
        {
            literalValue = definition.chainTerm3Length;
            literalType  = ExpressionOutputType.LENGTH;
        }
        else if (definition.outputType == ExpressionOutputType.ANGLE)
        {
            literalValue = definition.chainTerm3Angle;
            literalType  = ExpressionOutputType.ANGLE;
        }
        else
        {
            literalValue = definition.chainTerm3Number;
            literalType  = ExpressionOutputType.NUMBER;
        }
    }
    else // termIndex == 4
    {
        termMode     = definition.chainTerm4Mode;
        variableName = definition.chainTerm4VariableName;
        if (definition.outputType == ExpressionOutputType.LENGTH)
        {
            literalValue = definition.chainTerm4Length;
            literalType  = ExpressionOutputType.LENGTH;
        }
        else if (definition.outputType == ExpressionOutputType.ANGLE)
        {
            literalValue = definition.chainTerm4Angle;
            literalType  = ExpressionOutputType.ANGLE;
        }
        else
        {
            literalValue = definition.chainTerm4Number;
            literalType  = ExpressionOutputType.NUMBER;
        }
    }

    return formatOperandAsExpression(termMode, variableName, literalValue, literalType);
}

/**
 * Builds a copy-pasteable Onshape expression string for CHAIN mode
 * (A op1 B [op2 C [op3 D]]).  Terms are wrapped in parentheses and joined
 * by their respective operators, left to right with no implicit precedence.
 *
 * @param definition : Feature definition map.
 * @returns          : Expression string, e.g. "(5.0 * mm) + (3.0 * mm) - (1.0 * mm)".
 */
function buildChainExpressionString(definition is map) returns string
{
    var expressionString = "(" ~ buildChainTermExpression(definition, 1) ~ ")" ~
                           arithmeticOperationSymbol(definition.chainOp1) ~
                           "(" ~ buildChainTermExpression(definition, 2) ~ ")";

    if (definition.chainLength == ChainLength.THREE || definition.chainLength == ChainLength.FOUR)
    {
        expressionString = expressionString ~
                           arithmeticOperationSymbol(definition.chainOp2) ~
                           "(" ~ buildChainTermExpression(definition, 3) ~ ")";
    }

    if (definition.chainLength == ChainLength.FOUR)
    {
        expressionString = expressionString ~
                           arithmeticOperationSymbol(definition.chainOp3) ~
                           "(" ~ buildChainTermExpression(definition, 4) ~ ")";
    }

    return expressionString;
}
