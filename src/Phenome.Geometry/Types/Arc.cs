using System.Globalization;

namespace Phenome.Geometry.Types;

/// <summary>
/// An immutable circular arc: a frame, a radius, and the range of angles swept.
/// </summary>
/// <remarks>
/// A container for those three things and nothing else. Evaluation, tessellation, projection and
/// transformation all live in <see cref="ArcOps"/>.
/// <para>
/// Angles are in radians, measured from <see cref="Phenome.Geometry.Types.Plane.XAxis"/> towards
/// <see cref="Phenome.Geometry.Types.Plane.YAxis"/>. The domain is an <see cref="Interval"/> rather than a start
/// and a sweep, which means a decreasing domain expresses a clockwise arc without a sign convention to
/// remember, and the same arc traced backwards is just the reversed domain.
/// </para>
/// <para>
/// The domain is not reduced into any canonical range. An arc may sweep more than a full turn if
/// something built it that way, and it will evaluate consistently; nothing here silently rewrites it,
/// because doing so would lose the distinction between an arc and the same arc plus a lap.
/// </para>
/// <para>
/// There is no public constructor; the factories on <see cref="ArcOps"/> are the way in.
/// </para>
/// </remarks>
public readonly struct Arc : IEquatable<Arc>, IFormattable
{
    /// <summary>Assembles an arc from parts that are already known to be good.</summary>
    /// <remarks>Internal because it trusts its inputs.</remarks>
    internal Arc(Plane plane, double radius, Interval angleDomain)
    {
        Plane = plane;
        Radius = radius;
        AngleDomain = angleDomain;
    }

    /// <summary>The frame the arc lies in, centred on the arc's centre of curvature.</summary>
    public Plane Plane { get; }

    /// <summary>The distance from the centre to the arc.</summary>
    public double Radius { get; }

    /// <summary>
    /// The angles in radians the arc spans, measured from the plane's X axis. Decreasing means clockwise.
    /// </summary>
    public Interval AngleDomain { get; }

    /// <summary>
    /// An arc with an unset plane, a <see cref="double.NaN"/> radius and an unset domain, used to signal
    /// "no value".
    /// </summary>
    /// <remarks>Test for it with <see cref="ArcOps.IsValid"/> rather than with <c>==</c>.</remarks>
    public static Arc Unset => new(Plane.Unset, double.NaN, Interval.Unset);

    /// <summary>Splits the arc into its plane, radius and angle domain.</summary>
    public void Deconstruct(out Plane plane, out double radius, out Interval angleDomain)
    {
        plane = Plane;
        radius = Radius;
        angleDomain = AngleDomain;
    }

    /// <inheritdoc/>
    public override string ToString() => ToString(null, null);

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        if (!ArcOps.IsValid(this))
        {
            return "Arc(unset)";
        }

        formatProvider ??= CultureInfo.InvariantCulture;

        return string.Concat(
            "Arc(C ",
            Plane.Origin.ToString(format, formatProvider),
            "; R ",
            Radius.ToString(format, formatProvider),
            "; A ",
            AngleDomain.ToString(format, formatProvider),
            ")");
    }

    /// <summary>Compares plane, radius and domain for exact equality, treating NaN as equal to NaN.</summary>
    /// <remarks>
    /// Use <see cref="ArcOps.EpsilonEquals"/> for arcs that have been through arithmetic.
    /// </remarks>
    public bool Equals(Arc other) =>
        Plane.Equals(other.Plane) && Radius.Equals(other.Radius) && AngleDomain.Equals(other.AngleDomain);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Arc other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Plane, Radius, AngleDomain);

    /// <summary>
    /// Compares plane, radius and domain using IEEE rules, so any comparison involving NaN is
    /// <see langword="false"/>.
    /// </summary>
    public static bool operator ==(in Arc a, in Arc b) =>
        a.Plane == b.Plane && a.Radius == b.Radius && a.AngleDomain == b.AngleDomain;

    /// <summary>The negation of <c>==</c>.</summary>
    public static bool operator !=(in Arc a, in Arc b) => !(a == b);
}
