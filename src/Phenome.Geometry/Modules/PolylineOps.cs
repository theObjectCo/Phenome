using System.Diagnostics.CodeAnalysis;
namespace Phenome.Geometry.Modules;

/// <summary>
/// Everything you can do with a <see cref="Polyline"/>.
/// </summary>
/// <remarks>
/// Parameters are index-based, the way RhinoCommon does it: the integer part picks a segment and the
/// fraction is the position along it, so parameter 2.5 is the midpoint of segment 2. That costs nothing to
/// evaluate, unlike a parameter normalised over total length, which needs the length first. Use
/// <see cref="PointAtLength"/> when arc length is what matters.
/// </remarks>
public static class PolylineOps
{
    /// <summary>An empty polyline.</summary>
    public static Polyline Create() => new();

    /// <summary>A polyline through the given points, in order.</summary>
    public static Polyline Create(ReadOnlySpan<Point3d> points)
    {
        Polyline polyline = new();
        polyline.Reserve(points.Length);
        polyline.AddPoints(points);
        return polyline;
    }

    /// <summary>An independent copy.</summary>
    public static Polyline Duplicate(Polyline polyline) =>
        Create(polyline.Points);

    /// <summary>
    /// <see langword="true"/> when the polyline has at least two points and all of them are finite.
    /// </summary>
    /// <remarks>
    /// A single point is not a polyline: it has no segment, so nothing that follows would have an answer.
    /// </remarks>
    public static bool IsValid(Polyline polyline)
    {
        if (polyline.PointCount < 2)
        {
            return false;
        }

        foreach (Point3d point in polyline.Points)
        {
            if (!PointOps.IsValid(point))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>How many straight segments the polyline has, which is one fewer than its points.</summary>
    public static int SegmentCount(Polyline polyline) =>
        Math.Max(0, polyline.PointCount - 1);

    /// <summary>One segment of the polyline.</summary>
    /// <remarks>
    /// Allocates nothing. The previous library rebuilt an array of every segment on each call, including
    /// from inside its closest-point search.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="segmentIndex"/> is outside 0..<c>SegmentCount</c>-1.
    /// </exception>
    public static Line Segment(Polyline polyline, int segmentIndex)
    {
        Guard.NotNegative(segmentIndex);
        Guard.LessThan(segmentIndex, SegmentCount(polyline));

        ReadOnlySpan<Point3d> points = polyline.Points;
        return new Line(points[segmentIndex], points[segmentIndex + 1]);
    }

    /// <summary>Every segment, as a fresh array.</summary>
    /// <remarks>
    /// A convenience for when the segments are the thing being worked on. Loop over
    /// <see cref="Segment"/> instead when you only need to visit them.
    /// </remarks>
    public static Line[] Segments(Polyline polyline)
    {
        Line[] segments = new Line[SegmentCount(polyline)];

        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = Segment(polyline, i);
        }

        return segments;
    }

    /// <summary>Total length along the polyline.</summary>
    public static double Length(Polyline polyline)
    {
        ReadOnlySpan<Point3d> points = polyline.Points;
        double total = 0;

        for (int i = 1; i < points.Length; i++)
        {
            total += PointOps.DistanceTo(points[i - 1], points[i]);
        }

        return total;
    }

    /// <summary>
    /// <see langword="true"/> when the last point coincides with the first, so the polyline encloses a
    /// region.
    /// </summary>
    /// <remarks>
    /// Closing is expressed by repeating the first point, not by a flag, so this is a distance test rather
    /// than a structural one — and it takes a tolerance, because the previous library compared the two
    /// points for exact float equality and a closed loop that had been through a transform stopped counting
    /// as closed.
    /// </remarks>
    public static bool IsClosed(Polyline polyline, double tolerance = Tolerance.Distance)
    {
        // Three points is the minimum that can enclose anything once the repeat is accounted for.
        return polyline.PointCount >= 4 &&
            PointOps.EpsilonEquals(polyline.Points[0], polyline.Points[^1], tolerance);
    }

    /// <summary>
    /// A copy with the first point repeated at the end, or an unchanged copy when it already is.
    /// </summary>
    /// <exception cref="ArgumentException">The polyline has fewer than three points to close.</exception>
    public static Polyline Closed(Polyline polyline, double tolerance = Tolerance.Distance)
    {
        if (polyline.PointCount < 3)
        {
            throw new ArgumentException(
                $"Closing needs at least 3 points, but the polyline holds {polyline.PointCount}.",
                nameof(polyline));
        }

        if (IsClosed(polyline, tolerance))
        {
            return Duplicate(polyline);
        }

        Polyline closed = new();
        closed.Reserve(polyline.PointCount + 1);
        closed.AddPoints(polyline.Points);
        closed.AddPoint(polyline.Points[0]);
        return closed;
    }

    /// <summary>A copy with the points in the opposite order.</summary>
    public static Polyline Reversed(Polyline polyline)
    {
        Polyline reversed = new();
        reversed.Reserve(polyline.PointCount);

        ReadOnlySpan<Point3d> points = polyline.Points;

        for (int i = points.Length - 1; i >= 0; i--)
        {
            reversed.AddPoint(points[i]);
        }

        return reversed;
    }

    /// <summary>Moves every point of the polyline by a transformation matrix, in place.</summary>
    /// <remarks>
    /// In place, matching <see cref="MeshOps.Transform"/>. Wrap the input in
    /// <see cref="Duplicate"/> when it has to survive.
    /// </remarks>
    public static void Transform(Polyline polyline, in TMatrix matrix)
    {
        Span<Point3d> points = polyline.PointsForWriting();

        for (int i = 0; i < points.Length; i++)
        {
            points[i] = PointOps.Transform(points[i], matrix);
        }
    }

    /// <summary>
    /// The point at an index-based <paramref name="parameter"/>: the integer part picks a segment, the
    /// fraction is the position along it.
    /// </summary>
    /// <remarks>
    /// Clamped to the polyline. A polyline has no natural extension past its ends, so extrapolating would be
    /// inventing geometry.
    /// </remarks>
    /// <exception cref="ArgumentException">The polyline has fewer than two points.</exception>
    public static Point3d PointAt(Polyline polyline, double parameter)
    {
        RequireTwoPoints(polyline);

        ReadOnlySpan<Point3d> points = polyline.Points;
        int lastSegment = points.Length - 1;

        double clamped = Math.Clamp(parameter, 0.0, lastSegment);
        int segment = (int)Math.Floor(clamped);

        if (segment >= lastSegment)
        {
            return points[^1];
        }

        return PointOps.Lerp(points[segment], points[segment + 1], clamped - segment);
    }

    /// <summary>The point at <paramref name="distance"/> measured along the polyline from its start.</summary>
    /// <remarks>Clamped to the polyline, for the same reason as <see cref="PointAt"/>.</remarks>
    /// <exception cref="ArgumentException">The polyline has fewer than two points.</exception>
    public static Point3d PointAtLength(Polyline polyline, double distance)
    {
        RequireTwoPoints(polyline);

        ReadOnlySpan<Point3d> points = polyline.Points;

        if (distance <= 0)
        {
            return points[0];
        }

        double remaining = distance;

        for (int i = 1; i < points.Length; i++)
        {
            double segmentLength = PointOps.DistanceTo(points[i - 1], points[i]);

            if (remaining <= segmentLength)
            {
                // A zero-length segment cannot be divided, so land on its start.
                double fraction = segmentLength <= Tolerance.Zero ? 0.0 : remaining / segmentLength;
                return PointOps.Lerp(points[i - 1], points[i], fraction);
            }

            remaining -= segmentLength;
        }

        return points[^1];
    }

    /// <summary>
    /// The index-based parameter of the point on the polyline closest to <paramref name="point"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The polyline has fewer than two points.</exception>
    public static double ClosestParameter(Polyline polyline, Point3d point)
    {
        RequireTwoPoints(polyline);

        double best = double.MaxValue;
        double bestParameter = 0;

        for (int i = 0; i < SegmentCount(polyline); i++)
        {
            Line segment = Segment(polyline, i);
            double fraction = LineOps.ClosestParameter(segment, point, limitToSegment: true);
            double distance = PointOps.DistanceSquaredTo(
                LineOps.PointAt(segment, fraction), point);

            if (distance < best)
            {
                best = distance;
                bestParameter = i + fraction;
            }
        }

        return bestParameter;
    }

    /// <summary>The point on the polyline closest to <paramref name="point"/>.</summary>
    /// <exception cref="ArgumentException">The polyline has fewer than two points.</exception>
    public static Point3d ClosestPoint(Polyline polyline, Point3d point) =>
        PointAt(polyline, ClosestParameter(polyline, point));

    /// <summary>Distance from <paramref name="point"/> to the polyline.</summary>
    /// <exception cref="ArgumentException">The polyline has fewer than two points.</exception>
    public static double DistanceTo(Polyline polyline, Point3d point) =>
        PointOps.DistanceTo(ClosestPoint(polyline, point), point);

    /// <summary>
    /// The area the polyline encloses when projected onto <paramref name="plane"/>, signed by winding
    /// direction.
    /// </summary>
    /// <remarks>
    /// Positive means the polyline runs counter-clockwise about the plane normal. An open polyline is treated
    /// as closed by the implied segment from its last point back to its first.
    /// <para>
    /// This is the shoelace formula over the projected coordinates. The previous library answered the same
    /// question by summing unsigned angles, which cannot distinguish a direction at all, so its
    /// clockwise test was decided by rounding.
    /// </para>
    /// </remarks>
    public static double SignedArea(Polyline polyline, in Plane plane)
    {
        ReadOnlySpan<Point3d> points = polyline.Points;

        if (points.Length < 3)
        {
            return 0;
        }

        double sum = 0;

        for (int i = 0; i < points.Length; i++)
        {
            (double u0, double v0) = PlaneOps.ClosestParameter(plane, points[i]);
            (double u1, double v1) = PlaneOps.ClosestParameter(
                plane, points[(i + 1) % points.Length]);

            sum += (u0 * v1) - (u1 * v0);
        }

        return sum * 0.5;
    }

    /// <summary>
    /// <see langword="true"/> when the polyline runs clockwise as seen from the
    /// <paramref name="plane"/> normal side.
    /// </summary>
    public static bool IsClockwise(Polyline polyline, in Plane plane) =>
        SignedArea(polyline, plane) < 0;

    /// <summary>
    /// Points dividing the polyline into <paramref name="segments"/> pieces of equal length, ends included.
    /// </summary>
    /// <param name="polyline">The polyline to divide.</param>
    /// <param name="segments">How many pieces; the result holds one more point than this.</param>
    /// <param name="points">
    /// The division points, or <see langword="null"/> when the polyline has no length to divide.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the polyline is invalid or every point coincides, so there is no length
    /// to divide along.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="segments"/> is zero or negative.</exception>
    public static bool TryDivideByCount(
        Polyline polyline,
        int segments,
        [NotNullWhen(true)] out Point3d[]? points)
    {
        Guard.Positive(segments);

        double length = Length(polyline);

        if (!IsValid(polyline) || length <= Tolerance.Zero)
        {
            points = null;
            return false;
        }

        Point3d[] divisions = new Point3d[segments + 1];
        double step = length / segments;

        for (int i = 0; i <= segments; i++)
        {
            divisions[i] = PointAtLength(polyline, step * i);
        }

        points = divisions;
        return true;
    }

    /// <summary>
    /// A copy with every corner it can manage replaced by a circular arc of the given radius.
    /// </summary>
    /// <remarks>
    /// Each corner is replaced by the arc tangent to both its legs, so the outline stays smooth where it used
    /// to have a crease. A closed polyline has every corner rounded, including the one where it closes; an
    /// open one keeps its two ends sharp, because an end has only one leg.
    /// <para>
    /// A corner is left alone rather than approximated when the radius will not fit it: when the corner is
    /// straight or folded right back, or when the fillet would run past the end of its own leg. Two adjacent
    /// corners competing for the same short leg are resolved by leaving the tighter one sharp, since that is
    /// the one demanding more room. Every skipped corner is counted in the result, so a caller can lower the
    /// radius and try again rather than wonder why an edge is still crisp.
    /// </para>
    /// <para>
    /// This offsets nothing and cleans nothing up. It replaces corners in place, so the filleted outline
    /// stays inside the original and no new crossings appear — which is the property that makes it safe to
    /// feed straight into <see cref="MeshBuilders.CreateExtrusion"/>.
    /// </para>
    /// </remarks>
    /// <param name="polyline">The outline to round.</param>
    /// <param name="radius">The fillet radius; must be finite and greater than zero.</param>
    /// <param name="plane">
    /// The plane the outline lies in. Only used to decide the arcs' orientation, so a nearly-planar outline
    /// still works; a genuinely non-planar one will not.
    /// </param>
    /// <param name="segmentsPerCorner">
    /// How many straight segments to divide each arc into; at least one. Pick it with
    /// <see cref="ArcOps.SegmentCountForTolerance"/> rather than by eye.
    /// </param>
    /// <param name="filleted">The rounded copy, or <see langword="null"/> when the call failed outright.</param>
    /// <returns>
    /// <see cref="ResultStatus.Partial"/> naming how many corners were left sharp;
    /// <see cref="ResultStatus.Failed"/> when the polyline, radius, plane or segment count is unusable.
    /// </returns>
    public static OperationResult Fillet(
        Polyline polyline,
        double radius,
        in Plane plane,
        int segmentsPerCorner,
        out Polyline? filleted)
    {
        filleted = null;

        if (!IsValid(polyline))
        {
            return OperationResult.Failed("The polyline needs at least two finite points to fillet.");
        }

        if (!double.IsFinite(radius) || radius <= 0)
        {
            return OperationResult.Failed(
                $"The fillet radius must be finite and greater than zero, but was {radius}.");
        }

        if (!PlaneOps.IsValid(plane))
        {
            return OperationResult.Failed("The plane is invalid, so arc orientation cannot be decided.");
        }

        if (segmentsPerCorner < 1)
        {
            return OperationResult.Failed(
                $"A fillet needs at least one segment per corner, not {segmentsPerCorner}.");
        }

        bool closed = IsClosed(polyline);
        Point3d[] corners = closed
            ? polyline.Points[..^1].ToArray()
            : polyline.Points.ToArray();

        int count = corners.Length;

        if (count < 3)
        {
            filleted = Duplicate(polyline);
            return OperationResult.Partial(
                "A polyline of fewer than three corners has nothing to round.");
        }

        int first = closed ? 0 : 1;
        int last = closed ? count - 1 : count - 2;

        // How far back along each leg the fillet reaches. Zero marks a corner that stays sharp.
        double[] tangentDistance = new double[count];
        int skipped = 0;

        for (int i = first; i <= last; i++)
        {
            Point3d previous = corners[(i - 1 + count) % count];
            Point3d next = corners[(i + 1) % count];

            if (!VectorOps.TryNormalize(previous - corners[i], out Vector3d? towardsPrevious) ||
                !VectorOps.TryNormalize(next - corners[i], out Vector3d? towardsNext))
            {
                skipped++;
                continue;
            }

            double angle = VectorOps.AngleBetween(towardsPrevious.Value, towardsNext.Value);

            if (angle <= Tolerance.Angle || angle >= Math.PI - Tolerance.Angle)
            {
                skipped++;
                continue;
            }

            tangentDistance[i] = radius / Math.Tan(angle * 0.5);
        }

        // Two adjacent fillets cannot both eat into a leg shorter than the sum of what they need. The tighter
        // corner gives way, because it is the one asking for more room.
        for (int i = 0; i < count; i++)
        {
            int j = (i + 1) % count;

            if (!closed && j == 0)
            {
                break;
            }

            if (tangentDistance[i] == 0 && tangentDistance[j] == 0)
            {
                continue;
            }

            double legLength = PointOps.DistanceTo(corners[i], corners[j]);

            if (tangentDistance[i] + tangentDistance[j] <= legLength)
            {
                continue;
            }

            if (tangentDistance[i] >= tangentDistance[j])
            {
                if (tangentDistance[i] > 0)
                {
                    tangentDistance[i] = 0;
                    skipped++;
                }
            }
            else if (tangentDistance[j] > 0)
            {
                tangentDistance[j] = 0;
                skipped++;
            }

            // Give the survivor another chance now that its neighbour has stood down.
            if (tangentDistance[i] + tangentDistance[j] > legLength)
            {
                if (tangentDistance[i] > 0)
                {
                    tangentDistance[i] = 0;
                    skipped++;
                }

                if (tangentDistance[j] > 0)
                {
                    tangentDistance[j] = 0;
                    skipped++;
                }
            }
        }

        Polyline result = new();

        for (int i = 0; i < count; i++)
        {
            if (tangentDistance[i] == 0)
            {
                result.AddPoint(corners[i]);
                continue;
            }

            Point3d previous = corners[(i - 1 + count) % count];
            Point3d next = corners[(i + 1) % count];

            Vector3d towardsPrevious = VectorOps.Normalized(previous - corners[i]);
            Vector3d towardsNext = VectorOps.Normalized(next - corners[i]);

            Point3d start = corners[i] + (towardsPrevious * tangentDistance[i]);
            Point3d end = corners[i] + (towardsNext * tangentDistance[i]);

            if (!CircleOps.TryCreateInCorner(previous, corners[i], next, radius, out Circle? circle) ||
                !ArcOps.TryCreateFromPoints(
                    start,
                    CircleOps.ClosestPoint(circle.Value, corners[i]),
                    end,
                    out Arc? arc))
            {
                result.AddPoint(corners[i]);
                skipped++;
                continue;
            }

            // The arc's own tessellation, minus its last point when a following corner will add its start.
            Polyline tessellated = ArcOps.ToPolyline(arc.Value, segmentsPerCorner);
            result.AddPoints(tessellated.Points);
        }

        if (closed)
        {
            result.AddPoint(result.Points[0]);
        }

        filleted = result;

        return skipped == 0
            ? OperationResult.Success
            : OperationResult.Partial(
                $"{skipped} corner(s) were left sharp because the radius {radius} does not fit them. Lower " +
                "the radius to round them too.");
    }

    /// <summary>
    /// A copy moved sideways by a constant distance within a plane.
    /// </summary>
    /// <remarks>
    /// Each segment is moved out on its own and the new corners are where consecutive moved segments cross,
    /// so straight stays straight and a corner stays a corner — no arcs are inserted and the corner count
    /// does not change. That is what makes this the right tool for a wall thickness or an inset front, where
    /// the offset outline should look like the original.
    /// <para>
    /// Positive moves to the right of the direction of travel, looking down the plane normal; negative moves
    /// left. That is the side facing away from the enclosed area of a counter-clockwise outline, so positive
    /// grows such an outline and negative shrinks it — and the reverse for a clockwise one, because an
    /// outline has no outside until you know which way round it runs.
    /// </para>
    /// <para>
    /// There is no cleanup, and this is the part worth reading twice. Offset a closed outline inwards by more
    /// than half the narrowest part of it and the segments that should have vanished are still there. Often
    /// that shows up as an outline crossing itself, which <see cref="Triangulation.SelfIntersects"/> would
    /// catch — but not always. Shrink a square by more than half its width and both pairs of opposite edges
    /// pass through each other; the two crossings cancel, and out comes a smaller, correctly wound, entirely
    /// fictitious square. Nothing in the result says it is wrong.
    /// <para>
    /// So the guard belongs with the caller, who knows the shape: compare the offset outline's signed area
    /// against the original's. An inward offset that grew, or one that kept its sign when it should have
    /// annihilated the outline, has produced a phantom. Doing better inside this function means trimming the
    /// offset segments against each other and rebuilding the outline from the surviving pieces, which is a
    /// different and much larger operation.
    /// </para>
    /// </para>
    /// </remarks>
    /// <param name="polyline">The outline to move.</param>
    /// <param name="distance">How far to move it; may be negative. Zero returns a copy.</param>
    /// <param name="plane">The plane to work in. Its normal decides which side "left" is.</param>
    /// <param name="offset">The moved copy, or <see langword="null"/> when the call failed outright.</param>
    /// <returns>
    /// <see cref="ResultStatus.Partial"/> when a corner folded back on itself so that the two moved segments
    /// do not cross, in which case that corner is moved along its own bisector instead;
    /// <see cref="ResultStatus.Failed"/> when the polyline or the plane is unusable.
    /// </returns>
    public static OperationResult Offset(
        Polyline polyline,
        double distance,
        in Plane plane,
        out Polyline? offset)
    {
        offset = null;

        if (!IsValid(polyline))
        {
            return OperationResult.Failed("The polyline needs at least two finite points to offset.");
        }

        if (!PlaneOps.IsValid(plane))
        {
            return OperationResult.Failed("The plane is invalid, so there is no side to offset towards.");
        }

        if (!double.IsFinite(distance))
        {
            return OperationResult.Failed($"The offset distance must be finite, but was {distance}.");
        }

        if (distance == 0)
        {
            offset = Duplicate(polyline);
            return OperationResult.Success;
        }

        bool closed = IsClosed(polyline);
        Point3d[] corners = closed
            ? polyline.Points[..^1].ToArray()
            : polyline.Points.ToArray();

        int count = corners.Length;

        if (count < 2)
        {
            return OperationResult.Failed("The polyline collapses to a single point.");
        }

        double[] u = new double[count];
        double[] v = new double[count];

        for (int i = 0; i < count; i++)
        {
            (u[i], v[i]) = PlaneOps.ClosestParameter(plane, corners[i]);
        }

        int segments = closed ? count : count - 1;

        // Each segment's moved copy, as a point on it plus its direction.
        double[] baseU = new double[segments];
        double[] baseV = new double[segments];
        double[] dirU = new double[segments];
        double[] dirV = new double[segments];

        for (int s = 0; s < segments; s++)
        {
            int a = s;
            int b = (s + 1) % count;

            double du = u[b] - u[a];
            double dv = v[b] - v[a];
            double length = Math.Sqrt((du * du) + (dv * dv));

            if (length <= Tolerance.Zero)
            {
                return OperationResult.Failed(
                    $"Segment {s} has no length, so it has no side to offset towards.");
            }

            du /= length;
            dv /= length;

            dirU[s] = du;
            dirV[s] = dv;

            // The right of the direction of travel, a quarter turn against the plane normal. That is the
            // side that points away from the enclosed area for a counter-clockwise outline, which is what
            // makes a positive distance grow it.
            baseU[s] = u[a] + (dv * distance);
            baseV[s] = v[a] + (-du * distance);
        }

        double[] outU = new double[count];
        double[] outV = new double[count];
        int folded = 0;

        for (int i = 0; i < count; i++)
        {
            bool hasIncoming = closed || i > 0;
            bool hasOutgoing = closed || i < count - 1;

            int incoming = (i - 1 + count) % count;
            int outgoing = i % count;

            if (!hasIncoming)
            {
                // An open end has one segment, so it simply moves with it.
                outU[i] = baseU[outgoing];
                outV[i] = baseV[outgoing];
                continue;
            }

            if (!hasOutgoing)
            {
                outU[i] = baseU[incoming] + (dirU[incoming] * SegmentLength(u, v, incoming, count));
                outV[i] = baseV[incoming] + (dirV[incoming] * SegmentLength(u, v, incoming, count));
                continue;
            }

            double cross = (dirU[incoming] * dirV[outgoing]) - (dirV[incoming] * dirU[outgoing]);

            if (Math.Abs(cross) <= Tolerance.Zero)
            {
                // The two segments run parallel: either straight through, where the moved lines coincide, or
                // folded right back, where they do not meet at all. Either way the corner's own offset point
                // is the only sensible answer.
                double toCornerLength = SegmentLength(u, v, incoming, count);
                outU[i] = baseU[incoming] + (dirU[incoming] * toCornerLength);
                outV[i] = baseV[incoming] + (dirV[incoming] * toCornerLength);

                if (((dirU[incoming] * dirU[outgoing]) + (dirV[incoming] * dirV[outgoing])) < 0)
                {
                    folded++;
                }

                continue;
            }

            double gapU = baseU[outgoing] - baseU[incoming];
            double gapV = baseV[outgoing] - baseV[incoming];
            double t = ((gapU * dirV[outgoing]) - (gapV * dirU[outgoing])) / cross;

            outU[i] = baseU[incoming] + (dirU[incoming] * t);
            outV[i] = baseV[incoming] + (dirV[incoming] * t);
        }

        Polyline result = new();
        result.Reserve(count + (closed ? 1 : 0));

        for (int i = 0; i < count; i++)
        {
            result.AddPoint(PlaneOps.PointAt(plane, outU[i], outV[i]));
        }

        if (closed)
        {
            result.AddPoint(result.Points[0]);
        }

        offset = result;

        return folded == 0
            ? OperationResult.Success
            : OperationResult.Partial(
                $"{folded} corner(s) fold back on themselves, so their offset segments never meet and the " +
                "corner was moved along its own bisector instead.");
    }

    private static double SegmentLength(double[] u, double[] v, int segment, int count)
    {
        int a = segment;
        int b = (segment + 1) % count;
        double du = u[b] - u[a];
        double dv = v[b] - v[a];
        return Math.Sqrt((du * du) + (dv * dv));
    }

    /// <summary>
    /// <see langword="true"/> when every point is within <paramref name="tolerance"/> of the corresponding
    /// point of the other polyline.
    /// </summary>
    /// <remarks>
    /// Order-sensitive: a polyline and its reverse are not epsilon-equal even though they draw the same line.
    /// </remarks>
    public static bool EpsilonEquals(Polyline a, Polyline b, double tolerance = Tolerance.Distance)
    {
        if (a.PointCount != b.PointCount)
        {
            return false;
        }

        for (int i = 0; i < a.PointCount; i++)
        {
            if (!PointOps.EpsilonEquals(a.Points[i], b.Points[i], tolerance))
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireTwoPoints(Polyline polyline)
    {
        if (polyline.PointCount < 2)
        {
            throw new ArgumentException(
                $"This needs at least 2 points, but the polyline holds {polyline.PointCount}.",
                nameof(polyline));
        }
    }
}
