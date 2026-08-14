using System.Globalization;

namespace Phenome.Geometry.Types;

/// <summary>
/// An immutable direction and magnitude in three-dimensional space.
/// </summary>
/// <remarks>
/// A container for three components and nothing else. Length, normalisation, dot and cross products,
/// angles and transformation all live in <see cref="VectorOps"/>.
/// <para>
/// There is no public constructor; <see cref="VectorOps.Create"/> is the way in.
/// </para>
/// </remarks>
public readonly struct Vector3d : IEquatable<Vector3d>, IFormattable
{
    internal Vector3d(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>The X component.</summary>
    public double X { get; }

    /// <summary>The Y component.</summary>
    public double Y { get; }

    /// <summary>The Z component.</summary>
    public double Z { get; }

    /// <summary>The zero vector.</summary>
    public static Vector3d Zero => default;

    /// <summary>The unit vector along X.</summary>
    public static Vector3d XAxis => new(1, 0, 0);

    /// <summary>The unit vector along Y.</summary>
    public static Vector3d YAxis => new(0, 1, 0);

    /// <summary>The unit vector along Z.</summary>
    public static Vector3d ZAxis => new(0, 0, 1);

    /// <summary>
    /// A vector whose components are all <see cref="double.NaN"/>, used to signal "no value".
    /// </summary>
    /// <remarks>
    /// Test for it with <see cref="VectorOps.IsValid"/> rather than with <c>==</c>.
    /// </remarks>
    public static Vector3d Unset => new(double.NaN, double.NaN, double.NaN);

    /// <summary>Splits the vector into its components.</summary>
    public void Deconstruct(out double x, out double y, out double z)
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
        if (double.IsNaN(X) && double.IsNaN(Y) && double.IsNaN(Z))
        {
            return "Vector(unset)";
        }

        format ??= "0.###";
        formatProvider ??= CultureInfo.InvariantCulture;

        return string.Concat(
            "Vector(",
            X.ToString(format, formatProvider),
            ", ",
            Y.ToString(format, formatProvider),
            ", ",
            Z.ToString(format, formatProvider),
            ")");
    }

    /// <summary>
    /// Compares components for exact equality, treating NaN as equal to NaN so that unset vectors
    /// work as dictionary keys.
    /// </summary>
    /// <remarks>
    /// This deliberately differs from <c>==</c>, which follows IEEE rules. Use
    /// <see cref="VectorOps.EpsilonEquals"/> for anything that has been through floating-point
    /// arithmetic.
    /// </remarks>
    public bool Equals(Vector3d other) =>
        X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Vector3d other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    /// <summary>Adds two vectors.</summary>
    public static Vector3d operator +(Vector3d a, Vector3d b) =>
        new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>Subtracts two vectors.</summary>
    public static Vector3d operator -(Vector3d a, Vector3d b) =>
        new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>Has no meaning: a direction plus a position. Declared only so the compiler can say so.</summary>
    /// <remarks>
    /// Forbidden rather than absent, and the distinction matters: with the implicit conversions between
    /// points and vectors, an absent overload would not fail — the compiler would quietly convert the point
    /// and add two vectors, or refuse with a message about ambiguity that says nothing about geometry.
    /// Declaring the overload and poisoning it makes <c>vector + point</c> a compile error in the caller's
    /// own vocabulary: write <c>point + vector</c> to translate a position.
    /// </remarks>
    [Obsolete("A direction plus a position has no meaning. Write point + vector to translate a position.", error: true)]
    public static Vector3d operator +(Vector3d vector, Point3d point) =>
        throw new NotSupportedException();

    /// <summary>Has no meaning: a direction minus a position. Declared only so the compiler can say so.</summary>
    /// <remarks>See the twin above: forbidden rather than absent, so the error speaks geometry.</remarks>
    [Obsolete("A direction minus a position has no meaning. Subtract two points for a displacement, or two vectors for a difference.", error: true)]
    public static Vector3d operator -(Vector3d vector, Point3d point) =>
        throw new NotSupportedException();

    /// <summary>Reverses the vector.</summary>
    public static Vector3d operator -(Vector3d vector) => new(-vector.X, -vector.Y, -vector.Z);

    /// <summary>Scales the vector.</summary>
    public static Vector3d operator *(Vector3d vector, double factor) =>
        new(vector.X * factor, vector.Y * factor, vector.Z * factor);

    /// <summary>Scales the vector.</summary>
    public static Vector3d operator *(double factor, Vector3d vector) => vector * factor;

    /// <summary>Divides the vector.</summary>
    public static Vector3d operator /(Vector3d vector, double divisor) =>
        new(vector.X / divisor, vector.Y / divisor, vector.Z / divisor);

    /// <summary>
    /// Compares components using IEEE rules, so any comparison involving NaN is
    /// <see langword="false"/>.
    /// </summary>
    public static bool operator ==(Vector3d a, Vector3d b) =>
        a.X == b.X && a.Y == b.Y && a.Z == b.Z;

    /// <summary>The negation of <c>==</c>.</summary>
    public static bool operator !=(Vector3d a, Vector3d b) => !(a == b);

    /// <summary>Reinterprets a position as a vector from the origin.</summary>
    public static implicit operator Vector3d(Point3d point) =>
        new(point.X, point.Y, point.Z);

    /// <summary>Creates a vector from a tuple of components.</summary>
    public static implicit operator Vector3d((double X, double Y, double Z) components) =>
        new(components.X, components.Y, components.Z);
}
