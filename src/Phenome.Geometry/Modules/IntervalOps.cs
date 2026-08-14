using System.Diagnostics.CodeAnalysis;

namespace Phenome.Geometry.Modules;

/// <summary>
/// Everything you can do with an <see cref="Interval"/>.
/// </summary>
/// <remarks>
/// Two words are kept apart throughout. A <em>normalised</em> parameter runs 0 to 1 across the interval
/// whichever way it points; an <em>interval</em> parameter is a value in the interval's own units.
/// <see cref="ParameterAt"/> converts one way and <see cref="NormalizedParameterAt"/> the other.
/// </remarks>
public static class IntervalOps
{
    /// <summary>An interval between two bounds, in the order given.</summary>
    /// <remarks>
    /// The bounds are not sorted. Passing a larger value first produces a decreasing interval on purpose;
    /// use <see cref="Increasing"/> when the order is not wanted.
    /// </remarks>
    public static Interval Create(double t0, double t1) => new(t0, t1);

    /// <summary>An interval covering a single value, with both bounds the same.</summary>
    public static Interval Create(double t) => new(t, t);

    /// <summary>An interval covering both bounds, sorted so that it increases.</summary>
    public static Interval CreateFromSorted(double a, double b) => a <= b ? new Interval(a, b) : new Interval(b, a);

    /// <summary>
    /// An interval centred on a value, reaching <paramref name="radius"/> either side of it.
    /// </summary>
    public static Interval CreateFromCenter(double center, double radius) =>
        new(center - Math.Abs(radius), center + Math.Abs(radius));

    /// <summary>
    /// The tightest interval containing every value, or <see cref="Interval.Unset"/> when there are none.
    /// </summary>
    public static Interval Bound(ReadOnlySpan<double> values)
    {
        if (values.Length == 0)
        {
            return Interval.Unset;
        }

        double min = values[0];
        double max = values[0];

        for (int i = 1; i < values.Length; i++)
        {
            double value = values[i];

            if (value < min)
            {
                min = value;
            }
            else if (value > max)
            {
                max = value;
            }
        }

        return new Interval(min, max);
    }

    /// <summary><see langword="true"/> when both bounds are finite.</summary>
    public static bool IsValid(Interval interval) =>
        double.IsFinite(interval.T0) && double.IsFinite(interval.T1);

    /// <summary>
    /// The signed extent of the interval, which is <c>T1 - T0</c>.
    /// </summary>
    /// <remarks>
    /// Signed on purpose, so that a decreasing interval reports a negative length and the direction
    /// survives. Take <see cref="Math.Abs(double)"/> of it when only the size matters.
    /// </remarks>
    public static double Length(Interval interval) => interval.T1 - interval.T0;

    /// <summary>The smaller of the two bounds.</summary>
    public static double Min(Interval interval) => Math.Min(interval.T0, interval.T1);

    /// <summary>The larger of the two bounds.</summary>
    public static double Max(Interval interval) => Math.Max(interval.T0, interval.T1);

    /// <summary>The value halfway between the bounds.</summary>
    /// <remarks>
    /// Averaged rather than offset from a bound, so that an interval far from the origin does not lose
    /// precision.
    /// </remarks>
    public static double Mid(Interval interval) => (interval.T0 + interval.T1) * 0.5;

    /// <summary><see langword="true"/> when <c>T0</c> is less than <c>T1</c>.</summary>
    public static bool IsIncreasing(Interval interval) => interval.T0 < interval.T1;

    /// <summary><see langword="true"/> when <c>T0</c> is greater than <c>T1</c>.</summary>
    public static bool IsDecreasing(Interval interval) => interval.T0 > interval.T1;

    /// <summary><see langword="true"/> when both bounds are exactly the same value.</summary>
    /// <remarks>
    /// A singleton is valid, and is what bounding a single value produces. It is worth testing for before
    /// dividing by <see cref="Length"/>.
    /// </remarks>
    public static bool IsSingleton(Interval interval) => interval.T0 == interval.T1;

    /// <summary>The interval with its bounds swapped, so that it sweeps the other way.</summary>
    public static Interval Reversed(Interval interval) => new(interval.T1, interval.T0);

    /// <summary>The interval with its bounds sorted, so that it increases.</summary>
    public static Interval Increasing(Interval interval) =>
        interval.T0 <= interval.T1 ? interval : new Interval(interval.T1, interval.T0);

    /// <summary>The interval value at a normalised parameter, where 0 is <c>T0</c> and 1 is <c>T1</c>.</summary>
    /// <remarks>
    /// Not clamped: a parameter outside 0 to 1 extrapolates beyond the bounds, which is what makes this
    /// usable for offsetting a domain.
    /// </remarks>
    public static double ParameterAt(Interval interval, double normalizedParameter) =>
        interval.T0 + ((interval.T1 - interval.T0) * normalizedParameter);

    /// <summary>
    /// Where an interval value falls as a normalised parameter, where <c>T0</c> is 0 and <c>T1</c> is 1.
    /// </summary>
    /// <remarks>
    /// A singleton interval has no answer to this and returns <see cref="double.NaN"/>, because every
    /// value would map to the same place and no parameter could be recovered.
    /// </remarks>
    public static double NormalizedParameterAt(Interval interval, double parameter)
    {
        double length = interval.T1 - interval.T0;

        if (length == 0)
        {
            return double.NaN;
        }

        return (parameter - interval.T0) / length;
    }

    /// <summary><see langword="true"/> when a value lies between the bounds, whichever way they point.</summary>
    /// <param name="interval">The interval to test against.</param>
    /// <param name="parameter">The value to test.</param>
    /// <param name="includeBounds">
    /// Whether a value sitting exactly on a bound counts as inside. Defaults to <see langword="true"/>.
    /// </param>
    public static bool Includes(Interval interval, double parameter, bool includeBounds = true)
    {
        double min = Min(interval);
        double max = Max(interval);

        return includeBounds
            ? parameter >= min && parameter <= max
            : parameter > min && parameter < max;
    }

    /// <summary>A value moved to the nearest bound if it lies outside the interval.</summary>
    public static double Clamped(Interval interval, double parameter) =>
        Math.Clamp(parameter, Min(interval), Max(interval));

    /// <summary>
    /// The interval grown just enough to include a value, keeping the direction it already had.
    /// </summary>
    public static Interval Grown(Interval interval, double parameter)
    {
        if (!IsValid(interval))
        {
            return new Interval(parameter, parameter);
        }

        if (Includes(interval, parameter))
        {
            return interval;
        }

        return IsDecreasing(interval)
            ? new Interval(Math.Max(interval.T0, parameter), Math.Min(interval.T1, parameter))
            : new Interval(Math.Min(interval.T0, parameter), Math.Max(interval.T1, parameter));
    }

    /// <summary>The smallest increasing interval containing both, whether or not they overlap.</summary>
    public static Interval Union(Interval a, Interval b) =>
        new(Math.Min(Min(a), Min(b)), Math.Max(Max(a), Max(b)));

    /// <summary>
    /// <see langword="true"/> when the two intervals share at least one value.
    /// </summary>
    /// <remarks>
    /// Touching at a single bound counts as overlapping. Direction is ignored; only the covered values
    /// matter.
    /// </remarks>
    public static bool Overlaps(Interval a, Interval b) => Min(a) <= Max(b) && Min(b) <= Max(a);

    /// <summary>The increasing interval covered by both, or <see langword="null"/> when they are disjoint.</summary>
    /// <param name="a">The first interval.</param>
    /// <param name="b">The second interval.</param>
    /// <param name="intersection">
    /// The shared range on success, <see langword="null"/> otherwise. Two intervals touching at a bound
    /// succeed and give a singleton.
    /// </param>
    /// <returns><see langword="true"/> when the intervals overlap.</returns>
    public static bool TryIntersection(
        Interval a,
        Interval b,
        [NotNullWhen(true)] out Interval? intersection)
    {
        if (!IsValid(a) || !IsValid(b) || !Overlaps(a, b))
        {
            intersection = null;
            return false;
        }

        intersection = new Interval(Math.Max(Min(a), Min(b)), Math.Min(Max(a), Max(b)));
        return true;
    }

    /// <summary>Compares bounds with a tolerance, in stored order.</summary>
    public static bool EpsilonEquals(Interval a, Interval b, double tolerance = Tolerance.Distance) =>
        Math.Abs(a.T0 - b.T0) <= tolerance && Math.Abs(a.T1 - b.T1) <= tolerance;
}
