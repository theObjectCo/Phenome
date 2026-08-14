using System.Globalization;

namespace Phenome.Geometry.Types;

/// <summary>
/// An immutable straight segment between two points.
/// </summary>
/// <remarks>
/// A container for two endpoints and nothing else. Length, direction, projection, evaluation and
/// transformation all live in <see cref="LineOps"/>.
/// <para>
/// There is no public constructor; <see cref="LineOps.Create"/> is the way in.
/// </para>
/// <para>
/// The segment doubles as the infinite line through its two endpoints. Operations that could mean
/// either take a <c>limitToSegment</c> flag, defaulting to the infinite line. Parameters are
/// normalised: 0 is <see cref="From"/> and 1 is <see cref="To"/>, regardless of length.
/// </para>
/// </remarks>
public readonly struct Line : IEquatable<Line>, IFormattable
{
    internal Line(Point3d from, Point3d to)
    {
        From = from;
        To = to;
    }

    /// <summary>The start of the segment, at parameter 0.</summary>
    public Point3d From { get; }

    /// <summary>The end of the segment, at parameter 1.</summary>
    public Point3d To { get; }

    /// <summary>
    /// A segment between two unset points, used to signal "no value".
    /// </summary>
    /// <remarks>Test for it with <see cref="LineOps.IsValid"/> rather than with <c>==</c>.</remarks>
    public static Line Unset => new(Point3d.Unset, Point3d.Unset);

    /// <summary>Splits the segment into its endpoints.</summary>
    public void Deconstruct(out Point3d from, out Point3d to)
    {
        from = From;
        to = To;
    }

    /// <inheritdoc/>
    public override string ToString() => ToString(null, null);

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        formatProvider ??= CultureInfo.InvariantCulture;

        return string.Concat(
            "Line(",
            From.ToString(format, formatProvider),
            " -> ",
            To.ToString(format, formatProvider),
            ")");
    }

    /// <summary>
    /// Compares endpoints for exact equality, treating NaN as equal to NaN so that unset segments work
    /// as dictionary keys.
    /// </summary>
    /// <remarks>
    /// Use <see cref="LineOps.EpsilonEquals"/> for values that have been through arithmetic.
    /// </remarks>
    public bool Equals(Line other) => From.Equals(other.From) && To.Equals(other.To);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Line other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(From, To);

    /// <summary>
    /// Compares endpoints using IEEE rules, so any comparison involving NaN is
    /// <see langword="false"/>.
    /// </summary>
    public static bool operator ==(Line a, Line b) => a.From == b.From && a.To == b.To;

    /// <summary>The negation of <c>==</c>.</summary>
    public static bool operator !=(Line a, Line b) => !(a == b);
}
