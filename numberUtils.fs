FeatureScript 1948;
import(path : "onshape/std/common.fs", version : "1948.0");

/**
 * Creates pseudo random numbers. tip: Use the output at the seed for the next one in a loop or use the feature ID as the seed for really random stuff.
 */
export function pseudoRandomNumber(seed is number) returns number
{
    // Linear congruential generator constants
    const m = 2147483648; // 2^31
    const a = 1103515245;
    const c = 12345;

    // Generate a pseudo-random number using the linear congruential generator
    const randomNumber = (a * seed + c) % m;

    return randomNumber;
}

/**
 * Creates pseudo random numbers within a given range. tip: Use the output at the seed for the next one in a loop or use the feature ID as the seed for really random stuff.
 */
export function pseudoRandomNumber(seed is number, min is number, max is number) returns number
{
    // Linear congruential generator constants
    const m = 2147483648; // 2^31
    const a = 1103515245;
    const c = 12345;

    // Generate a pseudo-random number using the linear congruential generator
    const randomNumber = (a * seed + c) % m;

    return remap(randomNumber, 0, m, min, max);

}

/**
 * Creates pseudo random numbers within a given range. tip: Use the output at the seed for the next one in a loop or use the feature ID as the seed for really random stuff.
 */
export function pseudoRandomNumber(seed is number, min is ValueWithUnits, max is ValueWithUnits, unit is ValueWithUnits) returns ValueWithUnits
{
    // Linear congruential generator constants
    const m = 2147483648; // 2^31
    const a = 1103515245;
    const c = 12345;

    // Generate a pseudo-random number using the linear congruential generator
    const randomNumber = (a * seed + c) % m;

    return remap(randomNumber * unit, 0 * unit, m * unit, min, max);

}

/**
 * Places a number from a given range onto a 0to1 range
 */
export function normalizeValue(value, min, max)
{
    return ((value - min) / (max - min));
}

/**
 * Takes a value from a range and returns the corresponding value from another range. For example if value=5 and the min/max is 0-10 with a target range of 0-2 it will return 1 (halfway between each range).
 */
export function remap(value, min, max, targetMin, targetMax)
{
    return (value - min) * (targetMax - targetMin) / (max - min) + targetMin;
}


/**
 *
 */
export function sineWave(value, frequency, amplitude, phase)
{
    return amplitude * sin(value - frequency + phase);
}

/**
 * Takes a linear range from 0 to 1 and returns the range eased to a half cosine wave.
 */
export function easeLinearRange(num is number) returns number
{
    //https://www.mathsisfun.com/algebra/amplitude-period-frequency-phase-shift.html
    const a = 1 / 2; // amplitude is A
    const b = (2 * PI * radian) / 2; // period is 2π/B
    const c = -1; // phase shift is C (positive is to the left)
    const d = a; // vertical shift is D
    return a * cos(b * (num + c)) + d; // y = A sin(B(x + C)) + D
}

export function isEven(num is number) returns boolean
{
    return num % 2 == 0;
}

/**
 * Given an array of numbers and a target number, find the number in the array that is nearest in value to the target.
 * @return {{
 *      @field difference : Nearest number minus the target number
 *      @field index : The index of the nearest number
 *      @field closestValue : The value of the nearest number
 * }}
 **/
export function findClosestNumber(arr, target) returns map
{
    var difference;
    var distance;
    var index;
    var closestValue;

    for (var i = 0; i < size(arr); i += 1)
    {
        const newDifference = arr[i] - target;
        const newDistance = abs(newDifference);

        if (i == 0)
        {
            difference = newDifference;
            distance = newDistance;
            closestValue = arr[0];
            index = i;
        }
        else
        {
            const newDistance = abs(newDifference);
            if (newDistance < distance)
            {
                distance = newDistance;
                difference = newDifference;
                closestValue = arr[i];
                index = i;
            }
        }
    }
    return { "difference" : difference, "index" : index, "closestValue" : closestValue };
}


