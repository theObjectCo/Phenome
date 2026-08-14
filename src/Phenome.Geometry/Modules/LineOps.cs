using System.Diagnostics.CodeAnalysis;

namespace Phenome.Geometry.Modules;

/// <summary>
/// Everything you can do with a <see cref="Line"/>.
/// </summary>
/// <remarks>
/// Includes relations between a line and a point, since the line is the richer of the two types, and
/// relations between two lines.
/// </remarks>
public static class LineOps
{
    /// <summary>A segment between two points.</summary>
    public static Line Create(Point3d from, Point3d to) => new(from, to);

    /// <summary><see langword="true"/> when both endpoints are finite.</summary>
    /// <remarks>
    /// A valid segment may still be degenerate, i.e. have zero length. Check
    /// <see cref="IsDegenerate"/> separately when a direction is required.
    /// </remarks>
    public static bool IsValid(in Line line) =>
        PointOps.IsValid(line.From) && PointOps.IsValid(line.To);

    /// <summary>The vector from <see cref="Line.From"/> to <see cref="Line.To"/>, not normalised.</summary>
    public static Vector3d Direction(in Line line) => line.To - line.From;

    /// <summary>Length of the segment.</summary>
    public static double Length(in Line line) => PointOps.DistanceTo(line.From, line.To);

    /// <summary>Squared length of the segment.</summary>
    /// <remarks>Prefer this over <see cref="Length"/> when comparing lengths.</remarks>
    public static double LengthSquared(in Line line) =>
        PointOps.DistanceSquaredTo(line.From, line.To);

    /// <summary>The midpoint of the segment.</summary>
    public static Point3d Midpoint(in Line line) => PointOps.Lerp(line.From, line.To, 0.5);

    /// <summary><see langword="true"/> when the segment is too short to define a direction.</summary>
    /// <param name="line">The segment to test.</param>
    /// <param name="tolerance">Length at or below which the segment counts as degenerate.</param>
    public static bool IsDegenerate(in Line line, double tolerance = Tolerance.Zero) =>
        !IsValid(line) || LengthSquared(line) <= tolerance * tolerance;

    /// <summary>The direction of the segment, scaled to unit length.</summary>
    /// <exception cref="InvalidOperationException">
    /// The segment is degenerate or invalid. Use <see cref="TryUnitDirection"/> when that is expected.
    /// </exception>
    public static Vector3d UnitDirection(in Line line) => VectorOps.Normalized(Direction(line));

    /// <summary>
    /// The direction of the segment scaled to unit length, reporting failure instead of producing NaN.
    /// </summary>
    /// <param name="line">The segment to measure.</param>
    /// <param name="unitDirection">The unit direction, or <see langword="null"/> when the call fails.</param>
    /// <returns><see langword="false"/> when the segment is degenerate or invalid.</returns>
    public static bool TryUnitDirection(in Line line, [NotNullWhen(true)] out Vector3d? unitDirection) =>
        VectorOps.TryNormalize(Direction(line), out unitDirection);

    /// <summary>The same segment with its endpoints swapped.</summary>
    public static Line Flipped(in Line line) => new(line.To, line.From);

    /// <summary>The segment with both endpoints moved by a transformation matrix.</summary>
    /// <remarks>
    /// Both endpoints are transformed as positions, so translation applies. The previous mutating
    /// version of this operation silently did nothing whenever it was called on a segment obtained from
    /// a property, because it transformed a copy.
    /// </remarks>
    public static Line Transform(in Line line, in TMatrix matrix) =>
        new(PointOps.Transform(line.From, matrix), PointOps.Transform(line.To, matrix));

    /// <summary>
    /// The point at a normalised <paramref name="parameter"/>, where 0 is <see cref="Line.From"/> and 1
    /// is <see cref="Line.To"/>. Values outside [0, 1] lie on the infinite line.
    /// </summary>
    public static Point3d PointAt(in Line line, double parameter) =>
        PointOps.Lerp(line.From, line.To, parameter);

    /// <summary>
    /// The point at <paramref name="distance"/> from <see cref="Line.From"/>, measured along the segment.
    /// </summary>
    /// <exception cref="InvalidOperationException">The segment is degenerate or invalid.</exception>
    public static Point3d PointAtLength(in Line line, double distance) =>
        line.From + (UnitDirection(line) * distance);

    /// <summary>
    /// The normalised parameter of the point on a line closest to <paramref name="point"/>.
    /// </summary>
    /// <param name="line">The line to project onto.</param>
    /// <param name="point">The point to project.</param>
    /// <param name="limitToSegment">
    /// When <see langword="true"/>, the result is clamped to [0, 1] so that it lies on the segment.
    /// </param>
    /// <returns>
    /// The parameter, or 0 when the segment is degenerate — every point on a zero-length segment is
    /// <see cref="Line.From"/>, so that is the honest answer, and it avoids handing back a NaN.
    /// </returns>
    public static double ClosestParameter(in Line line, Point3d point, bool limitToSegment = false)
    {
        Vector3d direction = Direction(line);
        double lengthSquared = VectorOps.LengthSquared(direction);

        if (lengthSquared <= Tolerance.ZeroSquared)
        {
            return 0.0;
        }

        double parameter = VectorOps.Dot(point - line.From, direction) / lengthSquared;

        return limitToSegment ? Math.Clamp(parameter, 0.0, 1.0) : parameter;
    }

    /// <summary>The point on a line closest to <paramref name="point"/>.</summary>
    /// <param name="line">The line to project onto.</param>
    /// <param name="point">The point to project.</param>
    /// <param name="limitToSegment">
    /// When <see langword="true"/>, the result is constrained to the segment rather than the infinite
    /// line.
    /// </param>
    public static Point3d ClosestPoint(in Line line, Point3d point, bool limitToSegment = false) =>
        PointAt(line, ClosestParameter(line, point, limitToSegment));

    /// <summary>Distance from <paramref name="point"/> to a line.</summary>
    /// <param name="line">The line to measure to.</param>
    /// <param name="point">The point to measure from.</param>
    /// <param name="limitToSegment">
    /// When <see langword="true"/>, measures to the segment rather than to the infinite line.
    /// </param>
    public static double DistanceTo(in Line line, Point3d point, bool limitToSegment = false) =>
        PointOps.DistanceTo(ClosestPoint(line, point, limitToSegment), point);

    /// <summary>
    /// Creates a segment starting at <paramref name="origin"/>, running along
    /// <paramref name="direction"/> for <paramref name="length"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="direction"/> is degenerate or invalid, so no segment can be built. Use
    /// <see cref="TryCreateFromPointDirection"/> when that is expected.
    /// </exception>
    public static Line CreateFromPointDirection(Point3d origin, Vector3d direction, double length)
    {
        if (!TryCreateFromPointDirection(origin, direction, length, out Line? line))
        {
            throw new ArgumentException(
                $"Cannot build a line from {origin} along {direction}: the direction is degenerate or invalid.",
                nameof(direction));
        }

        return line.Value;
    }

    /// <summary>
    /// Creates a segment from an origin, a direction and a length, reporting failure instead of
    /// producing a segment with NaN endpoints.
    /// </summary>
    /// <param name="origin">Where the segment starts.</param>
    /// <param name="direction">The direction to run along; need not be normalised.</param>
    /// <param name="length">How far to run.</param>
    /// <param name="line">The segment, or <see langword="null"/> when the call fails.</param>
    /// <returns><see langword="false"/> when the direction does not define an orientation.</returns>
    public static bool TryCreateFromPointDirection(
        Point3d origin,
        Vector3d direction,
        double length,
        [NotNullWhen(true)] out Line? line)
    {
        if (!VectorOps.TryNormalize(direction, out Vector3d? unit))
        {
            line = null;
            return false;
        }

        line = new Line(origin, origin + (unit.Value * length));
        return true;
    }

    /// <summary>The parameters at which two lines come closest to one another.</summary>
    /// <param name="a">The first line.</param>
    /// <param name="b">The second line.</param>
    /// <param name="parameterOnA">
    /// Normalised parameter of the closest point on <paramref name="a"/>, or <see langword="null"/> when
    /// the call fails.
    /// </param>
    /// <param name="parameterOnB">
    /// Normalised parameter of the closest point on <paramref name="b"/>, or <see langword="null"/> when
    /// the call fails.
    /// </param>
    /// <param name="limitToSegments">
    /// When <see langword="true"/>, both parameters are clamped to [0, 1]. Note that clamping each
    /// parameter independently is an approximation for skew segments; it is exact when the unconstrained
    /// solution already lies within both segments.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the lines are parallel or either is degenerate, in which case no
    /// single pair of closest points exists.
    /// </returns>
    public static bool TryClosestParameters(
        in Line a,
        in Line b,
        [NotNullWhen(true)] out double? parameterOnA,
        [NotNullWhen(true)] out double? parameterOnB,
        bool limitToSegments = false)
    {
        parameterOnA = null;
        parameterOnB = null;

        if (!IsValid(a) || !IsValid(b))
        {
            return false;
        }

        Vector3d directionA = Direction(a);
        Vector3d directionB = Direction(b);
        Vector3d between = b.From - a.From;

        double aa = VectorOps.Dot(directionA, directionA);
        double ab = VectorOps.Dot(directionA, directionB);
        double bb = VectorOps.Dot(directionB, directionB);

        double determinant = (aa * bb) - (ab * ab);

        // Vanishes when either direction is degenerate or the two are parallel.
        if (Math.Abs(determinant) <= Tolerance.ZeroSquared)
        {
            return false;
        }

        double projectionA = VectorOps.Dot(directionA, between);
        double projectionB = VectorOps.Dot(directionB, between);

        double solvedA = ((projectionA * bb) - (ab * projectionB)) / determinant;
        double solvedB = ((projectionA * ab) - (aa * projectionB)) / determinant;

        if (limitToSegments)
        {
            solvedA = Math.Clamp(solvedA, 0.0, 1.0);
            solvedB = Math.Clamp(solvedB, 0.0, 1.0);
        }

        parameterOnA = solvedA;
        parameterOnB = solvedB;
        return true;
    }

    /// <summary>The pair of points at which two lines come closest to one another.</summary>
    /// <param name="a">The first line.</param>
    /// <param name="b">The second line.</param>
    /// <param name="pointOnA">
    /// The closest point on <paramref name="a"/>, or <see langword="null"/> when the call fails.
    /// </param>
    /// <param name="pointOnB">
    /// The closest point on <paramref name="b"/>, or <see langword="null"/> when the call fails.
    /// </param>
    /// <param name="limitToSegments">
    /// When <see langword="true"/>, both points are constrained to their segments.
    /// </param>
    /// <returns><see langword="false"/> when the lines are parallel or either is degenerate.</returns>
    public static bool TryClosestPoints(
        in Line a,
        in Line b,
        [NotNullWhen(true)] out Point3d? pointOnA,
        [NotNullWhen(true)] out Point3d? pointOnB,
        bool limitToSegments = false)
    {
        if (!TryClosestParameters(a, b, out double? parameterOnA, out double? parameterOnB, limitToSegments))
        {
            pointOnA = null;
            pointOnB = null;
            return false;
        }

        pointOnA = PointAt(a, parameterOnA.Value);
        pointOnB = PointAt(b, parameterOnB.Value);
        return true;
    }

    /// <summary>
    /// <see langword="true"/> when both endpoints are within <paramref name="tolerance"/> of the
    /// corresponding endpoints of the other segment.
    /// </summary>
    /// <remarks>
    /// Direction-sensitive: a segment and its flipped counterpart are not epsilon-equal.
    /// </remarks>
    public static bool EpsilonEquals(in Line a, in Line b, double tolerance = Tolerance.Distance) =>
        PointOps.EpsilonEquals(a.From, b.From, tolerance) &&
        PointOps.EpsilonEquals(a.To, b.To, tolerance);
}
