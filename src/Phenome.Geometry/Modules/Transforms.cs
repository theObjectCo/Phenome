using System.Diagnostics.CodeAnalysis;

namespace Phenome.Geometry.Modules;

/// <summary>
/// Everything you can do with a <see cref="TMatrix"/>: building them, inspecting them, inverting them.
/// </summary>
/// <remarks>
/// Applying a matrix to geometry lives with the geometry, not here — see
/// <see cref="PointOps.Transform"/>, <see cref="VectorOps.Transform"/>,
/// <see cref="LineOps.Transform"/> and <see cref="PlaneOps.Transform"/>. That keeps this
/// module about matrices rather than about every type in the library.
/// <para>
/// Remember that composition applies right to left, so
/// <c>Translate(c) * Rotate(axis, angle) * Translate(-c)</c> means "move to the origin, rotate, move
/// back". The <c>center</c> overloads below do exactly that for you.
/// </para>
/// </remarks>
public static class Transforms
{
    /// <summary>A matrix from all sixteen entries, given row by row.</summary>
    public static TMatrix Create(
        double m11, double m12, double m13, double m14,
        double m21, double m22, double m23, double m24,
        double m31, double m32, double m33, double m34,
        double m41, double m42, double m43, double m44) =>
        new(
            m11, m12, m13, m14,
            m21, m22, m23, m24,
            m31, m32, m33, m34,
            m41, m42, m43, m44);

    /// <summary><see langword="true"/> when every entry is a finite number.</summary>
    public static bool IsValid(in TMatrix matrix) =>
        double.IsFinite(matrix.M11) && double.IsFinite(matrix.M12) &&
        double.IsFinite(matrix.M13) && double.IsFinite(matrix.M14) &&
        double.IsFinite(matrix.M21) && double.IsFinite(matrix.M22) &&
        double.IsFinite(matrix.M23) && double.IsFinite(matrix.M24) &&
        double.IsFinite(matrix.M31) && double.IsFinite(matrix.M32) &&
        double.IsFinite(matrix.M33) && double.IsFinite(matrix.M34) &&
        double.IsFinite(matrix.M41) && double.IsFinite(matrix.M42) &&
        double.IsFinite(matrix.M43) && double.IsFinite(matrix.M44);

    /// <summary>
    /// <see langword="true"/> when the bottom row is (0, 0, 0, 1), so the matrix maps points to points
    /// without a perspective divide.
    /// </summary>
    /// <param name="matrix">The matrix to test.</param>
    /// <param name="tolerance">How far the bottom row may stray and still count as affine.</param>
    public static bool IsAffine(in TMatrix matrix, double tolerance = Tolerance.Distance) =>
        Math.Abs(matrix.M41) <= tolerance &&
        Math.Abs(matrix.M42) <= tolerance &&
        Math.Abs(matrix.M43) <= tolerance &&
        Math.Abs(matrix.M44 - 1.0) <= tolerance;

    /// <summary><see langword="true"/> when the matrix is the identity to within a tolerance.</summary>
    /// <param name="matrix">The matrix to test.</param>
    /// <param name="tolerance">Largest entry-wise deviation that still counts as the identity.</param>
    public static bool IsIdentity(in TMatrix matrix, double tolerance = Tolerance.Distance) =>
        EpsilonEquals(matrix, TMatrix.Identity, tolerance);

    /// <summary>The translation carried by the last column.</summary>
    /// <remarks>Meaningful for an affine matrix; see <see cref="IsAffine"/>.</remarks>
    public static Vector3d GetTranslation(in TMatrix matrix) =>
        new(matrix.M14, matrix.M24, matrix.M34);

    /// <summary>A matrix that moves geometry by <paramref name="translation"/>.</summary>
    public static TMatrix Translate(Vector3d translation) =>
        Translate(translation.X, translation.Y, translation.Z);

    /// <summary>A matrix that moves geometry by the given offsets.</summary>
    public static TMatrix Translate(double x, double y, double z) => new(
        1, 0, 0, x,
        0, 1, 0, y,
        0, 0, 1, z,
        0, 0, 0, 1);

    /// <summary>A matrix that scales uniformly about the origin.</summary>
    public static TMatrix Scale(double factor) => Scale(factor, factor, factor);

    /// <summary>A matrix that scales each axis independently about the origin.</summary>
    public static TMatrix Scale(double x, double y, double z) => new(
        x, 0, 0, 0,
        0, y, 0, 0,
        0, 0, z, 0,
        0, 0, 0, 1);

    /// <summary>A matrix that scales uniformly about <paramref name="center"/>.</summary>
    public static TMatrix Scale(Point3d center, double factor) =>
        Translate(center) * Scale(factor) * Translate(-(Vector3d)center);

    /// <summary>A matrix that reflects across a plane.</summary>
    /// <remarks>
    /// Only the plane's origin and normal matter; the in-plane axes make no difference to a reflection.
    /// <para>
    /// A reflection reverses handedness, so a mesh put through it comes out inside-out: its faces still
    /// occupy the right places but wind the other way, and its normals point inwards. Follow with
    /// <see cref="MeshOps.Flip"/>. Nothing here can do that for you, because a matrix does not know
    /// whether it is about to be applied to a solid, a curve or a single point — and for the latter two the
    /// reflection is the whole answer.
    /// </para>
    /// </remarks>
    /// <param name="plane">The mirror.</param>
    /// <exception cref="ArgumentException"><paramref name="plane"/> is invalid.</exception>
    public static TMatrix Mirror(in Plane plane)
    {
        if (!PlaneOps.IsValid(plane))
        {
            throw new ArgumentException("Cannot mirror across an invalid plane.", nameof(plane));
        }

        return Mirror(plane.Origin, plane.Normal);
    }

    /// <summary>A matrix that reflects across the plane through a point with the given normal.</summary>
    /// <remarks>See <see cref="Mirror(in Plane)"/> for what a reflection does to a mesh's winding.</remarks>
    /// <param name="origin">A point the mirror passes through.</param>
    /// <param name="normal">The direction perpendicular to the mirror; need not be normalised.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="normal"/> is degenerate, or <paramref name="origin"/> is not finite.
    /// </exception>
    public static TMatrix Mirror(Point3d origin, Vector3d normal)
    {
        if (!PointOps.IsValid(origin))
        {
            throw new ArgumentException("The mirror's origin is not a finite point.", nameof(origin));
        }

        if (!VectorOps.TryNormalize(normal, out Vector3d? unit))
        {
            throw new ArgumentException(
                $"Cannot mirror across a plane with normal {normal}: it is degenerate or invalid.",
                nameof(normal));
        }

        double nx = unit.Value.X;
        double ny = unit.Value.Y;
        double nz = unit.Value.Z;

        // A point moves by twice its signed distance from the plane, against the normal. Written out as a
        // matrix that is the identity less twice the outer product of the normal with itself, plus a
        // translation carrying the plane off the origin.
        double offset = 2 * VectorOps.Dot((Vector3d)origin, unit.Value);

        return new TMatrix(
            1 - (2 * nx * nx), -2 * nx * ny, -2 * nx * nz, offset * nx,
            -2 * ny * nx, 1 - (2 * ny * ny), -2 * ny * nz, offset * ny,
            -2 * nz * nx, -2 * nz * ny, 1 - (2 * nz * nz), offset * nz,
            0, 0, 0, 1);
    }

    /// <summary>
    /// A matrix that rotates by <paramref name="angle"/> radians about an axis through the origin.
    /// </summary>
    /// <param name="axis">The axis of rotation; need not be normalised.</param>
    /// <param name="angle">The angle in radians, counter-clockwise looking down the axis.</param>
    /// <exception cref="ArgumentException"><paramref name="axis"/> is degenerate or invalid.</exception>
    public static TMatrix Rotate(Vector3d axis, double angle)
    {
        if (!VectorOps.TryNormalize(axis, out Vector3d? unit))
        {
            throw new ArgumentException(
                $"Cannot rotate about {axis}: the axis is degenerate or invalid.",
                nameof(axis));
        }

        double sin = Math.Sin(angle);
        double cos = Math.Cos(angle);
        double t = 1.0 - cos;

        double x = unit.Value.X;
        double y = unit.Value.Y;
        double z = unit.Value.Z;

        return new TMatrix(
            cos + (x * x * t), (x * y * t) - (z * sin), (x * z * t) + (y * sin), 0,
            (y * x * t) + (z * sin), cos + (y * y * t), (y * z * t) - (x * sin), 0,
            (z * x * t) - (y * sin), (z * y * t) + (x * sin), cos + (z * z * t), 0,
            0, 0, 0, 1);
    }

    /// <summary>
    /// A matrix that rotates by <paramref name="angle"/> radians about an axis through
    /// <paramref name="center"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="axis"/> is degenerate or invalid.</exception>
    public static TMatrix Rotate(Point3d center, Vector3d axis, double angle) =>
        Translate(center) * Rotate(axis, angle) * Translate(-(Vector3d)center);

    /// <summary>
    /// The shortest rotation about the origin that takes <paramref name="from"/> onto
    /// <paramref name="to"/>.
    /// </summary>
    /// <remarks>
    /// Handles the awkward cases explicitly: already aligned gives the identity, and exactly opposed
    /// gives a half turn about an arbitrary perpendicular, since no shortest arc is defined there.
    /// </remarks>
    /// <exception cref="ArgumentException">Either direction is degenerate or invalid.</exception>
    public static TMatrix Rotate(Vector3d from, Vector3d to)
    {
        if (!TryRotate(from, to, out TMatrix? rotation))
        {
            throw new ArgumentException(
                $"Cannot rotate {from} onto {to}: at least one direction is degenerate or invalid.");
        }

        return rotation.Value;
    }

    /// <summary>
    /// The shortest rotation about the origin taking one direction onto another, reporting failure
    /// instead of throwing.
    /// </summary>
    /// <param name="from">The direction to rotate away from.</param>
    /// <param name="to">The direction to rotate onto.</param>
    /// <param name="rotation">The rotation, or <see langword="null"/> when the call fails.</param>
    /// <returns><see langword="false"/> when either direction is degenerate or invalid.</returns>
    public static bool TryRotate(
        Vector3d from,
        Vector3d to,
        [NotNullWhen(true)] out TMatrix? rotation)
    {
        if (!VectorOps.TryNormalize(from, out Vector3d? normalizedFrom) ||
            !VectorOps.TryNormalize(to, out Vector3d? normalizedTo))
        {
            rotation = null;
            return false;
        }

        Vector3d start = normalizedFrom.Value;
        Vector3d end = normalizedTo.Value;

        if (VectorOps.TryNormalize(VectorOps.Cross(start, end), out Vector3d? unitAxis))
        {
            rotation = Rotate(unitAxis.Value, VectorOps.AngleBetween(start, end));
            return true;
        }

        // The cross product vanishes when the directions are parallel: either identical, or opposed.
        if (VectorOps.Dot(start, end) > 0)
        {
            rotation = TMatrix.Identity;
            return true;
        }

        rotation = Rotate(VectorOps.PerpendicularTo(start), Math.PI);
        return true;
    }

    /// <summary>
    /// A matrix that maps the world basis onto the frame described by the given axes and origin.
    /// </summary>
    /// <remarks>
    /// The axes become the first three columns and the origin the last, so transforming
    /// <see cref="Vector3d.XAxis"/> hands back <paramref name="xAxis"/> and transforming
    /// <see cref="Point3d.Origin"/> hands back <paramref name="origin"/>.
    /// <para>
    /// The axes are used as given: no orthogonalisation, no normalisation. Pass a non-orthonormal set and
    /// you get a shear or a scale, which is occasionally what you want and otherwise a bug in the caller.
    /// </para>
    /// </remarks>
    public static TMatrix FrameToWorld(Vector3d xAxis, Vector3d yAxis, Vector3d zAxis, Point3d origin) =>
        new(
            xAxis.X, yAxis.X, zAxis.X, origin.X,
            xAxis.Y, yAxis.Y, zAxis.Y, origin.Y,
            xAxis.Z, yAxis.Z, zAxis.Z, origin.Z,
            0, 0, 0, 1);

    /// <summary>
    /// A matrix that maps the world basis onto a plane's frame, taking the world origin to the plane
    /// origin and the world axes to the plane axes.
    /// </summary>
    public static TMatrix PlaneToWorld(in Plane plane) =>
        FrameToWorld(plane.XAxis, plane.YAxis, plane.ZAxis, plane.Origin);

    /// <summary>
    /// A matrix that expresses world coordinates in a plane's frame, the exact inverse of
    /// <see cref="PlaneToWorld"/>.
    /// </summary>
    /// <remarks>
    /// Built analytically rather than by inverting: for an orthonormal frame the inverse rotation is
    /// simply the transpose, so each row is one axis and the translation is the negated projection of the
    /// origin onto it. That cannot fail and introduces no rounding, unlike a general inverse.
    /// Transforming a point through this yields its frame coordinates directly.
    /// </remarks>
    public static TMatrix WorldToPlane(in Plane plane)
    {
        Vector3d x = plane.XAxis;
        Vector3d y = plane.YAxis;
        Vector3d z = plane.ZAxis;
        Vector3d origin = plane.Origin;

        return new TMatrix(
            x.X, x.Y, x.Z, -VectorOps.Dot(x, origin),
            y.X, y.Y, y.Z, -VectorOps.Dot(y, origin),
            z.X, z.Y, z.Z, -VectorOps.Dot(z, origin),
            0, 0, 0, 1);
    }

    /// <summary>A matrix that carries geometry from one plane's frame into another's.</summary>
    /// <remarks>
    /// Reads right to left as "express it in the source frame, then plant it in the target frame". This
    /// replaces the previous hand-rolled version, which composed three rotations derived from unsigned
    /// angles and produced a NaN matrix whenever the source normal was opposite world Z.
    /// </remarks>
    public static TMatrix PlaneToPlane(in Plane from, in Plane to) =>
        PlaneToWorld(to) * WorldToPlane(from);

    /// <summary>The matrix with rows and columns exchanged.</summary>
    public static TMatrix Transposed(in TMatrix matrix) => new(
        matrix.M11, matrix.M21, matrix.M31, matrix.M41,
        matrix.M12, matrix.M22, matrix.M32, matrix.M42,
        matrix.M13, matrix.M23, matrix.M33, matrix.M43,
        matrix.M14, matrix.M24, matrix.M34, matrix.M44);

    /// <summary>The determinant of the matrix.</summary>
    /// <remarks>Zero means the transform collapses space and cannot be undone.</remarks>
    public static double Determinant(in TMatrix m)
    {
        double kp_lo = (m.M33 * m.M44) - (m.M34 * m.M43);
        double jp_ln = (m.M32 * m.M44) - (m.M34 * m.M42);
        double jo_kn = (m.M32 * m.M43) - (m.M33 * m.M42);
        double ip_lm = (m.M31 * m.M44) - (m.M34 * m.M41);
        double io_km = (m.M31 * m.M43) - (m.M33 * m.M41);
        double in_jm = (m.M31 * m.M42) - (m.M32 * m.M41);

        return (m.M11 * ((m.M22 * kp_lo) - (m.M23 * jp_ln) + (m.M24 * jo_kn)))
             - (m.M12 * ((m.M21 * kp_lo) - (m.M23 * ip_lm) + (m.M24 * io_km)))
             + (m.M13 * ((m.M21 * jp_ln) - (m.M22 * ip_lm) + (m.M24 * in_jm)))
             - (m.M14 * ((m.M21 * jo_kn) - (m.M22 * io_km) + (m.M23 * in_jm)));
    }

    /// <summary>The inverse of the matrix.</summary>
    /// <exception cref="InvalidOperationException">
    /// The matrix is singular or invalid, so it has no inverse. Use <see cref="TryInvert"/> when that is
    /// expected.
    /// </exception>
    public static TMatrix Inverted(in TMatrix matrix)
    {
        if (!TryInvert(matrix, out TMatrix? inverse))
        {
            throw new InvalidOperationException("Cannot invert the matrix: it is singular or invalid.");
        }

        return inverse.Value;
    }

    /// <summary>
    /// The inverse of the matrix, reporting failure instead of producing entries full of infinities.
    /// </summary>
    /// <param name="matrix">The matrix to invert.</param>
    /// <param name="inverse">
    /// The inverse, or <see langword="null"/> when the call fails. Deliberately not a usable fallback
    /// such as the identity: that would silently leave geometry untransformed, which reads as success and
    /// produces a plausible but wrong model. A <see langword="null"/> cannot be mistaken for a result.
    /// </param>
    /// <returns><see langword="false"/> when the matrix is singular or invalid.</returns>
    public static bool TryInvert(in TMatrix matrix, [NotNullWhen(true)] out TMatrix? inverse)
    {
        double a = matrix.M11;
        double b = matrix.M12;
        double c = matrix.M13;
        double d = matrix.M14;
        double e = matrix.M21;
        double f = matrix.M22;
        double g = matrix.M23;
        double h = matrix.M24;
        double i = matrix.M31;
        double j = matrix.M32;
        double k = matrix.M33;
        double l = matrix.M34;
        double m = matrix.M41;
        double n = matrix.M42;
        double o = matrix.M43;
        double p = matrix.M44;

        double kp_lo = (k * p) - (l * o);
        double jp_ln = (j * p) - (l * n);
        double jo_kn = (j * o) - (k * n);
        double ip_lm = (i * p) - (l * m);
        double io_km = (i * o) - (k * m);
        double in_jm = (i * n) - (j * m);

        double a11 = (f * kp_lo) - (g * jp_ln) + (h * jo_kn);
        double a12 = -((e * kp_lo) - (g * ip_lm) + (h * io_km));
        double a13 = (e * jp_ln) - (f * ip_lm) + (h * in_jm);
        double a14 = -((e * jo_kn) - (f * io_km) + (g * in_jm));

        double determinant = (a * a11) + (b * a12) + (c * a13) + (d * a14);

        if (!IsValid(matrix) || determinant == 0.0 || !double.IsFinite(determinant))
        {
            inverse = null;
            return false;
        }

        double invDet = 1.0 / determinant;

        double gp_ho = (g * p) - (h * o);
        double fp_hn = (f * p) - (h * n);
        double fo_gn = (f * o) - (g * n);
        double ep_hm = (e * p) - (h * m);
        double eo_gm = (e * o) - (g * m);
        double en_fm = (e * n) - (f * m);

        double gl_hk = (g * l) - (h * k);
        double fl_hj = (f * l) - (h * j);
        double fk_gj = (f * k) - (g * j);
        double el_hi = (e * l) - (h * i);
        double ek_gi = (e * k) - (g * i);
        double ej_fi = (e * j) - (f * i);

        TMatrix result = new(
            a11 * invDet,
            -((b * kp_lo) - (c * jp_ln) + (d * jo_kn)) * invDet,
            ((b * gp_ho) - (c * fp_hn) + (d * fo_gn)) * invDet,
            -((b * gl_hk) - (c * fl_hj) + (d * fk_gj)) * invDet,

            a12 * invDet,
            ((a * kp_lo) - (c * ip_lm) + (d * io_km)) * invDet,
            -((a * gp_ho) - (c * ep_hm) + (d * eo_gm)) * invDet,
            ((a * gl_hk) - (c * el_hi) + (d * ek_gi)) * invDet,

            a13 * invDet,
            -((a * jp_ln) - (b * ip_lm) + (d * in_jm)) * invDet,
            ((a * fp_hn) - (b * ep_hm) + (d * en_fm)) * invDet,
            -((a * fl_hj) - (b * el_hi) + (d * ej_fi)) * invDet,

            a14 * invDet,
            ((a * jo_kn) - (b * io_km) + (c * in_jm)) * invDet,
            -((a * fo_gn) - (b * eo_gm) + (c * en_fm)) * invDet,
            ((a * fk_gj) - (b * ek_gi) + (c * ej_fi)) * invDet);

        // A finite determinant does not guarantee finite cofactors: an entry can still overflow.
        if (!IsValid(result))
        {
            inverse = null;
            return false;
        }

        inverse = result;
        return true;
    }

    /// <summary>Writes the entries out in row-major order.</summary>
    public static double[] ToRowMajor(in TMatrix m) =>
    [
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44,
    ];

    /// <summary>Reads a matrix from sixteen values in row-major order.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="values"/> does not hold exactly sixteen values.
    /// </exception>
    public static TMatrix CreateFromRowMajor(ReadOnlySpan<double> values)
    {
        if (values.Length != 16)
        {
            throw new ArgumentException(
                $"Expected exactly 16 values in row-major order, but received {values.Length}.",
                nameof(values));
        }

        return new TMatrix(
            values[0], values[1], values[2], values[3],
            values[4], values[5], values[6], values[7],
            values[8], values[9], values[10], values[11],
            values[12], values[13], values[14], values[15]);
    }

    /// <summary>
    /// <see langword="true"/> when no entry differs from the other matrix by more than
    /// <paramref name="tolerance"/>.
    /// </summary>
    public static bool EpsilonEquals(in TMatrix a, in TMatrix b, double tolerance = Tolerance.Distance) =>
        Math.Abs(a.M11 - b.M11) <= tolerance && Math.Abs(a.M12 - b.M12) <= tolerance &&
        Math.Abs(a.M13 - b.M13) <= tolerance && Math.Abs(a.M14 - b.M14) <= tolerance &&
        Math.Abs(a.M21 - b.M21) <= tolerance && Math.Abs(a.M22 - b.M22) <= tolerance &&
        Math.Abs(a.M23 - b.M23) <= tolerance && Math.Abs(a.M24 - b.M24) <= tolerance &&
        Math.Abs(a.M31 - b.M31) <= tolerance && Math.Abs(a.M32 - b.M32) <= tolerance &&
        Math.Abs(a.M33 - b.M33) <= tolerance && Math.Abs(a.M34 - b.M34) <= tolerance &&
        Math.Abs(a.M41 - b.M41) <= tolerance && Math.Abs(a.M42 - b.M42) <= tolerance &&
        Math.Abs(a.M43 - b.M43) <= tolerance && Math.Abs(a.M44 - b.M44) <= tolerance;
}
