using System.Globalization;

namespace Phenome.Geometry.Types;

/// <summary>
/// An immutable circle: a frame plus a radius.
/// </summary>
/// <remarks>
/// A container for a plane and a radius and nothing else. Evaluation, tessellation, projection and
/// transformation all live in <see cref="CircleOps"/>.
/// <para>
/// The circle lies in the plane, centred on its origin. Angles are measured from
/// <see cref="Phenome.Geometry.Types.Plane.XAxis"/> towards <see cref="Phenome.Geometry.Types.Plane.YAxis"/>, so the
/// sweep is counter-clockwise looking down the plane normal. Because the frame is carried whole rather
/// than reduced to a centre and a normal, the circle has a definite start point and a definite direction —
/// which is what lets an arc be cut out of it unambiguously.
/// </para>
/// <para>
/// There is no public constructor; the factories on <see cref="CircleOps"/> are the way in.
/// </para>
/// </remarks>
public readonly struct Circle : IEquatable<Circle>, IFormattable
{
    /// <summary>Assembles a circle from a plane and a radius that are already known to be good.</summary>
    /// <remarks>Internal because it trusts its inputs.</remarks>
    internal Circle(Plane plane, double radius)
    {
        Plane = plane;
        Radius = radius;
    }

    /// <summary>The frame the circle lies in, centred on the circle.</summary>
    public Plane Plane { get; }

    /// <summary>The distance from the centre to the circle.</summary>
    public double Radius { get; }

    /// <summary>
    /// A circle with an unset plane and a <see cref="double.NaN"/> radius, used to signal "no value".
    /// </summary>
    /// <remarks>Test for it with <see cref="CircleOps.IsValid"/> rather than with <c>==</c>.</remarks>
    public static Circle Unset => new(Plane.Unset, double.NaN);

    /// <summary>Splits the circle into its plane and radius.</summary>
    public void Deconstruct(out Plane plane, out double radius)
    {
        plane = Plane;
        radius = Radius;
    }

    /// <inheritdoc/>
    public override string ToString() => ToString(null, null);

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        if (!CircleOps.IsValid(this))
        {
            return "Circle(unset)";
        }

        formatProvider ??= CultureInfo.InvariantCulture;

        return string.Concat(
            "Circle(C ",
            Plane.Origin.ToString(format, formatProvider),
            "; R ",
            Radius.ToString(format, formatProvider),
            ")");
    }

    /// <summary>Compares plane and radius for exact equality, treating NaN as equal to NaN.</summary>
    /// <remarks>
    /// Two circles occupying the same points are not equal unless their frames also match, because the
    /// frame decides where angle zero is. Use <see cref="CircleOps.EpsilonEquals"/> for circles
    /// that have been through arithmetic.
    /// </remarks>
    public bool Equals(Circle other) => Plane.Equals(other.Plane) && Radius.Equals(other.Radius);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Circle other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Plane, Radius);

    /// <summary>
    /// Compares plane and radius using IEEE rules, so any comparison involving NaN is
    /// <see langword="false"/>.
    /// </summary>
    public static bool operator ==(in Circle a, in Circle b) => a.Plane == b.Plane && a.Radius == b.Radius;

    /// <summary>The negation of <c>==</c>.</summary>
    public static bool operator !=(in Circle a, in Circle b) => !(a == b);
}
