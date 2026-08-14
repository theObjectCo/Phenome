using System.Diagnostics.CodeAnalysis;

namespace Phenome.Geometry.Modules;

/// <summary>
/// Everything you can do with a <see cref="Vector3d"/>.
/// </summary>
/// <remarks>
/// Operations that cannot be defined for a zero-length vector — normalising, measuring an angle,
/// building a perpendicular — come in two flavours: a <c>Try…</c> function that reports failure, and one
/// that throws.
/// <para>
/// The <c>Try…</c> functions follow the library convention: the <see langword="bool"/> return says
/// whether it worked, and the <c>out</c> parameter is nullable — <see langword="null"/> on failure, a
/// value on success. A caller who ignores the <see langword="bool"/> therefore cannot slip a bogus
/// result into the next calculation; unwrapping the <see langword="null"/> fails at the call site, with
/// a stack trace pointing at the cause rather than at some distant symptom.
/// </para>
/// </remarks>
public static class VectorOps
{
    /// <summary>A vector with the given components.</summary>
    public static Vector3d Create(double x, double y, double z) => new(x, y, z);

    /// <summary>
    /// <see langword="true"/> when every component is a finite number, i.e. neither NaN nor infinite.
    /// </summary>
    public static bool IsValid(Vector3d vector) =>
        double.IsFinite(vector.X) && double.IsFinite(vector.Y) && double.IsFinite(vector.Z);

    /// <summary>Squared magnitude.</summary>
    /// <remarks>Prefer this over <see cref="Length"/> when comparing magnitudes.</remarks>
    public static double LengthSquared(Vector3d vector) =>
        (vector.X * vector.X) + (vector.Y * vector.Y) + (vector.Z * vector.Z);

    /// <summary>Magnitude.</summary>
    public static double Length(Vector3d vector) => Math.Sqrt(LengthSquared(vector));

    /// <summary>
    /// <see langword="true"/> when the magnitude does not exceed <paramref name="tolerance"/>.
    /// </summary>
    public static bool IsZero(Vector3d vector, double tolerance = Tolerance.Zero) =>
        LengthSquared(vector) <= tolerance * tolerance;

    /// <summary>
    /// <see langword="true"/> when the magnitude is 1 to within <paramref name="tolerance"/>.
    /// </summary>
    public static bool IsUnit(Vector3d vector, double tolerance = Tolerance.Distance) =>
        Math.Abs(Length(vector) - 1.0) <= tolerance;

    /// <summary>Scales the vector to unit length, reporting failure instead of producing NaN.</summary>
    /// <param name="vector">The vector to scale.</param>
    /// <param name="unit">The unit vector, or <see langword="null"/> when the call fails.</param>
    /// <param name="tolerance">Magnitude at or below which the vector counts as degenerate.</param>
    /// <returns>
    /// <see langword="false"/> when the vector is invalid or too short to have a direction.
    /// </returns>
    public static bool TryNormalize(
        Vector3d vector,
        [NotNullWhen(true)] out Vector3d? unit,
        double tolerance = Tolerance.Zero)
    {
        double lengthSquared = LengthSquared(vector);

        if (!IsValid(vector) || lengthSquared <= tolerance * tolerance)
        {
            unit = null;
            return false;
        }

        double denominator = 1.0 / Math.Sqrt(lengthSquared);
        unit = new Vector3d(vector.X * denominator, vector.Y * denominator, vector.Z * denominator);
        return true;
    }

    /// <summary>The vector scaled to unit length.</summary>
    /// <exception cref="InvalidOperationException">
    /// The vector is invalid or has no direction. Use <see cref="TryNormalize"/> when a degenerate input
    /// is expected.
    /// </exception>
    public static Vector3d Normalized(Vector3d vector)
    {
        if (!TryNormalize(vector, out Vector3d? unit))
        {
            throw new InvalidOperationException(
                $"Cannot normalize {vector}: the vector is degenerate or invalid.");
        }

        return unit.Value;
    }

    /// <summary>The vector with every component reversed.</summary>
    public static Vector3d Reversed(Vector3d vector) => new(-vector.X, -vector.Y, -vector.Z);

    /// <summary>Dot product.</summary>
    public static double Dot(Vector3d a, Vector3d b) =>
        (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    /// <summary>Cross product, following the right-hand rule.</summary>
    public static Vector3d Cross(Vector3d a, Vector3d b) =>
        new(
            (a.Y * b.Z) - (a.Z * b.Y),
            (a.Z * b.X) - (a.X * b.Z),
            (a.X * b.Y) - (a.Y * b.X));

    /// <summary>The unsigned angle between two vectors, in radians, in the range [0, pi].</summary>
    /// <remarks>
    /// Computed as <c>atan2(|a x b|, a . b)</c> rather than <c>acos((a . b) / (|a| |b|))</c>. The
    /// arc-cosine form loses precision for nearly parallel and nearly opposite vectors, and rounding
    /// pushes its argument outside [-1, 1] often enough to yield NaN in practice.
    /// </remarks>
    /// <exception cref="ArgumentException">Either vector is degenerate or invalid.</exception>
    public static double AngleBetween(Vector3d a, Vector3d b)
    {
        if (!TryAngleBetween(a, b, out double? angle))
        {
            throw new ArgumentException(
                $"Cannot measure the angle between {a} and {b}: at least one is degenerate or invalid.");
        }

        return angle.Value;
    }

    /// <summary>The unsigned angle between two vectors, in radians, in the range [0, pi].</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <param name="angle">The angle, or <see langword="null"/> when the call fails.</param>
    /// <returns><see langword="false"/> when either vector is degenerate or invalid.</returns>
    public static bool TryAngleBetween(Vector3d a, Vector3d b, [NotNullWhen(true)] out double? angle)
    {
        if (IsZero(a) || IsZero(b) || !IsValid(a) || !IsValid(b))
        {
            angle = null;
            return false;
        }

        angle = Math.Atan2(Length(Cross(a, b)), Dot(a, b));
        return true;
    }

    /// <summary>
    /// The signed angle from <paramref name="from"/> to <paramref name="to"/> measured about
    /// <paramref name="axis"/>, in radians, in the range (-pi, pi].
    /// </summary>
    /// <remarks>
    /// Both vectors are first projected onto the plane perpendicular to <paramref name="axis"/>, so the
    /// inputs need not already lie in that plane. The sign follows the right-hand rule about
    /// <paramref name="axis"/>.
    /// <para>
    /// An unsigned angle cannot express direction, so anything that needs to tell clockwise from
    /// counter-clockwise — winding order, sorting around a hub, arc parameters — needs this and not
    /// <see cref="AngleBetween"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The axis is degenerate, or a vector collapses to nothing once projected onto the plane
    /// perpendicular to the axis.
    /// </exception>
    public static double SignedAngle(Vector3d from, Vector3d to, Vector3d axis)
    {
        if (!TrySignedAngle(from, to, axis, out double? angle))
        {
            throw new ArgumentException(
                $"Cannot measure the signed angle from {from} to {to} about {axis}: " +
                "the axis is degenerate, or a vector vanishes when projected onto the plane perpendicular to it.");
        }

        return angle.Value;
    }

    /// <summary>
    /// The signed angle from <paramref name="from"/> to <paramref name="to"/> about
    /// <paramref name="axis"/>, in the range (-pi, pi].
    /// </summary>
    /// <param name="from">The direction to measure from.</param>
    /// <param name="to">The direction to measure to.</param>
    /// <param name="axis">The axis to measure about.</param>
    /// <param name="angle">The angle, or <see langword="null"/> when the call fails.</param>
    /// <returns><see langword="false"/> when the inputs do not define an angle.</returns>
    public static bool TrySignedAngle(
        Vector3d from,
        Vector3d to,
        Vector3d axis,
        [NotNullWhen(true)] out double? angle)
    {
        angle = null;

        if (!IsValid(from) || !IsValid(to) || !TryNormalize(axis, out Vector3d? unitAxis))
        {
            return false;
        }

        Vector3d normal = unitAxis.Value;
        Vector3d projectedFrom = from - (normal * Dot(from, normal));
        Vector3d projectedTo = to - (normal * Dot(to, normal));

        if (IsZero(projectedFrom) || IsZero(projectedTo))
        {
            return false;
        }

        angle = Math.Atan2(
            Dot(Cross(projectedFrom, projectedTo), normal),
            Dot(projectedFrom, projectedTo));

        return true;
    }

    /// <summary>
    /// <see langword="true"/> when the two vectors point along the same line, in either direction.
    /// </summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <param name="angleTolerance">How far from parallel, in radians, still counts as parallel.</param>
    public static bool IsParallelTo(Vector3d a, Vector3d b, double angleTolerance = Tolerance.Angle)
    {
        if (!TryAngleBetween(a, b, out double? angle))
        {
            return false;
        }

        return angle.Value <= angleTolerance || angle.Value >= Math.PI - angleTolerance;
    }

    /// <summary><see langword="true"/> when the two vectors are at right angles.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <param name="angleTolerance">
    /// How far from a right angle, in radians, still counts as perpendicular.
    /// </param>
    public static bool IsPerpendicularTo(Vector3d a, Vector3d b, double angleTolerance = Tolerance.Angle)
    {
        if (!TryAngleBetween(a, b, out double? angle))
        {
            return false;
        }

        return Math.Abs(angle.Value - (Math.PI * 0.5)) <= angleTolerance;
    }

    /// <summary>
    /// Some unit vector at right angles to the input. Which one is unspecified but deterministic.
    /// </summary>
    /// <remarks>
    /// Crosses with whichever principal axis the input is least aligned with, so the result stays well
    /// conditioned even when the vector is nearly parallel to an axis.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The vector is degenerate or invalid.</exception>
    public static Vector3d PerpendicularTo(Vector3d vector)
    {
        if (!TryPerpendicularTo(vector, out Vector3d? perpendicular))
        {
            throw new InvalidOperationException(
                $"Cannot build a perpendicular to {vector}: the vector is degenerate or invalid.");
        }

        return perpendicular.Value;
    }

    /// <summary>
    /// Some unit vector at right angles to the input, reporting failure instead of throwing.
    /// </summary>
    /// <param name="vector">The vector to find a perpendicular to.</param>
    /// <param name="perpendicular">
    /// The perpendicular, or <see langword="null"/> when the call fails.
    /// </param>
    /// <returns><see langword="false"/> when the vector is degenerate or invalid.</returns>
    public static bool TryPerpendicularTo(Vector3d vector, [NotNullWhen(true)] out Vector3d? perpendicular)
    {
        if (!TryNormalize(vector, out Vector3d? normalized))
        {
            perpendicular = null;
            return false;
        }

        Vector3d unit = normalized.Value;

        double absX = Math.Abs(unit.X);
        double absY = Math.Abs(unit.Y);
        double absZ = Math.Abs(unit.Z);

        Vector3d leastAligned = absX <= absY && absX <= absZ
            ? Vector3d.XAxis
            : absY <= absZ ? Vector3d.YAxis : Vector3d.ZAxis;

        return TryNormalize(Cross(unit, leastAligned), out perpendicular);
    }

    /// <summary>
    /// <see langword="true"/> when the two vectors differ by no more than
    /// <paramref name="tolerance"/> in magnitude.
    /// </summary>
    public static bool EpsilonEquals(Vector3d a, Vector3d b, double tolerance = Tolerance.Distance) =>
        LengthSquared(a - b) <= tolerance * tolerance;

    /// <summary>The vector rotated, scaled and sheared by a transformation matrix.</summary>
    /// <remarks>
    /// Translation is deliberately ignored: a direction has no position, so moving the world must leave
    /// it unchanged. This is what separates transforming a vector from transforming a point.
    /// <para>
    /// Note that under a non-uniform scale this is the right transform for a tangent but the wrong one
    /// for a surface normal, which needs the inverse transpose.
    /// </para>
    /// </remarks>
    public static Vector3d Transform(Vector3d vector, in TMatrix matrix) => new(
        (matrix.M11 * vector.X) + (matrix.M12 * vector.Y) + (matrix.M13 * vector.Z),
        (matrix.M21 * vector.X) + (matrix.M22 * vector.Y) + (matrix.M23 * vector.Z),
        (matrix.M31 * vector.X) + (matrix.M32 * vector.Y) + (matrix.M33 * vector.Z));

    /// <summary>Reads one vector from the first three values of a component buffer.</summary>
    /// <remarks>Slice the span to read from an offset.</remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="components"/> holds fewer than three values.
    /// </exception>
    public static Vector3d CreateFromComponents(ReadOnlySpan<double> components)
    {
        if (components.Length < 3)
        {
            throw new ArgumentException(
                $"Expected at least 3 components, but the buffer holds {components.Length}.",
                nameof(components));
        }

        return new Vector3d(components[0], components[1], components[2]);
    }

    /// <summary>Sum of a set of vectors.</summary>
    /// <remarks>Returns <see cref="Vector3d.Zero"/> for an empty set, which is the identity.</remarks>
    public static Vector3d Sum(ReadOnlySpan<Vector3d> vectors)
    {
        double x = 0;
        double y = 0;
        double z = 0;

        foreach (Vector3d vector in vectors)
        {
            x += vector.X;
            y += vector.Y;
            z += vector.Z;
        }

        return new Vector3d(x, y, z);
    }

    /// <summary>The average direction of a set of vectors, scaled to unit length.</summary>
    /// <remarks>
    /// This is how a vertex normal is built from the normals of the faces around it. Summing then
    /// normalising weights each contribution by its magnitude, so pass unit vectors when every
    /// contribution should count equally.
    /// </remarks>
    /// <param name="vectors">The vectors to average.</param>
    /// <param name="average">
    /// The unit average, or <see langword="null"/> when the vectors cancel out or the set is empty.
    /// </param>
    /// <returns><see langword="false"/> when the sum has no direction.</returns>
    public static bool TryAverageDirection(
        ReadOnlySpan<Vector3d> vectors,
        [NotNullWhen(true)] out Vector3d? average) =>
        TryNormalize(Sum(vectors), out average);
}
