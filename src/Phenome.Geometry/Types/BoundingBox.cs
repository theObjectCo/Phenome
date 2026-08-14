using System.Globalization;

namespace Phenome.Geometry.Types;

/// <summary>
/// An immutable axis-aligned range in space, as three <see cref="Interval"/>s.
/// </summary>
/// <remarks>
/// A container for three ranges and nothing else. Measurement, containment, set operations and everything
/// else live in <see cref="BoundingBoxOps"/>.
/// <para>
/// Three intervals rather than a minimum and a maximum point, because that is what a range is, and because it
/// composes: <see cref="IntervalOps"/> already knows how to grow, clamp, union and evaluate one, and
/// <see cref="MeshBuilders.CreateBox(Interval, Interval, Interval)"/> already takes three. Ask
/// <see cref="BoundingBoxOps.Min"/> and <see cref="BoundingBoxOps.Max"/> when the corners are what you
/// want.
/// </para>
/// <para>
/// The ranges are always stored increasing, so a box has no direction and cannot be inside out. A box may be
/// flat — the bounding box of a planar mesh has one range of zero width — and that is valid; test for it with
/// <see cref="BoundingBoxOps.IsDegenerate"/> rather than assuming volume.
/// </para>
/// <para>
/// There is no public constructor; the factories on <see cref="BoundingBoxOps"/> are the way in.
/// </para>
/// </remarks>
public readonly struct BoundingBox : IEquatable<BoundingBox>, IFormattable
{
    /// <summary>Assembles a box from ranges that are already sorted increasing.</summary>
    /// <remarks>Internal because it trusts its inputs.</remarks>
    internal BoundingBox(Interval x, Interval y, Interval z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>The range along X.</summary>
    public Interval X { get; }

    /// <summary>The range along Y.</summary>
    public Interval Y { get; }

    /// <summary>The range along Z.</summary>
    public Interval Z { get; }

    /// <summary>
    /// A box whose ranges are all unset, used to signal "no value" and as the identity for growing.
    /// </summary>
    /// <remarks>
    /// Growing an unset box by a point gives a box containing just that point, so a bound can be accumulated
    /// starting here without a special case for the first item. Test for it with
    /// <see cref="BoundingBoxOps.IsValid"/> rather than with <c>==</c>.
    /// </remarks>
    public static BoundingBox Unset => new(Interval.Unset, Interval.Unset, Interval.Unset);

    /// <summary>The unit cube from the origin, with all three ranges 0 to 1.</summary>
    public static BoundingBox Unit => new(Interval.Unit, Interval.Unit, Interval.Unit);

    /// <summary>Splits the box into its three ranges.</summary>
    public void Deconstruct(out Interval x, out Interval y, out Interval z)
    {
        x = X;
        y = Y;
        z = Z;
    }

    /// <inheritdoc/>
    public override string ToString() => ToString(null, null);

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        if (!BoundingBoxOps.IsValid(this))
        {
            return "BoundingBox(unset)";
        }

        formatProvider ??= CultureInfo.InvariantCulture;

        return string.Concat(
            "BoundingBox(",
            BoundingBoxOps.Min(this).ToString(format, formatProvider),
            " .. ",
            BoundingBoxOps.Max(this).ToString(format, formatProvider),
            ")");
    }

    /// <summary>Compares all three ranges for exact equality, treating NaN as equal to NaN.</summary>
    /// <remarks>
    /// Use <see cref="BoundingBoxOps.EpsilonEquals"/> for boxes that have been through arithmetic.
    /// </remarks>
    public bool Equals(BoundingBox other) =>
        X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BoundingBox other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    /// <summary>
    /// Compares all three ranges using IEEE rules, so any comparison involving NaN is
    /// <see langword="false"/>.
    /// </summary>
    public static bool operator ==(in BoundingBox a, in BoundingBox b) =>
        a.X == b.X && a.Y == b.Y && a.Z == b.Z;

    /// <summary>The negation of <c>==</c>.</summary>
    public static bool operator !=(in BoundingBox a, in BoundingBox b) => !(a == b);
}
