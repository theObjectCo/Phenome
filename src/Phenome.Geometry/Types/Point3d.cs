using System.Globalization;

namespace Phenome.Geometry.Types;

/// <summary>
/// An immutable position in three-dimensional space.
/// </summary>
/// <remarks>
/// A container for three coordinates and nothing else. Everything that computes from them — distance,
/// validity, interpolation, transformation — lives in <see cref="PointOps"/>.
/// <para>
/// There is no public constructor; <see cref="PointOps.Create"/> is the way in. Keeping
/// construction in the module gives every operation in the library the same shape: a plain static
/// function with declared inputs and outputs, which is exactly what a visual node maps onto.
/// </para>
/// <para>
/// Operators stay here because C# requires them to be declared in one of the operand types, and
/// <c>b - a</c> reads better than any function call could.
/// </para>
/// </remarks>
public readonly struct Point3d : IEquatable<Point3d>, IFormattable
{
    internal Point3d(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>The X coordinate.</summary>
    public double X { get; }

    /// <summary>The Y coordinate.</summary>
    public double Y { get; }

    /// <summary>The Z coordinate.</summary>
    public double Z { get; }

    /// <summary>The point at (0, 0, 0).</summary>
    public static Point3d Origin => default;

    /// <summary>
    /// A point whose coordinates are all <see cref="double.NaN"/>, used to signal "no value".
    /// </summary>
    /// <remarks>
    /// Test for it with <see cref="PointOps.IsValid"/>, not with <c>==</c>: by IEEE rules
    /// <c>Unset == Unset</c> is <see langword="false"/>, because NaN never equals itself.
    /// </remarks>
    public static Point3d Unset => new(double.NaN, double.NaN, double.NaN);

    /// <summary>Splits the point into its coordinates.</summary>
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
            return "Point(unset)";
        }

        format ??= "0.###";
        formatProvider ??= CultureInfo.InvariantCulture;

        return string.Concat(
            "Point(",
            X.ToString(format, formatProvider),
            ", ",
            Y.ToString(format, formatProvider),
            ", ",
            Z.ToString(format, formatProvider),
            ")");
    }

    /// <summary>
    /// Compares coordinates for exact equality, treating NaN as equal to NaN so that unset points
    /// work as dictionary keys.
    /// </summary>
    /// <remarks>
    /// This deliberately differs from <c>==</c>, which follows IEEE rules. Use
    /// <see cref="PointOps.EpsilonEquals"/> for anything that has been through floating-point
    /// arithmetic.
    /// </remarks>
    public bool Equals(Point3d other) =>
        X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Point3d other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    /// <summary>Adds two points coordinate-wise.</summary>
    /// <remarks>
    /// In a mixed expression the left operand decides the result type: a point on the left yields a
    /// point, a vector on the left yields a vector, and the other operand is read through the implicit
    /// conversion. The one exception is <c>point - point</c>, which yields the displacement between two
    /// positions and so is a vector.
    /// <para>
    /// All eight combinations are declared explicitly. Because the two types convert implicitly to one
    /// another, any combination left out would be ambiguous rather than simply absent, and would fail
    /// to compile with a diagnostic that says nothing about geometry.
    /// </para>
    /// </remarks>
    public static Point3d operator +(Point3d a, Point3d b) =>
        new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>Translates a point by a vector, giving the translated position.</summary>
    public static Point3d operator +(Point3d point, Vector3d vector) =>
        new(point.X + vector.X, point.Y + vector.Y, point.Z + vector.Z);

    /// <summary>Returns the vector leading from <paramref name="b"/> to <paramref name="a"/>.</summary>
    public static Vector3d operator -(Point3d a, Point3d b) =>
        new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>Translates a point by the reverse of a vector.</summary>
    public static Point3d operator -(Point3d point, Vector3d vector) =>
        new(point.X - vector.X, point.Y - vector.Y, point.Z - vector.Z);

    /// <summary>Negates every coordinate.</summary>
    public static Point3d operator -(Point3d point) => new(-point.X, -point.Y, -point.Z);

    /// <summary>Scales every coordinate.</summary>
    public static Point3d operator *(Point3d point, double factor) =>
        new(point.X * factor, point.Y * factor, point.Z * factor);

    /// <summary>Scales every coordinate.</summary>
    public static Point3d operator *(double factor, Point3d point) => point * factor;

    /// <summary>Divides every coordinate.</summary>
    public static Point3d operator /(Point3d point, double divisor) =>
        new(point.X / divisor, point.Y / divisor, point.Z / divisor);

    /// <summary>
    /// Compares coordinates using IEEE rules, so any comparison involving NaN is
    /// <see langword="false"/>.
    /// </summary>
    public static bool operator ==(Point3d a, Point3d b) =>
        a.X == b.X && a.Y == b.Y && a.Z == b.Z;

    /// <summary>The negation of <c>==</c>.</summary>
    public static bool operator !=(Point3d a, Point3d b) => !(a == b);

    /// <summary>Reinterprets a vector as a position relative to the origin.</summary>
    public static implicit operator Point3d(Vector3d vector) =>
        new(vector.X, vector.Y, vector.Z);

    /// <summary>Creates a point from a tuple of coordinates.</summary>
    public static implicit operator Point3d((double X, double Y, double Z) coordinates) =>
        new(coordinates.X, coordinates.Y, coordinates.Z);
}
