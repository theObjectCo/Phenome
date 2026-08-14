using System.Globalization;

namespace Phenome.Geometry.Types;

/// <summary>
/// An immutable origin with a right-handed orthonormal frame: a position in space plus an orientation.
/// </summary>
/// <remarks>
/// A container for an origin and three axes. Projection, distance, evaluation at frame coordinates and
/// transformation all live in <see cref="PlaneOps"/>.
/// <para>
/// The three axes are always unit length, mutually perpendicular, and right-handed, so that
/// <see cref="ZAxis"/> equals <see cref="XAxis"/> crossed with <see cref="YAxis"/>. That invariant is
/// established by the factories in <see cref="PlaneOps"/>, which is the only way in — there is
/// no public constructor and no settable member, so the frame cannot be assembled inconsistently.
/// </para>
/// <para>
/// The previous implementation accepted an X and a Y vector and then silently discarded Y, replacing it
/// with X rotated a quarter turn, so a left-handed pair flipped the normal without telling anyone. It
/// also normalised without checking, so two parallel axes produced a plane full of NaN.
/// <see cref="PlaneOps.TryCreateFromAxes"/> does neither.
/// </para>
/// </remarks>
public readonly struct Plane : IEquatable<Plane>, IFormattable
{
    /// <summary>
    /// Assembles a plane from axes that are already orthonormal and right-handed.
    /// </summary>
    /// <remarks>Internal because it trusts its inputs.</remarks>
    internal Plane(Point3d origin, Vector3d xAxis, Vector3d yAxis, Vector3d zAxis)
    {
        Origin = origin;
        XAxis = xAxis;
        YAxis = yAxis;
        ZAxis = zAxis;
    }

    /// <summary>The position the frame is anchored at.</summary>
    public Point3d Origin { get; }

    /// <summary>The first axis of the frame, unit length.</summary>
    public Vector3d XAxis { get; }

    /// <summary>The second axis of the frame, unit length and perpendicular to <see cref="XAxis"/>.</summary>
    public Vector3d YAxis { get; }

    /// <summary>
    /// The third axis of the frame, unit length and equal to <see cref="XAxis"/> crossed with
    /// <see cref="YAxis"/>.
    /// </summary>
    public Vector3d ZAxis { get; }

    /// <summary>The plane normal, which is <see cref="ZAxis"/> under a more geometric name.</summary>
    public Vector3d Normal => ZAxis;

    /// <summary>The plane through the origin spanned by the world X and Y axes.</summary>
    public static Plane WorldXY => new(Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis);

    /// <summary>The plane through the origin spanned by the world Y and Z axes.</summary>
    public static Plane WorldYZ => new(Point3d.Origin, Vector3d.YAxis, Vector3d.ZAxis, Vector3d.XAxis);

    /// <summary>The plane through the origin spanned by the world Z and X axes.</summary>
    public static Plane WorldZX => new(Point3d.Origin, Vector3d.ZAxis, Vector3d.XAxis, Vector3d.YAxis);

    /// <summary>
    /// A plane whose origin and axes are all <see cref="double.NaN"/>, used to signal "no value".
    /// </summary>
    /// <remarks>
    /// Test for it with <see cref="PlaneOps.IsValid"/> rather than with <c>==</c>.
    /// </remarks>
    public static Plane Unset => new(Point3d.Unset, Vector3d.Unset, Vector3d.Unset, Vector3d.Unset);

    /// <summary>Splits the plane into its origin and axes.</summary>
    public void Deconstruct(out Point3d origin, out Vector3d xAxis, out Vector3d yAxis, out Vector3d zAxis)
    {
        origin = Origin;
        xAxis = XAxis;
        yAxis = YAxis;
        zAxis = ZAxis;
    }

    /// <inheritdoc/>
    public override string ToString() => ToString(null, null);

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        if (!PlaneOps.IsValid(this))
        {
            return "Plane(unset)";
        }

        formatProvider ??= CultureInfo.InvariantCulture;

        return string.Concat(
            "Plane(O ",
            Origin.ToString(format, formatProvider),
            "; N ",
            ZAxis.ToString(format, formatProvider),
            ")");
    }

    /// <summary>Compares origin and axes for exact equality, treating NaN as equal to NaN.</summary>
    /// <remarks>
    /// Use <see cref="PlaneOps.EpsilonEquals"/> for planes that have been through arithmetic.
    /// </remarks>
    public bool Equals(Plane other) =>
        Origin.Equals(other.Origin) &&
        XAxis.Equals(other.XAxis) &&
        YAxis.Equals(other.YAxis) &&
        ZAxis.Equals(other.ZAxis);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Plane other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Origin, XAxis, YAxis, ZAxis);

    /// <summary>
    /// Compares origin and axes using IEEE rules, so any comparison involving NaN is
    /// <see langword="false"/>.
    /// </summary>
    public static bool operator ==(in Plane a, in Plane b) =>
        a.Origin == b.Origin && a.XAxis == b.XAxis && a.YAxis == b.YAxis && a.ZAxis == b.ZAxis;

    /// <summary>The negation of <c>==</c>.</summary>
    public static bool operator !=(in Plane a, in Plane b) => !(a == b);
}
