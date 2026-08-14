using System.Globalization;

namespace Phenome.Geometry.Types;

/// <summary>
/// An immutable pair of numbers bounding a one-dimensional range.
/// </summary>
/// <remarks>
/// A container for two numbers and nothing else. Length, midpoint, evaluation, clamping and set operations
/// all live in <see cref="IntervalOps"/>.
/// <para>
/// The pair is deliberately allowed to decrease, so that <c>T0</c> may be greater than <c>T1</c>. A
/// decreasing interval is not an error: it carries a direction, which is what makes an arc swept clockwise
/// expressible at all. Anything that needs the bounds in order should ask
/// <see cref="IntervalOps.Min"/> and <see cref="IntervalOps.Max"/> rather than assume.
/// </para>
/// <para>
/// There is no public constructor; <see cref="IntervalOps.Create(double, double)"/> is the way in.
/// </para>
/// </remarks>
public readonly struct Interval : IEquatable<Interval>, IFormattable
{
    internal Interval(double t0, double t1)
    {
        T0 = t0;
        T1 = t1;
    }

    /// <summary>The bound at normalised parameter 0.</summary>
    public double T0 { get; }

    /// <summary>The bound at normalised parameter 1.</summary>
    public double T1 { get; }

    /// <summary>The interval from 0 to 1, the domain normalised parameters live in.</summary>
    public static Interval Unit => new(0, 1);

    /// <summary>The interval from 0 to twice pi, one full turn measured in radians.</summary>
    public static Interval FullTurn => new(0, Math.Tau);

    /// <summary>
    /// An interval of two <see cref="double.NaN"/> bounds, used to signal "no value".
    /// </summary>
    /// <remarks>Test for it with <see cref="IntervalOps.IsValid"/> rather than with <c>==</c>.</remarks>
    public static Interval Unset => new(double.NaN, double.NaN);

    /// <summary>Splits the interval into its bounds, in stored order.</summary>
    public void Deconstruct(out double t0, out double t1)
    {
        t0 = T0;
        t1 = T1;
    }

    /// <inheritdoc/>
    public override string ToString() => ToString(null, null);

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        formatProvider ??= CultureInfo.InvariantCulture;

        return string.Concat(
            "Interval(",
            T0.ToString(format, formatProvider),
            " .. ",
            T1.ToString(format, formatProvider),
            ")");
    }

    /// <summary>
    /// Compares bounds for exact equality, treating NaN as equal to NaN so that unset intervals work as
    /// dictionary keys.
    /// </summary>
    /// <remarks>
    /// Order matters: the interval 1 to 0 does not equal the interval 0 to 1, because they sweep opposite
    /// ways. Use <see cref="IntervalOps.EpsilonEquals"/> for bounds that have been through
    /// arithmetic.
    /// </remarks>
    public bool Equals(Interval other) => T0.Equals(other.T0) && T1.Equals(other.T1);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Interval other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(T0, T1);

    /// <summary>
    /// Compares bounds using IEEE rules, so any comparison involving NaN is <see langword="false"/>.
    /// </summary>
    public static bool operator ==(Interval a, Interval b) => a.T0 == b.T0 && a.T1 == b.T1;

    /// <summary>The negation of <c>==</c>.</summary>
    public static bool operator !=(Interval a, Interval b) => !(a == b);
}
