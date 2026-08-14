using System.Globalization;

namespace Phenome.Geometry.Types;

/// <summary>
/// An immutable 4x4 transformation matrix.
/// </summary>
/// <remarks>
/// A container for sixteen entries and nothing else. Building matrices, inverting them, taking a
/// determinant or a transpose, and applying them to geometry all live in <see cref="Transforms"/>.
/// <para>
/// There is no public constructor; <see cref="Transforms.Create"/> is the way in, and the named
/// factories there cover every case that actually comes up.
/// </para>
/// <para>
/// Fields are named <c>M</c><em>row</em><em>column</em>, so <see cref="M23"/> is row 2, column 3. The
/// matrix acts on column vectors — <c>p' = M * p</c> — which puts translation in the last column
/// (<see cref="M14"/>, <see cref="M24"/>, <see cref="M34"/>) and makes the bottom row (0, 0, 0, 1) for
/// an affine transform. Composition follows the same convention: in <c>a * b</c> the right-hand matrix
/// applies first.
/// </para>
/// <para>
/// Because the struct is 128 bytes, every function that takes one does so by <c>in</c> reference. That
/// costs nothing here precisely because this is a <c>readonly struct</c>, so the compiler emits no
/// defensive copies on member access.
/// </para>
/// </remarks>
public readonly struct TMatrix : IEquatable<TMatrix>, IFormattable
{
    internal TMatrix(
        double m11, double m12, double m13, double m14,
        double m21, double m22, double m23, double m24,
        double m31, double m32, double m33, double m34,
        double m41, double m42, double m43, double m44)
    {
        M11 = m11;
        M12 = m12;
        M13 = m13;
        M14 = m14;
        M21 = m21;
        M22 = m22;
        M23 = m23;
        M24 = m24;
        M31 = m31;
        M32 = m32;
        M33 = m33;
        M34 = m34;
        M41 = m41;
        M42 = m42;
        M43 = m43;
        M44 = m44;
    }

    /// <summary>Row 1, column 1.</summary>
    public readonly double M11;

    /// <summary>Row 1, column 2.</summary>
    public readonly double M12;

    /// <summary>Row 1, column 3.</summary>
    public readonly double M13;

    /// <summary>Row 1, column 4. The X component of the translation, for an affine matrix.</summary>
    public readonly double M14;

    /// <summary>Row 2, column 1.</summary>
    public readonly double M21;

    /// <summary>Row 2, column 2.</summary>
    public readonly double M22;

    /// <summary>Row 2, column 3.</summary>
    public readonly double M23;

    /// <summary>Row 2, column 4. The Y component of the translation, for an affine matrix.</summary>
    public readonly double M24;

    /// <summary>Row 3, column 1.</summary>
    public readonly double M31;

    /// <summary>Row 3, column 2.</summary>
    public readonly double M32;

    /// <summary>Row 3, column 3.</summary>
    public readonly double M33;

    /// <summary>Row 3, column 4. The Z component of the translation, for an affine matrix.</summary>
    public readonly double M34;

    /// <summary>Row 4, column 1. Zero for an affine matrix.</summary>
    public readonly double M41;

    /// <summary>Row 4, column 2. Zero for an affine matrix.</summary>
    public readonly double M42;

    /// <summary>Row 4, column 3. Zero for an affine matrix.</summary>
    public readonly double M43;

    /// <summary>Row 4, column 4. One for an affine matrix.</summary>
    public readonly double M44;

    /// <summary>The identity matrix, which leaves geometry unchanged.</summary>
    public static TMatrix Identity => new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1);

    /// <summary>The matrix with every entry zero. Singular, and not a usable transform.</summary>
    public static TMatrix Zero => default;

    /// <summary>
    /// A matrix whose entries are all <see cref="double.NaN"/>, used to signal "no value".
    /// </summary>
    /// <remarks>
    /// Test for it with <see cref="Transforms.IsValid"/> rather than with <c>==</c>. Note that the
    /// <c>Try…</c> functions report failure through a <see langword="null"/> <c>out</c> parameter, not
    /// through this value.
    /// </remarks>
    public static TMatrix Unset => new(
        double.NaN, double.NaN, double.NaN, double.NaN,
        double.NaN, double.NaN, double.NaN, double.NaN,
        double.NaN, double.NaN, double.NaN, double.NaN,
        double.NaN, double.NaN, double.NaN, double.NaN);

    /// <summary>Reads one entry by row and column, both one-based to match the field names.</summary>
    /// <remarks>
    /// This selects a stored field rather than computing anything, which is why it stays on the type.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="row"/> or <paramref name="column"/> lies outside 1..4.
    /// </exception>
    public double this[int row, int column] => (row, column) switch
    {
        (1, 1) => M11,
        (1, 2) => M12,
        (1, 3) => M13,
        (1, 4) => M14,
        (2, 1) => M21,
        (2, 2) => M22,
        (2, 3) => M23,
        (2, 4) => M24,
        (3, 1) => M31,
        (3, 2) => M32,
        (3, 3) => M33,
        (3, 4) => M34,
        (4, 1) => M41,
        (4, 2) => M42,
        (4, 3) => M43,
        (4, 4) => M44,
        _ => throw new ArgumentOutOfRangeException(
            row is < 1 or > 4 ? nameof(row) : nameof(column),
            row is < 1 or > 4 ? row : column,
            "Row and column indices are one-based and must lie in 1..4."),
    };

    /// <inheritdoc/>
    public override string ToString() => ToString(null, null);

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        format ??= "0.###";
        formatProvider ??= CultureInfo.InvariantCulture;

        string Row(double a, double b, double c, double d) => string.Concat(
            "[",
            a.ToString(format, formatProvider),
            " ",
            b.ToString(format, formatProvider),
            " ",
            c.ToString(format, formatProvider),
            " ",
            d.ToString(format, formatProvider),
            "]");

        return string.Join(
            Environment.NewLine,
            "TMatrix(",
            "  " + Row(M11, M12, M13, M14),
            "  " + Row(M21, M22, M23, M24),
            "  " + Row(M31, M32, M33, M34),
            "  " + Row(M41, M42, M43, M44),
            ")");
    }

    /// <summary>Compares entries for exact equality, treating NaN as equal to NaN.</summary>
    /// <remarks>
    /// Use <see cref="Transforms.EpsilonEquals"/> for matrices that have been through arithmetic.
    /// </remarks>
    public bool Equals(TMatrix other) =>
        M11.Equals(other.M11) && M12.Equals(other.M12) && M13.Equals(other.M13) && M14.Equals(other.M14) &&
        M21.Equals(other.M21) && M22.Equals(other.M22) && M23.Equals(other.M23) && M24.Equals(other.M24) &&
        M31.Equals(other.M31) && M32.Equals(other.M32) && M33.Equals(other.M33) && M34.Equals(other.M34) &&
        M41.Equals(other.M41) && M42.Equals(other.M42) && M43.Equals(other.M43) && M44.Equals(other.M44);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TMatrix other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;

        hash.Add(M11);
        hash.Add(M12);
        hash.Add(M13);
        hash.Add(M14);
        hash.Add(M21);
        hash.Add(M22);
        hash.Add(M23);
        hash.Add(M24);
        hash.Add(M31);
        hash.Add(M32);
        hash.Add(M33);
        hash.Add(M34);
        hash.Add(M41);
        hash.Add(M42);
        hash.Add(M43);
        hash.Add(M44);

        return hash.ToHashCode();
    }

    /// <summary>Composes two transforms. The right-hand matrix applies first.</summary>
    public static TMatrix operator *(in TMatrix a, in TMatrix b) => new(
        (a.M11 * b.M11) + (a.M12 * b.M21) + (a.M13 * b.M31) + (a.M14 * b.M41),
        (a.M11 * b.M12) + (a.M12 * b.M22) + (a.M13 * b.M32) + (a.M14 * b.M42),
        (a.M11 * b.M13) + (a.M12 * b.M23) + (a.M13 * b.M33) + (a.M14 * b.M43),
        (a.M11 * b.M14) + (a.M12 * b.M24) + (a.M13 * b.M34) + (a.M14 * b.M44),

        (a.M21 * b.M11) + (a.M22 * b.M21) + (a.M23 * b.M31) + (a.M24 * b.M41),
        (a.M21 * b.M12) + (a.M22 * b.M22) + (a.M23 * b.M32) + (a.M24 * b.M42),
        (a.M21 * b.M13) + (a.M22 * b.M23) + (a.M23 * b.M33) + (a.M24 * b.M43),
        (a.M21 * b.M14) + (a.M22 * b.M24) + (a.M23 * b.M34) + (a.M24 * b.M44),

        (a.M31 * b.M11) + (a.M32 * b.M21) + (a.M33 * b.M31) + (a.M34 * b.M41),
        (a.M31 * b.M12) + (a.M32 * b.M22) + (a.M33 * b.M32) + (a.M34 * b.M42),
        (a.M31 * b.M13) + (a.M32 * b.M23) + (a.M33 * b.M33) + (a.M34 * b.M43),
        (a.M31 * b.M14) + (a.M32 * b.M24) + (a.M33 * b.M34) + (a.M34 * b.M44),

        (a.M41 * b.M11) + (a.M42 * b.M21) + (a.M43 * b.M31) + (a.M44 * b.M41),
        (a.M41 * b.M12) + (a.M42 * b.M22) + (a.M43 * b.M32) + (a.M44 * b.M42),
        (a.M41 * b.M13) + (a.M42 * b.M23) + (a.M43 * b.M33) + (a.M44 * b.M43),
        (a.M41 * b.M14) + (a.M42 * b.M24) + (a.M43 * b.M34) + (a.M44 * b.M44));

    /// <summary>Compares entries using IEEE rules, so any comparison involving NaN is false.</summary>
    public static bool operator ==(in TMatrix a, in TMatrix b) =>
        a.M11 == b.M11 && a.M12 == b.M12 && a.M13 == b.M13 && a.M14 == b.M14 &&
        a.M21 == b.M21 && a.M22 == b.M22 && a.M23 == b.M23 && a.M24 == b.M24 &&
        a.M31 == b.M31 && a.M32 == b.M32 && a.M33 == b.M33 && a.M34 == b.M34 &&
        a.M41 == b.M41 && a.M42 == b.M42 && a.M43 == b.M43 && a.M44 == b.M44;

    /// <summary>The negation of <c>==</c>.</summary>
    public static bool operator !=(in TMatrix a, in TMatrix b) => !(a == b);
}
