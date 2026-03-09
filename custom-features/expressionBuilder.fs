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
 *   STRING_CONCAT - Two to six segments joined by ~  "prefix" ~ #var ~ "suffix"
 *
 * Each operand can be:
 *   LITERAL  - a plain dimensionless number (CHAIN mode only); or a length, angle, number, or
 *              string value selected via an inline type picker (ARITHMETIC and MATH_FUNCTION flex operands)
 *   VARIABLE - the name of an existing context variable (may hold any type: number, length, angle, string)
 *
 * STRING_CONCAT segment types:
 *   TEXT     - a literal string the user types in the panel (can include any unit text, e.g. "1.5 in")
 *   VARIABLE - a named context variable, auto-converted to string (lengths in mm, angles in deg)
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
 * STRING output enables string concatenation in ARITHMETIC / CHAIN modes and
 * the toString() function in MATH_FUNCTION mode.
 */
export enum ExpressionOutputType
{
    annotation { "Name" : "Length" }
    LENGTH,
    annotation { "Name" : "Angle" }
    ANGLE,
    annotation { "Name" : "Number" }
    NUMBER,
    annotation { "Name" : "String" }
    STRING
}

/**
 * Top-level expression structure.
 *   ARITHMETIC    – two operands joined by one operator (A op B)
 *   MATH_FUNCTION – a named math function applied to one or two operands
 *   CHAIN         – two to four operands joined by independent operators
 *   STRING_CONCAT – two to six segments concatenated with the ~ operator into a string result
 */
export enum ExpressionBuilderMode
{
    annotation { "Name" : "Arithmetic  (A op B)" }
    ARITHMETIC,
    annotation { "Name" : "Math function  f(A)" }
    MATH_FUNCTION,
    annotation { "Name" : "Chain  (A op B op C...)" }
    CHAIN,
    annotation { "Name" : "String concat  (A ~ B ~ C...)" }
    STRING_CONCAT
}

/**
 * Binary arithmetic operators used in ARITHMETIC and CHAIN modes.
 * MULTIPLY, DIVIDE, and POWER treat operand B as a dimensionless scalar so
 * that the result preserves the units of operand A (length * scalar = length, etc.).
 * STRING output type always concatenates operands with ~ regardless of this setting.
 */
export enum ArithmeticOperation
{
    annotation { "Name" : "Add" }
    ADD,
    annotation { "Name" : "Subtract" }
    SUBTRACT,
    annotation { "Name" : "Multiply" }
    MULTIPLY,
    annotation { "Name" : "Divide" }
    DIVIDE,
    annotation { "Name" : "Power" }
    POWER
}

/**
 * Supported single- and dual-argument math functions.
 * Trig functions (SIN, COS, TAN) expect an angle operand.
 * Inverse trig functions (ASIN, ACOS, ATAN, ATAN2) return an angle.
 * All others operate on values whose type matches the declared output type.
 * TO_STRING converts any operand value to a string and should be used with
 * String output type.
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
    MAX,
    annotation { "Name" : "To string  toString(A) - use with String output type" }
    TO_STRING
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

/**
 * The source type for a single STRING_CONCAT mode segment.
 *   TEXT     – the user types any literal string directly in the panel (including values
 *              with units written as plain text such as "1.5 in" or "45 deg")
 *   VARIABLE – reads the value of a named context variable and converts it to a string
 *              automatically (strings pass through; numbers, lengths, and angles are
 *              toString'd; lengths display in mm, angles in deg)
 */
export enum ConcatSegmentInputType
{
    annotation { "Name" : "Text" }
    TEXT,
    annotation { "Name" : "Variable" }
    VARIABLE
}

/**
 * How many segments the STRING_CONCAT mode concatenates.
 */
export enum StringConcatLength
{
    annotation { "Name" : "2 segments" }
    TWO,
    annotation { "Name" : "3 segments" }
    THREE,
    annotation { "Name" : "4 segments" }
    FOUR,
    annotation { "Name" : "5 segments" }
    FIVE,
    annotation { "Name" : "6 segments" }
    SIX
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
 *      @field expressionMode {ExpressionBuilderMode} : How the expression is structured.
 *      Per-operand type selectors appear inline with each literal value field to control
 *      what type of value is entered (LENGTH, ANGLE, NUMBER, or STRING). The actual result
 *      type is determined by the math, not declared up front.
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
 *      @field chainTerm1Mode … chainTerm4Mode {OperandInputMode}: Source for each term (LITERAL or VARIABLE).
 *      @field chainTerm1VariableName … chainTerm4VariableName {string}: Variable names for VARIABLE terms.
 *      @field chainTerm1Number … chainTerm4Number {number}: Dimensionless literal values for LITERAL terms.
 *      @field chainOp1 … chainOp3 {ArithmeticOperation}: Operators between consecutive terms.
 *
 *      // STRING_CONCAT mode fields
 *      @field stringConcatLength {StringConcatLength} : Number of segments (TWO through SIX).
 *      @field concatSeg1Type … concatSeg6Type {ConcatSegmentInputType}: TEXT or VARIABLE for each segment.
 *      @field concatSeg1Text … concatSeg6Text {string}: Literal text for TEXT segments (any string, including "1.5 in").
 *      @field concatSeg1Variable … concatSeg6Variable {string}: Variable names for VARIABLE segments.
 * }}
 */
annotation { "Feature Type Name" : "Expression Builder",
             "Feature Name Template" : "###variableName = #result",
             "Tooltip Template" : "###variableName = #result",
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
        // Expression structure
        // ---------------------------------------------------------------
        annotation { "Name" : "Expression mode",
                     "Description" : "How to structure the expression: two-operand arithmetic, a named math function, or a chain of up to four operands." }
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
                    annotation { "Name" : "Type",
                                 "Description" : "Data type of this operand.",
                                 "UIHint" : UIHint.HORIZONTAL_ENUM }
                    definition.operandAValueType is ExpressionOutputType;

                    if (definition.operandAValueType == ExpressionOutputType.STRING)
                    {
                        annotation { "Name" : "Text value", "MaxLength" : 1024 }
                        definition.operandAStringLiteral is string;
                    }
                    else if (definition.operandAValueType == ExpressionOutputType.LENGTH)
                    {
                        annotation { "Name" : "Value" }
                        isLength(definition.operandALength, ZERO_DEFAULT_LENGTH_BOUNDS);
                    }
                    else if (definition.operandAValueType == ExpressionOutputType.ANGLE)
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
                         "Description" : "Arithmetic operator. Multiply, Divide, and Power treat operand B as a dimensionless scalar. Add applied to two string operands concatenates them." }
            definition.arithmeticOperation is ArithmeticOperation;

            // --- Operand B ---
            annotation { "Group Name" : "Operand B", "Collapsed By Default" : false }
            {
                // Multiply, Divide, and Power: B is always a dimensionless scalar so
                // the output preserves the units of A (length * scalar = length, etc.).
                if (definition.arithmeticOperation == ArithmeticOperation.MULTIPLY ||
                    definition.arithmeticOperation == ArithmeticOperation.DIVIDE ||
                    definition.arithmeticOperation == ArithmeticOperation.POWER)
                {
                    annotation { "Name" : "Source",
                                 "Description" : "Dimensionless scalar (multiplier, divisor, or exponent).",
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
                                     "Description" : "Dimensionless multiplier, divisor, or exponent." }
                        isReal(definition.operandBScalar, EXPRESSION_BUILDER_SCALAR_BOUNDS);
                    }
                }
                else
                {
                    // Add / Subtract: B has the same type as A.
                    // When adding two strings the Add operation concatenates them with ~.
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
                        annotation { "Name" : "Type",
                                     "Description" : "Data type of this operand.",
                                     "UIHint" : UIHint.HORIZONTAL_ENUM }
                        definition.operandBValueType is ExpressionOutputType;

                        if (definition.operandBValueType == ExpressionOutputType.STRING)
                        {
                            annotation { "Name" : "Text value", "MaxLength" : 1024 }
                            definition.operandBStringLiteral is string;
                        }
                        else if (definition.operandBValueType == ExpressionOutputType.LENGTH)
                        {
                            annotation { "Name" : "Value" }
                            isLength(definition.operandBLength, ZERO_DEFAULT_LENGTH_BOUNDS);
                        }
                        else if (definition.operandBValueType == ExpressionOutputType.ANGLE)
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
        // ===============================================================
        // MATH_FUNCTION MODE  ( fn(A)  or  fn(A, B) )
        // ===============================================================
        else if (definition.expressionMode == ExpressionBuilderMode.MATH_FUNCTION)
        {
            annotation { "Name" : "Function",
                         "Description" : "Select a math function. Fixed-type functions (sin/cos/tan: angle; asin/atan/floor/log/exp: number) show the correct input field automatically. Flexible functions (abs, sqrt, min, max, toString) show a Type selector for the operand." }
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
                    // SIN / COS / TAN: always require an angle.
                    if (definition.mathFunction == MathFunctionType.SIN ||
                        definition.mathFunction == MathFunctionType.COS ||
                        definition.mathFunction == MathFunctionType.TAN)
                    {
                        annotation { "Name" : "Angle value",
                                     "Description" : "Input angle for the trigonometric function." }
                        isAngle(definition.funcOperandAAngle, ANGLE_360_ZERO_DEFAULT_BOUNDS);
                    }
                    // ASIN / ACOS / ATAN / ATAN2 / FLOOR / CEIL / ROUND / LOG / LOG10 / EXP:
                    // always require a dimensionless number.
                    else if (definition.mathFunction == MathFunctionType.ASIN ||
                             definition.mathFunction == MathFunctionType.ACOS ||
                             definition.mathFunction == MathFunctionType.ATAN ||
                             definition.mathFunction == MathFunctionType.ATAN2 ||
                             definition.mathFunction == MathFunctionType.FLOOR ||
                             definition.mathFunction == MathFunctionType.CEIL ||
                             definition.mathFunction == MathFunctionType.ROUND ||
                             definition.mathFunction == MathFunctionType.LOG ||
                             definition.mathFunction == MathFunctionType.LOG10 ||
                             definition.mathFunction == MathFunctionType.EXP)
                    {
                        annotation { "Name" : "Number value",
                                     "Description" : "Dimensionless number (asin / acos expect -1 to 1; log / log10 expect > 0)." }
                        isReal(definition.funcOperandANumber, EXPRESSION_BUILDER_NUMBER_BOUNDS);
                    }
                    else
                    {
                        // ABS / SQRT / MIN / MAX / TO_STRING: flexible input type.
                        // The user selects what kind of value they are passing in.
                        annotation { "Name" : "Type",
                                     "Description" : "Data type of this operand.",
                                     "UIHint" : UIHint.HORIZONTAL_ENUM }
                        definition.funcOperandAFlexType is ExpressionOutputType;

                        if (definition.funcOperandAFlexType == ExpressionOutputType.STRING)
                        {
                            annotation { "Name" : "Text value", "MaxLength" : 1024 }
                            definition.funcOperandAFlexString is string;
                        }
                        else if (definition.funcOperandAFlexType == ExpressionOutputType.LENGTH)
                        {
                            annotation { "Name" : "Length value" }
                            isLength(definition.funcOperandAFlexLength, ZERO_DEFAULT_LENGTH_BOUNDS);
                        }
                        else if (definition.funcOperandAFlexType == ExpressionOutputType.ANGLE)
                        {
                            annotation { "Name" : "Angle value" }
                            isAngle(definition.funcOperandAFlexAngle, ANGLE_360_ZERO_DEFAULT_BOUNDS);
                        }
                        else
                        {
                            annotation { "Name" : "Number value" }
                            isReal(definition.funcOperandAFlexNumber, EXPRESSION_BUILDER_NUMBER_BOUNDS);
                        }
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
                        // ATAN2(y, x): both arguments are dimensionless numbers.
                        if (definition.mathFunction == MathFunctionType.ATAN2)
                        {
                            annotation { "Name" : "Number value",
                                         "Description" : "Second dimensionless argument for atan2(A, B)." }
                            isReal(definition.funcOperandBNumber, EXPRESSION_BUILDER_NUMBER_BOUNDS);
                        }
                        else
                        {
                            // MIN / MAX: flexible input type matching operand A.
                            annotation { "Name" : "Type",
                                         "Description" : "Data type of this operand. Should match Operand A's type.",
                                         "UIHint" : UIHint.HORIZONTAL_ENUM }
                            definition.funcOperandBFlexType is ExpressionOutputType;

                            if (definition.funcOperandBFlexType == ExpressionOutputType.STRING)
                            {
                                annotation { "Name" : "Text value", "MaxLength" : 1024 }
                                definition.funcOperandBFlexString is string;
                            }
                            else if (definition.funcOperandBFlexType == ExpressionOutputType.LENGTH)
                            {
                                annotation { "Name" : "Length value" }
                                isLength(definition.funcOperandBFlexLength, ZERO_DEFAULT_LENGTH_BOUNDS);
                            }
                            else if (definition.funcOperandBFlexType == ExpressionOutputType.ANGLE)
                            {
                                annotation { "Name" : "Angle value" }
                                isAngle(definition.funcOperandBFlexAngle, ANGLE_360_ZERO_DEFAULT_BOUNDS);
                            }
                            else
                            {
                                annotation { "Name" : "Number value" }
                                isReal(definition.funcOperandBFlexNumber, EXPRESSION_BUILDER_NUMBER_BOUNDS);
                            }
                        }
                    }
                }
            }
        }

        // ===============================================================
        // CHAIN MODE  ( A op1 B  [op2 C  [op3 D]] )
        // Each term is either a dimensionless number typed directly in the
        // panel or the value of a named context variable (which may carry
        // units such as length or angle — those values flow through from
        // prior Expression Builder features via the VARIABLE path).
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
                annotation { "Name" : "Source",
                             "Description" : "Enter a dimensionless number, or read any value from an existing context variable.",
                             "UIHint" : UIHint.HORIZONTAL_ENUM }
                definition.chainTerm1Mode is OperandInputMode;

                if (definition.chainTerm1Mode == OperandInputMode.VARIABLE)
                {
                    annotation { "Name" : "Variable name",
                                 "Description" : "Name of the context variable to read (do not include #).",
                                 "MaxLength" : 256 }
                    definition.chainTerm1VariableName is string;
                }
                else
                {
                    annotation { "Name" : "Value",
                                 "Description" : "Dimensionless number. Use Variable mode to pass a length or angle from a prior feature." }
                    isReal(definition.chainTerm1Number, EXPRESSION_BUILDER_NUMBER_BOUNDS);
                }
            }

            // --- Operation 1 (between Term 1 and Term 2) ---
            annotation { "Name" : "Operation 1",
                         "Description" : "Operator applied between Term 1 and Term 2. Multiply, Divide, and Power treat the right-hand term as a dimensionless scalar. When Variable terms hold string values, Add concatenates them with ~." }
            definition.chainOp1 is ArithmeticOperation;

            // --- Term 2 ---
            annotation { "Group Name" : "Term 2", "Collapsed By Default" : false }
            {
                annotation { "Name" : "Source",
                             "Description" : "Enter a dimensionless number, or read any value from an existing context variable.",
                             "UIHint" : UIHint.HORIZONTAL_ENUM }
                definition.chainTerm2Mode is OperandInputMode;

                if (definition.chainTerm2Mode == OperandInputMode.VARIABLE)
                {
                    annotation { "Name" : "Variable name",
                                 "Description" : "Name of the context variable to read (do not include #).",
                                 "MaxLength" : 256 }
                    definition.chainTerm2VariableName is string;
                }
                else
                {
                    annotation { "Name" : "Value",
                                 "Description" : "Dimensionless number. Use Variable mode to pass a length or angle from a prior feature." }
                    isReal(definition.chainTerm2Number, EXPRESSION_BUILDER_NUMBER_BOUNDS);
                }
            }

            // --- Term 3 and Operation 2 (only when chainLength >= THREE) ---
            if (definition.chainLength == ChainLength.THREE ||
                definition.chainLength == ChainLength.FOUR)
            {
                annotation { "Name" : "Operation 2",
                             "Description" : "Operator applied between Term 2 and Term 3." }
                definition.chainOp2 is ArithmeticOperation;

                annotation { "Group Name" : "Term 3", "Collapsed By Default" : false }
                {
                    annotation { "Name" : "Source",
                                 "Description" : "Enter a dimensionless number, or read any value from an existing context variable.",
                                 "UIHint" : UIHint.HORIZONTAL_ENUM }
                    definition.chainTerm3Mode is OperandInputMode;

                    if (definition.chainTerm3Mode == OperandInputMode.VARIABLE)
                    {
                        annotation { "Name" : "Variable name",
                                     "Description" : "Name of the context variable to read (do not include #).",
                                     "MaxLength" : 256 }
                        definition.chainTerm3VariableName is string;
                    }
                    else
                    {
                        annotation { "Name" : "Value",
                                     "Description" : "Dimensionless number. Use Variable mode to pass a length or angle from a prior feature." }
                        isReal(definition.chainTerm3Number, EXPRESSION_BUILDER_NUMBER_BOUNDS);
                    }
                }
            }

            // --- Term 4 and Operation 3 (only when chainLength == FOUR) ---
            if (definition.chainLength == ChainLength.FOUR)
            {
                annotation { "Name" : "Operation 3",
                             "Description" : "Operator applied between Term 3 and Term 4." }
                definition.chainOp3 is ArithmeticOperation;

                annotation { "Group Name" : "Term 4", "Collapsed By Default" : false }
                {
                    annotation { "Name" : "Source",
                                 "Description" : "Enter a dimensionless number, or read any value from an existing context variable.",
                                 "UIHint" : UIHint.HORIZONTAL_ENUM }
                    definition.chainTerm4Mode is OperandInputMode;

                    if (definition.chainTerm4Mode == OperandInputMode.VARIABLE)
                    {
                        annotation { "Name" : "Variable name",
                                     "Description" : "Name of the context variable to read (do not include #).",
                                     "MaxLength" : 256 }
                        definition.chainTerm4VariableName is string;
                    }
                    else
                    {
                        annotation { "Name" : "Value",
                                     "Description" : "Dimensionless number. Use Variable mode to pass a length or angle from a prior feature." }
                        isReal(definition.chainTerm4Number, EXPRESSION_BUILDER_NUMBER_BOUNDS);
                    }
                }
            }
        }

        // ===============================================================
        // STRING_CONCAT MODE  ( Seg1 ~ Seg2 ~ [Seg3 ~ ... ~ Seg6] )
        // Each segment is either a literal text string (the user can type any value including
        // unit text such as "1.5 in" or "45 deg") or the name of a context variable whose
        // value is automatically converted to string at evaluation time.
        // ===============================================================
        else if (definition.expressionMode == ExpressionBuilderMode.STRING_CONCAT)
        {
            annotation { "Name" : "Number of segments",
                         "Description" : "How many text segments to join into the output string.",
                         "UIHint" : UIHint.HORIZONTAL_ENUM }
            definition.stringConcatLength is StringConcatLength;

            // --- Segment 1 ---
            annotation { "Group Name" : "Segment 1", "Collapsed By Default" : false }
            {
                annotation { "Name" : "Type",
                             "Description" : "Text: type any string (numbers, units, labels, e.g. 1.5 in or 45 deg). Variable: read and toString() a named context variable.",
                             "UIHint" : UIHint.HORIZONTAL_ENUM }
                definition.concatSeg1Type is ConcatSegmentInputType;

                if (definition.concatSeg1Type == ConcatSegmentInputType.TEXT)
                {
                    annotation { "Name" : "Text",
                                 "Description" : "Any literal string. You may type values with units here, e.g. 1.5 in or 45 deg.",
                                 "MaxLength" : 1024 }
                    definition.concatSeg1Text is string;
                }
                else // VARIABLE
                {
                    annotation { "Name" : "Variable name",
                                 "Description" : "Name of the context variable to read and convert to string (do not include #). Lengths display in mm, angles in deg.",
                                 "MaxLength" : 256 }
                    definition.concatSeg1Variable is string;
                }
            }

            // --- Segment 2 ---
            annotation { "Group Name" : "Segment 2", "Collapsed By Default" : false }
            {
                annotation { "Name" : "Type",
                             "Description" : "Text: type any string (numbers, units, labels, e.g. 1.5 in or 45 deg). Variable: read and toString() a named context variable.",
                             "UIHint" : UIHint.HORIZONTAL_ENUM }
                definition.concatSeg2Type is ConcatSegmentInputType;

                if (definition.concatSeg2Type == ConcatSegmentInputType.TEXT)
                {
                    annotation { "Name" : "Text",
                                 "Description" : "Any literal string. You may type values with units here, e.g. 1.5 in or 45 deg.",
                                 "MaxLength" : 1024 }
                    definition.concatSeg2Text is string;
                }
                else // VARIABLE
                {
                    annotation { "Name" : "Variable name",
                                 "Description" : "Name of the context variable to read and convert to string (do not include #). Lengths display in mm, angles in deg.",
                                 "MaxLength" : 256 }
                    definition.concatSeg2Variable is string;
                }
            }

            // --- Segment 3 (visible when THREE or more segments selected) ---
            if (definition.stringConcatLength == StringConcatLength.THREE ||
                definition.stringConcatLength == StringConcatLength.FOUR  ||
                definition.stringConcatLength == StringConcatLength.FIVE  ||
                definition.stringConcatLength == StringConcatLength.SIX)
            {
                annotation { "Group Name" : "Segment 3", "Collapsed By Default" : false }
                {
                    annotation { "Name" : "Type",
                                 "Description" : "Text: type any string (numbers, units, labels, e.g. 1.5 in or 45 deg). Variable: read and toString() a named context variable.",
                                 "UIHint" : UIHint.HORIZONTAL_ENUM }
                    definition.concatSeg3Type is ConcatSegmentInputType;

                    if (definition.concatSeg3Type == ConcatSegmentInputType.TEXT)
                    {
                        annotation { "Name" : "Text",
                                     "Description" : "Any literal string. You may type values with units here, e.g. 1.5 in or 45 deg.",
                                     "MaxLength" : 1024 }
                        definition.concatSeg3Text is string;
                    }
                    else // VARIABLE
                    {
                        annotation { "Name" : "Variable name",
                                     "Description" : "Name of the context variable to read and convert to string (do not include #). Lengths display in mm, angles in deg.",
                                     "MaxLength" : 256 }
                        definition.concatSeg3Variable is string;
                    }
                }
            }

            // --- Segment 4 (visible when FOUR or more segments selected) ---
            if (definition.stringConcatLength == StringConcatLength.FOUR ||
                definition.stringConcatLength == StringConcatLength.FIVE ||
                definition.stringConcatLength == StringConcatLength.SIX)
            {
                annotation { "Group Name" : "Segment 4", "Collapsed By Default" : false }
                {
                    annotation { "Name" : "Type",
                                 "Description" : "Text: type any string (numbers, units, labels, e.g. 1.5 in or 45 deg). Variable: read and toString() a named context variable.",
                                 "UIHint" : UIHint.HORIZONTAL_ENUM }
                    definition.concatSeg4Type is ConcatSegmentInputType;

                    if (definition.concatSeg4Type == ConcatSegmentInputType.TEXT)
                    {
                        annotation { "Name" : "Text",
                                     "Description" : "Any literal string. You may type values with units here, e.g. 1.5 in or 45 deg.",
                                     "MaxLength" : 1024 }
                        definition.concatSeg4Text is string;
                    }
                    else // VARIABLE
                    {
                        annotation { "Name" : "Variable name",
                                     "Description" : "Name of the context variable to read and convert to string (do not include #). Lengths display in mm, angles in deg.",
                                     "MaxLength" : 256 }
                        definition.concatSeg4Variable is string;
                    }
                }
            }

            // --- Segment 5 (visible when FIVE or more segments selected) ---
            if (definition.stringConcatLength == StringConcatLength.FIVE ||
                definition.stringConcatLength == StringConcatLength.SIX)
            {
                annotation { "Group Name" : "Segment 5", "Collapsed By Default" : false }
                {
                    annotation { "Name" : "Type",
                                 "Description" : "Text: type any string (numbers, units, labels, e.g. 1.5 in or 45 deg). Variable: read and toString() a named context variable.",
                                 "UIHint" : UIHint.HORIZONTAL_ENUM }
                    definition.concatSeg5Type is ConcatSegmentInputType;

                    if (definition.concatSeg5Type == ConcatSegmentInputType.TEXT)
                    {
                        annotation { "Name" : "Text",
                                     "Description" : "Any literal string. You may type values with units here, e.g. 1.5 in or 45 deg.",
                                     "MaxLength" : 1024 }
                        definition.concatSeg5Text is string;
                    }
                    else // VARIABLE
                    {
                        annotation { "Name" : "Variable name",
                                     "Description" : "Name of the context variable to read and convert to string (do not include #). Lengths display in mm, angles in deg.",
                                     "MaxLength" : 256 }
                        definition.concatSeg5Variable is string;
                    }
                }
            }

            // --- Segment 6 (visible only when SIX segments selected) ---
            if (definition.stringConcatLength == StringConcatLength.SIX)
            {
                annotation { "Group Name" : "Segment 6", "Collapsed By Default" : false }
                {
                    annotation { "Name" : "Type",
                                 "Description" : "Text: type any string (numbers, units, labels, e.g. 1.5 in or 45 deg). Variable: read and toString() a named context variable.",
                                 "UIHint" : UIHint.HORIZONTAL_ENUM }
                    definition.concatSeg6Type is ConcatSegmentInputType;

                    if (definition.concatSeg6Type == ConcatSegmentInputType.TEXT)
                    {
                        annotation { "Name" : "Text",
                                     "Description" : "Any literal string. You may type values with units here, e.g. 1.5 in or 45 deg.",
                                     "MaxLength" : 1024 }
                        definition.concatSeg6Text is string;
                    }
                    else // VARIABLE
                    {
                        annotation { "Name" : "Variable name",
                                     "Description" : "Name of the context variable to read and convert to string (do not include #). Lengths display in mm, angles in deg.",
                                     "MaxLength" : 256 }
                        definition.concatSeg6Variable is string;
                    }
                }
            }
        }

        // The result of the expression is stored here as a computed parameter so that the
        // Feature Name Template can reference it via "#result". The engine formats ValueWithUnits
        // values in the document's preferred unit system automatically (identical to how the
        // built-in Variable feature uses setFeatureComputedParameter to show values in inches
        // for imperial documents). This parameter is never shown in the panel.
        annotation { "UIHint" : UIHint.ALWAYS_HIDDEN }
        isAnything(definition.result);
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
        else if (definition.expressionMode == ExpressionBuilderMode.STRING_CONCAT)
        {
            result = evaluateStringConcatExpression(context, definition);
        }

        if (result == undefined)
        {
            throw regenError("Expression evaluation produced no result. "
                             ~ "Check that all operands and settings are valid for the selected mode.");
        }

        // Store the computed value as a named context variable
        setVariable(context, definition.variableName, result, definition.variableDescription);

        // Push the result into the Feature Name Template so the feature list shows
        // "variableName = <value>" with automatic document-unit formatting (e.g. inches
        // for imperial documents). This is identical to the mechanism used by the built-in
        // Variable feature: setFeatureComputedParameter (exported from onshape/std/feature.fs)
        // feeds a ValueWithUnits into the slot referenced by "#result" in the Feature Name
        // Template, and the Onshape engine renders it in the document's preferred unit system
        // automatically. Strings are wrapped in double-quotes to match the Variable feature
        // convention (see publishVariableValue in variable.fs).
        const quotedResult = (result is string) ? ('"' ~ result ~ '"') : result;
        setFeatureComputedParameter(context, id, { "name" : "result", "value" : quotedResult });

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
        else if (definition.expressionMode == ExpressionBuilderMode.STRING_CONCAT)
        {
            expressionString = buildStringConcatExpressionString(definition);
        }
        else
        {
            expressionString = "(unknown expression mode)";
        }

        // Show the copy-pasteable expression string in the feature info panel.
        // The result value is already displayed in document units via the Feature Name
        // Template ("#result" slot fed by setFeatureComputedParameter above), so we do
        // not duplicate it here. This matches the Variable feature, which also relies
        // solely on the Feature Name Template for result display.
        reportFeatureInfo(context, id, "Expression: " ~ expressionString);
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
 * Wraps each operation in try() so type-mismatch errors (e.g. adding a length
 * to an angle) are caught and surfaced as clear regenErrors in the feature panel.
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
        // When the left operand is a string, use ~ for concatenation.
        // For numeric operands, use standard addition.
        if (leftOperand is string)
        {
            const concatResult = try(leftOperand ~ rightOperand);
            if (concatResult == undefined)
            {
                throw regenError("String concatenation failed: the left operand is a string but the right operand "
                                 ~ "could not be concatenated. Ensure both operands are string values, "
                                 ~ "or use toString() to convert a non-string value before concatenating.");
            }
            return concatResult;
        }
        const addResult = try(leftOperand + rightOperand);
        if (addResult == undefined)
        {
            throw regenError("Addition failed: operands have incompatible types or units. "
                             ~ "Ensure both operands share the same output type (length, angle, or number).");
        }
        return addResult;
    }
    else if (operation == ArithmeticOperation.SUBTRACT)
    {
        const subResult = try(leftOperand - rightOperand);
        if (subResult == undefined)
        {
            throw regenError("Subtraction failed: operands have incompatible types or units. "
                             ~ "Ensure both operands share the same output type (length, angle, or number).");
        }
        return subResult;
    }
    else if (operation == ArithmeticOperation.MULTIPLY)
    {
        const mulResult = try(leftOperand * rightOperand);
        if (mulResult == undefined)
        {
            throw regenError("Multiplication failed: check that the scalar multiplier is a plain number "
                             ~ "and that the operand type is compatible.");
        }
        return mulResult;
    }
    else if (operation == ArithmeticOperation.DIVIDE)
    {
        if (rightOperand == 0)
        {
            throw regenError("Division by zero: the scalar divisor evaluated to 0.");
        }
        const divResult = try(leftOperand / rightOperand);
        if (divResult == undefined)
        {
            throw regenError("Division failed: check that the scalar divisor is a plain number "
                             ~ "and that the operand type is compatible.");
        }
        return divResult;
    }
    else // POWER
    {
        // FeatureScript uses ^ for exponentiation (no pow() function exists).
        // For values with units, only integer exponents produce valid results.
        const powResult = try(leftOperand ^ rightOperand);
        if (powResult == undefined)
        {
            throw regenError("Power failed: check that the exponent is a dimensionless number. "
                             ~ "For values with units, only integer exponents are valid (e.g., length^2).");
        }
        return powResult;
    }
}

// ---------------------------------------------------------------------------
// Mode evaluation functions
// ---------------------------------------------------------------------------

/**
 * Evaluates an ARITHMETIC mode expression (A op B) and returns the result.
 * Each operand's literal type is read from its own inline type selector.
 * String concatenation is handled automatically by applyArithmeticOperation
 * when the ADD operation is applied to string operands.
 *
 * @param context    : Active build context.
 * @param definition : Feature definition map.
 * @returns          : Computed result value.
 */
function evaluateArithmeticExpression(context is Context, definition is map)
{
    // --- Resolve operand A ---
    // When mode is LITERAL, the value field shown depends on operandAValueType.
    // When mode is VARIABLE, literalA remains undefined — resolveTypedOperand branches
    // to readContextVariable before ever reading literalValue, so this is safe.
    var literalA;
    if (definition.operandAMode == OperandInputMode.LITERAL)
    {
        if (definition.operandAValueType == ExpressionOutputType.STRING)
            literalA = definition.operandAStringLiteral;
        else if (definition.operandAValueType == ExpressionOutputType.LENGTH)
            literalA = definition.operandALength;
        else if (definition.operandAValueType == ExpressionOutputType.ANGLE)
            literalA = definition.operandAAngle;
        else
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

    if (operation == ArithmeticOperation.MULTIPLY ||
        operation == ArithmeticOperation.DIVIDE ||
        operation == ArithmeticOperation.POWER)
    {
        // B is always a dimensionless scalar for multiply / divide / power
        operandB = resolveTypedOperand(context,
                                       definition.operandBScalarMode,
                                       definition.operandBScalarVariableName,
                                       definition.operandBScalar,
                                       "Operand B (scalar)");
    }
    else
    {
        // ADD or SUBTRACT: each operand uses its own per-operand type selector.
        // When operandBMode is VARIABLE, literalB remains undefined — resolveTypedOperand
        // branches to readContextVariable before ever reading literalValue, so this is safe.
        var literalB;
        if (definition.operandBMode == OperandInputMode.LITERAL)
        {
            if (definition.operandBValueType == ExpressionOutputType.STRING)
                literalB = definition.operandBStringLiteral;
            else if (definition.operandBValueType == ExpressionOutputType.LENGTH)
                literalB = definition.operandBLength;
            else if (definition.operandBValueType == ExpressionOutputType.ANGLE)
                literalB = definition.operandBAngle;
            else
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
 * The literal field read for each operand depends on the function's required input type:
 *   - Fixed-type functions (sin/cos/tan): always an angle.
 *   - Fixed-type functions (asin/acos/atan/atan2/floor/ceil/round/log/log10/exp): always a number.
 *   - Flexible-type functions (abs/sqrt/min/max/toString): read from funcOperandAFlexType /
 *     funcOperandBFlexType and the corresponding literal field.
 *
 * @param context    : Active build context.
 * @param definition : Feature definition map.
 * @returns          : Computed result value.
 */
function evaluateMathFunctionExpression(context is Context, definition is map)
{
    const mathFunction = definition.mathFunction;

    // --- Resolve primary operand A ---
    var literalA;
    if (definition.funcOperandAMode == OperandInputMode.LITERAL)
    {
        if (mathFunction == MathFunctionType.SIN ||
            mathFunction == MathFunctionType.COS ||
            mathFunction == MathFunctionType.TAN)
        {
            // Fixed ANGLE input for forward trig functions.
            literalA = definition.funcOperandAAngle;
        }
        else if (mathFunction == MathFunctionType.ASIN ||
                 mathFunction == MathFunctionType.ACOS ||
                 mathFunction == MathFunctionType.ATAN ||
                 mathFunction == MathFunctionType.ATAN2 ||
                 mathFunction == MathFunctionType.FLOOR ||
                 mathFunction == MathFunctionType.CEIL ||
                 mathFunction == MathFunctionType.ROUND ||
                 mathFunction == MathFunctionType.LOG ||
                 mathFunction == MathFunctionType.LOG10 ||
                 mathFunction == MathFunctionType.EXP)
        {
            // Fixed NUMBER input for inverse trig and scalar-only functions.
            literalA = definition.funcOperandANumber;
        }
        else
        {
            // ABS / SQRT / MIN / MAX / TO_STRING: flexible type, read from inline selector.
            if (definition.funcOperandAFlexType == ExpressionOutputType.STRING)
                literalA = definition.funcOperandAFlexString;
            else if (definition.funcOperandAFlexType == ExpressionOutputType.LENGTH)
                literalA = definition.funcOperandAFlexLength;
            else if (definition.funcOperandAFlexType == ExpressionOutputType.ANGLE)
                literalA = definition.funcOperandAFlexAngle;
            else
                literalA = definition.funcOperandAFlexNumber;
        }
    }

    const operandA = resolveTypedOperand(context,
                                         definition.funcOperandAMode,
                                         definition.funcOperandAVariableName,
                                         literalA,
                                         "Operand A");

    // --- Apply single-argument functions ---
    if (mathFunction == MathFunctionType.ABS)
    {
        const absResult = try(abs(operandA));
        if (absResult == undefined)
        {
            throw regenError("abs() failed: check that Operand A is a valid numeric value.");
        }
        return absResult;
    }
    else if (mathFunction == MathFunctionType.SQRT)
    {
        // sqrt() requires all unit exponents to be even (e.g. mm^2 is valid, mm is not).
        const sqrtResult = try(sqrt(operandA));
        if (sqrtResult == undefined)
        {
            throw regenError("sqrt() failed: Operand A must be a dimensionless number or a value "
                             ~ "whose unit exponents are all even (e.g., mm^2). "
                             ~ "A plain length (mm) or angle cannot be square-rooted.");
        }
        return sqrtResult;
    }
    else if (mathFunction == MathFunctionType.FLOOR)
    {
        const floorResult = try(floor(operandA));
        if (floorResult == undefined)
        {
            throw regenError("floor() failed: check that Operand A is a plain dimensionless number.");
        }
        return floorResult;
    }
    else if (mathFunction == MathFunctionType.CEIL)
    {
        const ceilResult = try(ceil(operandA));
        if (ceilResult == undefined)
        {
            throw regenError("ceil() failed: check that Operand A is a plain dimensionless number.");
        }
        return ceilResult;
    }
    else if (mathFunction == MathFunctionType.ROUND)
    {
        const roundResult = try(round(operandA));
        if (roundResult == undefined)
        {
            throw regenError("round() failed: check that Operand A is a plain dimensionless number.");
        }
        return roundResult;
    }
    else if (mathFunction == MathFunctionType.SIN)
    {
        const sinResult = try(sin(operandA));
        if (sinResult == undefined)
        {
            throw regenError("sin() failed: Operand A must be an angle value or a dimensionless number "
                             ~ "(interpreted as radians).");
        }
        return sinResult;
    }
    else if (mathFunction == MathFunctionType.COS)
    {
        const cosResult = try(cos(operandA));
        if (cosResult == undefined)
        {
            throw regenError("cos() failed: Operand A must be an angle value or a dimensionless number "
                             ~ "(interpreted as radians).");
        }
        return cosResult;
    }
    else if (mathFunction == MathFunctionType.TAN)
    {
        const tanResult = try(tan(operandA));
        if (tanResult == undefined)
        {
            throw regenError("tan() failed: Operand A must be an angle value or a dimensionless number "
                             ~ "(interpreted as radians).");
        }
        return tanResult;
    }
    else if (mathFunction == MathFunctionType.ASIN)
    {
        // asin() requires the input to be in the range [-1, 1].
        const asinResult = try(asin(operandA));
        if (asinResult == undefined)
        {
            throw regenError("asin() failed: Operand A must be a dimensionless number in the range [-1, 1].");
        }
        return asinResult;
    }
    else if (mathFunction == MathFunctionType.ACOS)
    {
        // acos() requires the input to be in the range [-1, 1].
        const acosResult = try(acos(operandA));
        if (acosResult == undefined)
        {
            throw regenError("acos() failed: Operand A must be a dimensionless number in the range [-1, 1].");
        }
        return acosResult;
    }
    else if (mathFunction == MathFunctionType.ATAN)
    {
        const atanResult = try(atan(operandA));
        if (atanResult == undefined)
        {
            throw regenError("atan() failed: check that Operand A is a plain dimensionless number.");
        }
        return atanResult;
    }
    else if (mathFunction == MathFunctionType.LOG)
    {
        // log() requires a strictly positive dimensionless number.
        const logResult = try(log(operandA));
        if (logResult == undefined)
        {
            throw regenError("log() failed: Operand A must be a positive dimensionless number (> 0).");
        }
        return logResult;
    }
    else if (mathFunction == MathFunctionType.LOG10)
    {
        // log10() requires a strictly positive dimensionless number.
        const log10Result = try(log10(operandA));
        if (log10Result == undefined)
        {
            throw regenError("log10() failed: Operand A must be a positive dimensionless number (> 0).");
        }
        return log10Result;
    }
    else if (mathFunction == MathFunctionType.EXP)
    {
        const expResult = try(exp(operandA));
        if (expResult == undefined)
        {
            throw regenError("exp() failed: check that Operand A is a plain dimensionless number.");
        }
        return expResult;
    }
    else if (mathFunction == MathFunctionType.TO_STRING)
    {
        // toString() converts any value (number, length, angle, or variable) to a string.
        // Use with String output type to store the result as a string variable.
        const toStringResult = try(toString(operandA));
        if (toStringResult == undefined)
        {
            throw regenError("toString() failed: unable to convert Operand A to a string. "
                             ~ "Ensure Operand A is a valid numeric, length, or angle value.");
        }
        return toStringResult;
    }

    // --- Resolve secondary operand B (binary functions: ATAN2, MIN, MAX) ---
    var literalB;
    if (definition.funcOperandBMode == OperandInputMode.LITERAL)
    {
        if (mathFunction == MathFunctionType.ATAN2)
        {
            // ATAN2 operand B is always a fixed NUMBER input.
            literalB = definition.funcOperandBNumber;
        }
        else
        {
            // MIN / MAX: flexible type, read from inline selector.
            if (definition.funcOperandBFlexType == ExpressionOutputType.STRING)
                literalB = definition.funcOperandBFlexString;
            else if (definition.funcOperandBFlexType == ExpressionOutputType.LENGTH)
                literalB = definition.funcOperandBFlexLength;
            else if (definition.funcOperandBFlexType == ExpressionOutputType.ANGLE)
                literalB = definition.funcOperandBFlexAngle;
            else
                literalB = definition.funcOperandBFlexNumber;
        }
    }

    const operandB = resolveTypedOperand(context,
                                         definition.funcOperandBMode,
                                         definition.funcOperandBVariableName,
                                         literalB,
                                         "Operand B");

    // --- Apply dual-argument functions ---
    if (mathFunction == MathFunctionType.ATAN2)
    {
        // atan2() requires both operands to be dimensionless numbers; fails when both are 0.
        const atan2Result = try(atan2(operandA, operandB));
        if (atan2Result == undefined)
        {
            throw regenError("atan2() failed: both operands must be dimensionless numbers, "
                             ~ "and they cannot both be 0 simultaneously.");
        }
        return atan2Result;
    }
    else if (mathFunction == MathFunctionType.MIN)
    {
        const minResult = try(min(operandA, operandB));
        if (minResult == undefined)
        {
            throw regenError("min() failed: both operands must have the same type and units.");
        }
        return minResult;
    }
    else // MAX
    {
        const maxResult = try(max(operandA, operandB));
        if (maxResult == undefined)
        {
            throw regenError("max() failed: both operands must have the same type and units.");
        }
        return maxResult;
    }
}

/**
 * Resolves a single CHAIN mode term and returns its value.
 * When the term mode is VARIABLE, the named context variable is read.
 * When the term mode is LITERAL, the plain number typed in the panel is returned.
 *
 * Variable terms may hold any type (number, length, angle, string) — those values
 * flow through unchanged to the arithmetic operations that follow.
 *
 * @param context    : Active build context.
 * @param definition : Feature definition map.
 * @param termIndex  : 1-based term index (1 to 4).
 * @returns          : Resolved term value.
 */
function resolveChainTerm(context is Context, definition is map, termIndex is number)
{
    var termMode;
    // Default values serve as safe fallbacks; all valid indices (1–4) overwrite these below.
    var variableName  = "";
    var literalNumber = 0;

    if (termIndex == 1)
    {
        termMode      = definition.chainTerm1Mode;
        variableName  = definition.chainTerm1VariableName;
        literalNumber = definition.chainTerm1Number;
    }
    else if (termIndex == 2)
    {
        termMode      = definition.chainTerm2Mode;
        variableName  = definition.chainTerm2VariableName;
        literalNumber = definition.chainTerm2Number;
    }
    else if (termIndex == 3)
    {
        termMode      = definition.chainTerm3Mode;
        variableName  = definition.chainTerm3VariableName;
        literalNumber = definition.chainTerm3Number;
    }
    else // termIndex == 4
    {
        termMode      = definition.chainTerm4Mode;
        variableName  = definition.chainTerm4VariableName;
        literalNumber = definition.chainTerm4Number;
    }

    return resolveTypedOperand(context, termMode, variableName, literalNumber, "Term " ~ termIndex);
}

/**
 * Evaluates a CHAIN mode expression (Term1 op1 Term2 [op2 Term3 [op3 Term4]]) and returns the result.
 * Operations are evaluated left-to-right with no implicit precedence.
 * String concatenation is handled automatically by applyArithmeticOperation when the ADD
 * operation is applied to string terms (no separate string path needed).
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
// String concatenation helpers
// ---------------------------------------------------------------------------

/**
 * Resolves a single STRING_CONCAT mode segment and returns its string representation.
 * Reads concatSegNType and the appropriate value field from the definition for the
 * given segment index (1 through 6).
 *
 * Conversion rules per segment type:
 *   TEXT     → the literal string as-is (user may have typed "1.5 in", "45 deg", etc.)
 *   VARIABLE → reads the named context variable, then:
 *                 string        → used as-is
 *                 number        → toString(number)
 *                 length        → toString(value / millimeter) ~ " mm"
 *                 angle         → toString(value / degree) ~ " deg"
 *                 anything else → stdlib toString()
 *
 * @param context      : Active build context (required to read VARIABLE segments).
 * @param definition   : Feature definition map.
 * @param segmentIndex : 1-based segment index (1 to 6).
 * @returns            : String representation of the segment's value.
 */
function resolveStringConcatSegment(context is Context, definition is map, segmentIndex is number) returns string
{
    var segmentType;
    var textValue    = "";
    var variableName = "";

    if (segmentIndex == 1)
    {
        segmentType  = definition.concatSeg1Type;
        if (segmentType == ConcatSegmentInputType.TEXT)
            textValue = definition.concatSeg1Text;
        else // VARIABLE
            variableName = definition.concatSeg1Variable;
    }
    else if (segmentIndex == 2)
    {
        segmentType  = definition.concatSeg2Type;
        if (segmentType == ConcatSegmentInputType.TEXT)
            textValue = definition.concatSeg2Text;
        else // VARIABLE
            variableName = definition.concatSeg2Variable;
    }
    else if (segmentIndex == 3)
    {
        segmentType  = definition.concatSeg3Type;
        if (segmentType == ConcatSegmentInputType.TEXT)
            textValue = definition.concatSeg3Text;
        else // VARIABLE
            variableName = definition.concatSeg3Variable;
    }
    else if (segmentIndex == 4)
    {
        segmentType  = definition.concatSeg4Type;
        if (segmentType == ConcatSegmentInputType.TEXT)
            textValue = definition.concatSeg4Text;
        else // VARIABLE
            variableName = definition.concatSeg4Variable;
    }
    else if (segmentIndex == 5)
    {
        segmentType  = definition.concatSeg5Type;
        if (segmentType == ConcatSegmentInputType.TEXT)
            textValue = definition.concatSeg5Text;
        else // VARIABLE
            variableName = definition.concatSeg5Variable;
    }
    else // segmentIndex == 6
    {
        segmentType  = definition.concatSeg6Type;
        if (segmentType == ConcatSegmentInputType.TEXT)
            textValue = definition.concatSeg6Text;
        else // VARIABLE
            variableName = definition.concatSeg6Variable;
    }

    // --- Convert to string ---
    if (segmentType == ConcatSegmentInputType.TEXT)
    {
        return textValue;
    }
    else // VARIABLE
    {
        const varValue = readContextVariable(context, variableName, "Segment " ~ segmentIndex);
        if (varValue is string)
            return varValue;
        if (varValue is number)
            return toString(varValue);
        if (varValue is ValueWithUnits)
        {
            // Auto-detect length and angle for friendly formatting.
            if (varValue.unit == LENGTH_UNITS)
                return toString(varValue / millimeter) ~ " mm";
            if (varValue.unit == ANGLE_UNITS)
                return toString(varValue / degree) ~ " deg";
        }
        const fallbackStr = try(toString(varValue));
        if (fallbackStr == undefined)
        {
            throw regenError("Segment " ~ segmentIndex ~ " variable '" ~ variableName ~
                             "' could not be converted to a string. " ~
                             "Ensure the variable holds a string, number, length, or angle value.");
        }
        return fallbackStr;
    }
}

/**
 * Evaluates a STRING_CONCAT mode expression and returns the concatenated string result.
 * Each segment is converted to a string by resolveStringConcatSegment, then segments
 * are joined with FeatureScript's ~ operator (left to right).
 *
 * @param context    : Active build context.
 * @param definition : Feature definition map.
 * @returns          : Concatenated string result.
 */
function evaluateStringConcatExpression(context is Context, definition is map) returns string
{
    // Always at least two segments
    var result = resolveStringConcatSegment(context, definition, 1) ~
                 resolveStringConcatSegment(context, definition, 2);

    if (definition.stringConcatLength == StringConcatLength.THREE ||
        definition.stringConcatLength == StringConcatLength.FOUR  ||
        definition.stringConcatLength == StringConcatLength.FIVE  ||
        definition.stringConcatLength == StringConcatLength.SIX)
    {
        result = result ~ resolveStringConcatSegment(context, definition, 3);
    }

    if (definition.stringConcatLength == StringConcatLength.FOUR ||
        definition.stringConcatLength == StringConcatLength.FIVE ||
        definition.stringConcatLength == StringConcatLength.SIX)
    {
        result = result ~ resolveStringConcatSegment(context, definition, 4);
    }

    if (definition.stringConcatLength == StringConcatLength.FIVE ||
        definition.stringConcatLength == StringConcatLength.SIX)
    {
        result = result ~ resolveStringConcatSegment(context, definition, 5);
    }

    if (definition.stringConcatLength == StringConcatLength.SIX)
    {
        result = result ~ resolveStringConcatSegment(context, definition, 6);
    }

    return result;
}

// ---------------------------------------------------------------------------
// Report formatting helpers
// ---------------------------------------------------------------------------

/**
 * Formats a single literal value as a copy-pasteable Onshape expression fragment.
 *   LENGTH  → "X * mm"
 *   ANGLE   → "X * deg"
 *   NUMBER  → "X"
 *   STRING  → '"text"'  (the literal string wrapped in double-quotes)
 *
 * @param literalValue : The literal value to format (ValueWithUnits for LENGTH/ANGLE, number for NUMBER,
 *                       string for STRING). Left untyped intentionally: FeatureScript has no union type
 *                       and the same function must accept all value types.
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
    else if (literalType == ExpressionOutputType.STRING)
    {
        return "\"" ~ literalValue ~ "\"";
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
 * @returns         : Operator string, e.g. " + ", " - ", " * ", " / ", " ^ ".
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
    else if (operation == ArithmeticOperation.POWER)
    {
        return " ^ ";
    }
    else
    {
        return " / ";
    }
}

/**
 * Builds a copy-pasteable Onshape expression string for ARITHMETIC mode (A op B).
 * Uses per-operand type selectors to format literal values with correct units.
 * String operands are wrapped in double-quotes.
 *
 * @param definition : Feature definition map.
 * @returns          : Expression string, e.g. "(5.0 * mm) + (3.0 * mm)".
 */
function buildArithmeticExpressionString(definition is map) returns string
{
    // --- Operand A ---
    var operandAStr;
    if (definition.operandAMode == OperandInputMode.VARIABLE)
    {
        operandAStr = "#" ~ definition.operandAVariableName;
    }
    else
    {
        if (definition.operandAValueType == ExpressionOutputType.STRING)
            operandAStr = formatLiteralAsExpression(definition.operandAStringLiteral, ExpressionOutputType.STRING);
        else if (definition.operandAValueType == ExpressionOutputType.LENGTH)
            operandAStr = formatLiteralAsExpression(definition.operandALength, ExpressionOutputType.LENGTH);
        else if (definition.operandAValueType == ExpressionOutputType.ANGLE)
            operandAStr = formatLiteralAsExpression(definition.operandAAngle, ExpressionOutputType.ANGLE);
        else
            operandAStr = formatLiteralAsExpression(definition.operandANumber, ExpressionOutputType.NUMBER);
    }

    const operatorStr = arithmeticOperationSymbol(definition.arithmeticOperation);

    // --- Operand B ---
    var operandBStr;
    if (definition.arithmeticOperation == ArithmeticOperation.MULTIPLY ||
        definition.arithmeticOperation == ArithmeticOperation.DIVIDE ||
        definition.arithmeticOperation == ArithmeticOperation.POWER)
    {
        // B is always a dimensionless scalar
        operandBStr = formatOperandAsExpression(definition.operandBScalarMode,
                                               definition.operandBScalarVariableName,
                                               definition.operandBScalar,
                                               ExpressionOutputType.NUMBER);
    }
    else if (definition.operandBMode == OperandInputMode.VARIABLE)
    {
        operandBStr = "#" ~ definition.operandBVariableName;
    }
    else
    {
        if (definition.operandBValueType == ExpressionOutputType.STRING)
            operandBStr = formatLiteralAsExpression(definition.operandBStringLiteral, ExpressionOutputType.STRING);
        else if (definition.operandBValueType == ExpressionOutputType.LENGTH)
            operandBStr = formatLiteralAsExpression(definition.operandBLength, ExpressionOutputType.LENGTH);
        else if (definition.operandBValueType == ExpressionOutputType.ANGLE)
            operandBStr = formatLiteralAsExpression(definition.operandBAngle, ExpressionOutputType.ANGLE);
        else
            operandBStr = formatLiteralAsExpression(definition.operandBNumber, ExpressionOutputType.NUMBER);
    }

    return "(" ~ operandAStr ~ ")" ~ operatorStr ~ "(" ~ operandBStr ~ ")";
}

/**
 * Builds a copy-pasteable Onshape expression string for MATH_FUNCTION mode (fn(A) or fn(A, B)).
 * Mirrors the operand-type logic of evaluateMathFunctionExpression.
 *
 * @param definition : Feature definition map.
 * @returns          : Expression string, e.g. "sin(45.0 * deg)", "min(3.0, #myVar)".
 */
function buildMathFunctionExpressionString(definition is map) returns string
{
    const mathFunction = definition.mathFunction;

    // --- Determine function name string ---
    var functionName;
    if (mathFunction == MathFunctionType.ABS)         { functionName = "abs"; }
    else if (mathFunction == MathFunctionType.SQRT)   { functionName = "sqrt"; }
    else if (mathFunction == MathFunctionType.FLOOR)  { functionName = "floor"; }
    else if (mathFunction == MathFunctionType.CEIL)   { functionName = "ceil"; }
    else if (mathFunction == MathFunctionType.ROUND)  { functionName = "round"; }
    else if (mathFunction == MathFunctionType.SIN)    { functionName = "sin"; }
    else if (mathFunction == MathFunctionType.COS)    { functionName = "cos"; }
    else if (mathFunction == MathFunctionType.TAN)    { functionName = "tan"; }
    else if (mathFunction == MathFunctionType.ASIN)   { functionName = "asin"; }
    else if (mathFunction == MathFunctionType.ACOS)   { functionName = "acos"; }
    else if (mathFunction == MathFunctionType.ATAN)   { functionName = "atan"; }
    else if (mathFunction == MathFunctionType.ATAN2)  { functionName = "atan2"; }
    else if (mathFunction == MathFunctionType.LOG)    { functionName = "log"; }
    else if (mathFunction == MathFunctionType.LOG10)  { functionName = "log10"; }
    else if (mathFunction == MathFunctionType.EXP)    { functionName = "exp"; }
    else if (mathFunction == MathFunctionType.MIN)    { functionName = "min"; }
    else if (mathFunction == MathFunctionType.MAX)    { functionName = "max"; }
    else                                              { functionName = "toString"; }

    // --- Format primary operand A ---
    var operandAStr;
    if (definition.funcOperandAMode == OperandInputMode.VARIABLE)
    {
        operandAStr = "#" ~ definition.funcOperandAVariableName;
    }
    else
    {
        if (mathFunction == MathFunctionType.SIN ||
            mathFunction == MathFunctionType.COS ||
            mathFunction == MathFunctionType.TAN)
        {
            operandAStr = formatLiteralAsExpression(definition.funcOperandAAngle, ExpressionOutputType.ANGLE);
        }
        else if (mathFunction == MathFunctionType.ASIN ||
                 mathFunction == MathFunctionType.ACOS ||
                 mathFunction == MathFunctionType.ATAN ||
                 mathFunction == MathFunctionType.ATAN2 ||
                 mathFunction == MathFunctionType.FLOOR ||
                 mathFunction == MathFunctionType.CEIL ||
                 mathFunction == MathFunctionType.ROUND ||
                 mathFunction == MathFunctionType.LOG ||
                 mathFunction == MathFunctionType.LOG10 ||
                 mathFunction == MathFunctionType.EXP)
        {
            operandAStr = formatLiteralAsExpression(definition.funcOperandANumber, ExpressionOutputType.NUMBER);
        }
        else
        {
            // ABS / SQRT / MIN / MAX / TO_STRING: use the per-operand flex type.
            if (definition.funcOperandAFlexType == ExpressionOutputType.STRING)
                operandAStr = formatLiteralAsExpression(definition.funcOperandAFlexString, ExpressionOutputType.STRING);
            else if (definition.funcOperandAFlexType == ExpressionOutputType.LENGTH)
                operandAStr = formatLiteralAsExpression(definition.funcOperandAFlexLength, ExpressionOutputType.LENGTH);
            else if (definition.funcOperandAFlexType == ExpressionOutputType.ANGLE)
                operandAStr = formatLiteralAsExpression(definition.funcOperandAFlexAngle, ExpressionOutputType.ANGLE);
            else
                operandAStr = formatLiteralAsExpression(definition.funcOperandAFlexNumber, ExpressionOutputType.NUMBER);
        }
    }

    // --- Single-argument functions ---
    if (mathFunction != MathFunctionType.ATAN2 &&
        mathFunction != MathFunctionType.MIN &&
        mathFunction != MathFunctionType.MAX)
    {
        return functionName ~ "(" ~ operandAStr ~ ")";
    }

    // --- Format secondary operand B (binary functions: ATAN2, MIN, MAX) ---
    var operandBStr;
    if (definition.funcOperandBMode == OperandInputMode.VARIABLE)
    {
        operandBStr = "#" ~ definition.funcOperandBVariableName;
    }
    else if (mathFunction == MathFunctionType.ATAN2)
    {
        operandBStr = formatLiteralAsExpression(definition.funcOperandBNumber, ExpressionOutputType.NUMBER);
    }
    else
    {
        // MIN / MAX: use per-operand flex type for B.
        if (definition.funcOperandBFlexType == ExpressionOutputType.STRING)
            operandBStr = formatLiteralAsExpression(definition.funcOperandBFlexString, ExpressionOutputType.STRING);
        else if (definition.funcOperandBFlexType == ExpressionOutputType.LENGTH)
            operandBStr = formatLiteralAsExpression(definition.funcOperandBFlexLength, ExpressionOutputType.LENGTH);
        else if (definition.funcOperandBFlexType == ExpressionOutputType.ANGLE)
            operandBStr = formatLiteralAsExpression(definition.funcOperandBFlexAngle, ExpressionOutputType.ANGLE);
        else
            operandBStr = formatLiteralAsExpression(definition.funcOperandBFlexNumber, ExpressionOutputType.NUMBER);
    }

    return functionName ~ "(" ~ operandAStr ~ ", " ~ operandBStr ~ ")";
}

/**
 * Formats a single CHAIN mode term as a copy-pasteable Onshape expression fragment
 * for the "Expression: ..." line shown in the reportFeatureInfo panel.
 *
 *   LITERAL  → the plain number value (e.g. "5.0")
 *   VARIABLE → #varName
 *
 * @param definition : Feature definition map.
 * @param termIndex  : 1-based term index (1 to 4).
 * @returns          : Expression fragment string for this term.
 */
function buildChainTermExpression(definition is map, termIndex is number) returns string
{
    var termMode;
    // Default values serve as safe fallbacks; all valid indices (1–4) overwrite these below.
    var variableName  = "";
    var literalNumber = 0;

    if (termIndex == 1)
    {
        termMode      = definition.chainTerm1Mode;
        variableName  = definition.chainTerm1VariableName;
        literalNumber = definition.chainTerm1Number;
    }
    else if (termIndex == 2)
    {
        termMode      = definition.chainTerm2Mode;
        variableName  = definition.chainTerm2VariableName;
        literalNumber = definition.chainTerm2Number;
    }
    else if (termIndex == 3)
    {
        termMode      = definition.chainTerm3Mode;
        variableName  = definition.chainTerm3VariableName;
        literalNumber = definition.chainTerm3Number;
    }
    else // termIndex == 4
    {
        termMode      = definition.chainTerm4Mode;
        variableName  = definition.chainTerm4VariableName;
        literalNumber = definition.chainTerm4Number;
    }

    if (termMode == OperandInputMode.VARIABLE)
        return "#" ~ variableName;
    else
        return toString(literalNumber);
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

/**
 * Formats a single STRING_CONCAT segment as a human-readable fragment for the
 * "Expression: ..." line shown in the reportFeatureInfo panel.
 *
 *   TEXT     → "text"     (the literal string in double-quotes)
 *   VARIABLE → #varName   (variable reference)
 *
 * @param definition   : Feature definition map.
 * @param segmentIndex : 1-based segment index (1 to 6).
 * @returns            : Expression fragment string for this segment.
 */
function buildConcatSegmentExpression(definition is map, segmentIndex is number) returns string
{
    var segmentType;
    var textValue    = "";
    var variableName = "";

    if (segmentIndex == 1)
    {
        segmentType = definition.concatSeg1Type;
        if (segmentType == ConcatSegmentInputType.TEXT)
            textValue = definition.concatSeg1Text;
        else
            variableName = definition.concatSeg1Variable;
    }
    else if (segmentIndex == 2)
    {
        segmentType = definition.concatSeg2Type;
        if (segmentType == ConcatSegmentInputType.TEXT)
            textValue = definition.concatSeg2Text;
        else
            variableName = definition.concatSeg2Variable;
    }
    else if (segmentIndex == 3)
    {
        segmentType = definition.concatSeg3Type;
        if (segmentType == ConcatSegmentInputType.TEXT)
            textValue = definition.concatSeg3Text;
        else
            variableName = definition.concatSeg3Variable;
    }
    else if (segmentIndex == 4)
    {
        segmentType = definition.concatSeg4Type;
        if (segmentType == ConcatSegmentInputType.TEXT)
            textValue = definition.concatSeg4Text;
        else
            variableName = definition.concatSeg4Variable;
    }
    else if (segmentIndex == 5)
    {
        segmentType = definition.concatSeg5Type;
        if (segmentType == ConcatSegmentInputType.TEXT)
            textValue = definition.concatSeg5Text;
        else
            variableName = definition.concatSeg5Variable;
    }
    else // segmentIndex == 6
    {
        segmentType = definition.concatSeg6Type;
        if (segmentType == ConcatSegmentInputType.TEXT)
            textValue = definition.concatSeg6Text;
        else
            variableName = definition.concatSeg6Variable;
    }

    // Format as expression fragment
    if (segmentType == ConcatSegmentInputType.TEXT)
    {
        return '"' ~ textValue ~ '"';
    }
    else // VARIABLE
    {
        return "#" ~ variableName;
    }
}

/**
 * Builds the expression string for STRING_CONCAT mode shown in the reportFeatureInfo panel.
 * Segments are separated by " ~ " to reflect FeatureScript concatenation syntax.
 *
 * @param definition : Feature definition map.
 * @returns          : Expression string, e.g. '"prefix " ~ #myVar ~ " suffix"'.
 */
function buildStringConcatExpressionString(definition is map) returns string
{
    var expressionString = buildConcatSegmentExpression(definition, 1) ~ " ~ " ~
                           buildConcatSegmentExpression(definition, 2);

    if (definition.stringConcatLength == StringConcatLength.THREE ||
        definition.stringConcatLength == StringConcatLength.FOUR  ||
        definition.stringConcatLength == StringConcatLength.FIVE  ||
        definition.stringConcatLength == StringConcatLength.SIX)
    {
        expressionString = expressionString ~ " ~ " ~ buildConcatSegmentExpression(definition, 3);
    }

    if (definition.stringConcatLength == StringConcatLength.FOUR ||
        definition.stringConcatLength == StringConcatLength.FIVE ||
        definition.stringConcatLength == StringConcatLength.SIX)
    {
        expressionString = expressionString ~ " ~ " ~ buildConcatSegmentExpression(definition, 4);
    }

    if (definition.stringConcatLength == StringConcatLength.FIVE ||
        definition.stringConcatLength == StringConcatLength.SIX)
    {
        expressionString = expressionString ~ " ~ " ~ buildConcatSegmentExpression(definition, 5);
    }

    if (definition.stringConcatLength == StringConcatLength.SIX)
    {
        expressionString = expressionString ~ " ~ " ~ buildConcatSegmentExpression(definition, 6);
    }

    return expressionString;
}
