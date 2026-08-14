using System.Diagnostics.CodeAnalysis;

namespace Phenome.Geometry.Modules;

/// <summary>
/// Everything you can do with a <see cref="Point3d"/>.
/// </summary>
/// <remarks>
/// The type holds three coordinates; this module holds every operation over them. Relations between a
/// point and a richer type live in that type's module instead — point against a line is in
/// <see cref="LineOps"/>, point against a plane is in <see cref="PlaneOps"/>.
/// </remarks>
public static class PointOps
{
    /// <summary>A point at the given coordinates.</summary>
    public static Point3d Create(double x, double y, double z) => new(x, y, z);

    /// <summary>
    /// <see langword="true"/> when every coordinate is a finite number, i.e. neither NaN nor infinite.
    /// </summary>
    public static bool IsValid(Point3d point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);

    /// <summary>Squared distance between two points.</summary>
    /// <remarks>Prefer this over <see cref="DistanceTo"/> when comparing distances.</remarks>
    public static double DistanceSquaredTo(Point3d from, Point3d to)
    {
        double dx = from.X - to.X;
        double dy = from.Y - to.Y;
        double dz = from.Z - to.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    /// <summary>Distance between two points.</summary>
    public static double DistanceTo(Point3d from, Point3d to) => Math.Sqrt(DistanceSquaredTo(from, to));

    /// <summary>
    /// <see langword="true"/> when two points are within <paramref name="tolerance"/> of each other.
    /// </summary>
    /// <param name="a">The first point.</param>
    /// <param name="b">The second point.</param>
    /// <param name="tolerance">
    /// Maximum distance at which the two points are still considered equal. Pass a value that matches
    /// the scale of your model rather than relying on the default.
    /// </param>
    public static bool EpsilonEquals(Point3d a, Point3d b, double tolerance = Tolerance.Distance) =>
        DistanceSquaredTo(a, b) <= tolerance * tolerance;

    /// <summary>
    /// Linearly interpolates between two points. <paramref name="parameter"/> 0 returns
    /// <paramref name="from"/>, 1 returns <paramref name="to"/>; values outside [0, 1] extrapolate.
    /// </summary>
    public static Point3d Lerp(Point3d from, Point3d to, double parameter) =>
        new(
            from.X + ((to.X - from.X) * parameter),
            from.Y + ((to.Y - from.Y) * parameter),
            from.Z + ((to.Z - from.Z) * parameter));

    /// <summary>The point moved by a transformation matrix.</summary>
    /// <remarks>
    /// Applies the full affine transform, translation included. When the matrix carries a perspective
    /// row the result is divided by the resulting w; if that w collapses to zero the point maps to
    /// infinity and <see cref="Point3d.Unset"/> is returned instead of infinity-filled coordinates.
    /// </remarks>
    public static Point3d Transform(Point3d point, in TMatrix matrix)
    {
        double x = (matrix.M11 * point.X) + (matrix.M12 * point.Y) + (matrix.M13 * point.Z) + matrix.M14;
        double y = (matrix.M21 * point.X) + (matrix.M22 * point.Y) + (matrix.M23 * point.Z) + matrix.M24;
        double z = (matrix.M31 * point.X) + (matrix.M32 * point.Y) + (matrix.M33 * point.Z) + matrix.M34;
        double w = (matrix.M41 * point.X) + (matrix.M42 * point.Y) + (matrix.M43 * point.Z) + matrix.M44;

        // The overwhelmingly common case: an affine matrix leaves w at exactly one.
        if (w == 1.0)
        {
            return new Point3d(x, y, z);
        }

        if (Math.Abs(w) <= Tolerance.Zero)
        {
            return Point3d.Unset;
        }

        double denominator = 1.0 / w;
        return new Point3d(x * denominator, y * denominator, z * denominator);
    }

    /// <summary>Arithmetic mean of a set of points.</summary>
    /// <exception cref="ArgumentException"><paramref name="points"/> is empty.</exception>
    public static Point3d Centroid(ReadOnlySpan<Point3d> points)
    {
        if (points.IsEmpty)
        {
            throw new ArgumentException("Cannot compute the centroid of an empty set.", nameof(points));
        }

        double x = 0;
        double y = 0;
        double z = 0;

        foreach (Point3d point in points)
        {
            x += point.X;
            y += point.Y;
            z += point.Z;
        }

        double denominator = 1.0 / points.Length;
        return new Point3d(x * denominator, y * denominator, z * denominator);
    }

    /// <summary>Arithmetic mean of a sequence of points, enumerated exactly once.</summary>
    /// <remarks>
    /// The previous library indexed sequences with <c>ElementAt(i)</c> inside a loop bounded by
    /// <c>Count()</c>, which is quadratic for anything that is not a list and re-enumerates on every
    /// step. Prefer the span overload where you can.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="points"/> is empty.</exception>
    public static Point3d Centroid(IEnumerable<Point3d> points)
    {
        double x = 0;
        double y = 0;
        double z = 0;
        int count = 0;

        foreach (Point3d point in points)
        {
            x += point.X;
            y += point.Y;
            z += point.Z;
            count++;
        }

        if (count == 0)
        {
            throw new ArgumentException("Cannot compute the centroid of an empty sequence.", nameof(points));
        }

        double denominator = 1.0 / count;
        return new Point3d(x * denominator, y * denominator, z * denominator);
    }

    /// <summary>Reads one point from the first three values of a coordinate buffer.</summary>
    /// <remarks>
    /// Slice the span to read from an offset. Taking a span rather than an array means a missing buffer
    /// is simply an empty one, so there is no null case to handle separately.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="coordinates"/> holds fewer than three values.
    /// </exception>
    public static Point3d CreateFromCoordinates(ReadOnlySpan<double> coordinates)
    {
        if (coordinates.Length < 3)
        {
            throw new ArgumentException(
                $"Expected at least 3 coordinates, but the buffer holds {coordinates.Length}.",
                nameof(coordinates));
        }

        return new Point3d(coordinates[0], coordinates[1], coordinates[2]);
    }

    /// <summary>
    /// The index of the point in <paramref name="points"/> closest to <paramref name="target"/>.
    /// </summary>
    /// <param name="points">The candidates to search.</param>
    /// <param name="target">The position to measure from.</param>
    /// <param name="index">The index of the closest point, or <see langword="null"/> when the set is empty.</param>
    /// <returns><see langword="false"/> when <paramref name="points"/> is empty.</returns>
    public static bool TryClosestIndex(
        ReadOnlySpan<Point3d> points,
        Point3d target,
        [NotNullWhen(true)] out int? index)
    {
        double best = double.MaxValue;
        int bestIndex = -1;

        for (int i = 0; i < points.Length; i++)
        {
            double distance = DistanceSquaredTo(points[i], target);

            if (distance < best)
            {
                best = distance;
                bestIndex = i;
            }
        }

        index = bestIndex < 0 ? null : bestIndex;
        return bestIndex >= 0;
    }
}
