using System.Diagnostics.CodeAnalysis;

namespace Phenome.Geometry.Modules;

/// <summary>
/// Everything you can do with an <see cref="Arc"/>.
/// </summary>
/// <remarks>
/// Two kinds of parameter appear here, as they do on <see cref="Polyline"/>. An <em>angle</em> is in radians
/// measured from the arc plane's X axis, the same convention as <see cref="CircleOps"/>; a
/// <em>normalised</em> parameter runs 0 to 1 from the arc's start to its end, whichever way it sweeps.
/// Normalised is usually what you want, because it does not care about the direction.
/// </remarks>
public static class ArcOps
{
    /// <summary>An arc in a plane, spanning an angle domain measured from the plane's X axis.</summary>
    /// <exception cref="ArgumentException">The plane or the angle domain is unusable.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The radius is not finite and positive.</exception>
    public static Arc Create(in Plane plane, double radius, Interval angleDomain)
    {
        if (!TryCreate(plane, radius, angleDomain, out Arc? arc))
        {
            if (!double.IsFinite(radius) || radius <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(radius),
                    radius,
                    "The radius must be finite and greater than zero.");
            }

            throw new ArgumentException(
                "Cannot build an arc: the plane is invalid, or the angle domain is unset or sweeps nothing.");
        }

        return arc.Value;
    }

    /// <summary>
    /// An arc in a plane spanning an angle domain, reporting failure instead of producing an unusable arc.
    /// </summary>
    /// <param name="plane">The plane to lie in, centred on its origin.</param>
    /// <param name="radius">The radius; must be finite and greater than zero.</param>
    /// <param name="angleDomain">
    /// The angles in radians the arc spans. Decreasing sweeps clockwise; a domain wider than a full turn is
    /// kept as given rather than reduced.
    /// </param>
    /// <param name="arc">The arc, or <see langword="null"/> when the call fails.</param>
    /// <returns>
    /// <see langword="false"/> when the plane is invalid, the radius unusable, or the domain unset or of
    /// zero width.
    /// </returns>
    public static bool TryCreate(
        in Plane plane,
        double radius,
        Interval angleDomain,
        [NotNullWhen(true)] out Arc? arc)
    {
        if (!PlaneOps.IsValid(plane) ||
            !double.IsFinite(radius) ||
            radius <= 0 ||
            !IsDomainValid(angleDomain))
        {
            arc = null;
            return false;
        }

        arc = new Arc(plane, radius, angleDomain);
        return true;
    }

    /// <summary>The part of a circle covered by an angle domain.</summary>
    /// <exception cref="ArgumentException">The circle or the angle domain is unusable.</exception>
    public static Arc Create(in Circle circle, Interval angleDomain) =>
        Create(circle.Plane, circle.Radius, angleDomain);

    /// <summary>The arc through three points, running from the first to the last by way of the middle.</summary>
    /// <exception cref="ArgumentException">
    /// The points are coincident or collinear, so no arc passes through them.
    /// </exception>
    public static Arc CreateFromPoints(Point3d start, Point3d interior, Point3d end)
    {
        if (!TryCreateFromPoints(start, interior, end, out Arc? arc))
        {
            throw new ArgumentException(
                $"No arc passes through {start}, {interior} and {end}: the points are coincident or " +
                "collinear, or the ends meet.");
        }

        return arc.Value;
    }

    /// <summary>
    /// The arc through three points, reporting failure instead of producing an arc full of NaN.
    /// </summary>
    /// <remarks>
    /// The middle point does more than pin the radius: it picks which of the two arcs between the ends is
    /// meant. <see cref="CircleOps.TryCreateFromPoints"/> builds a frame with angle zero on
    /// <paramref name="start"/> sweeping towards <paramref name="interior"/>, so the domain is simply zero
    /// to the angle of <paramref name="end"/>, and the middle point is guaranteed to fall inside it.
    /// </remarks>
    /// <param name="start">Where the arc begins.</param>
    /// <param name="interior">A point the arc passes through between its ends.</param>
    /// <param name="end">Where the arc ends.</param>
    /// <param name="arc">The arc, or <see langword="null"/> when the call fails.</param>
    /// <returns>
    /// <see langword="false"/> when any point is invalid, when the three are coincident or collinear, or
    /// when the ends coincide and the sweep is therefore undefined.
    /// </returns>
    public static bool TryCreateFromPoints(
        Point3d start,
        Point3d interior,
        Point3d end,
        [NotNullWhen(true)] out Arc? arc)
    {
        arc = null;

        if (!CircleOps.TryCreateFromPoints(start, interior, end, out Circle? circle))
        {
            return false;
        }

        double endAngle = CircleOps.ClosestParameter(circle.Value, end);

        // ClosestParameter wraps into 0 to just under a full turn, so a start and end that coincide come
        // back as an angle near zero — an arc sweeping nothing rather than the full circle someone might
        // have meant. Refusing is the only honest answer, since three points cannot say which was intended.
        if (endAngle <= Tolerance.Angle)
        {
            return false;
        }

        arc = new Arc(circle.Value.Plane, circle.Value.Radius, new Interval(0, endAngle));
        return true;
    }

    /// <summary>
    /// <see langword="true"/> when the plane is valid, the radius finite and positive, and the domain
    /// sweeps a nonzero angle.
    /// </summary>
    public static bool IsValid(in Arc arc) =>
        PlaneOps.IsValid(arc.Plane) &&
        double.IsFinite(arc.Radius) &&
        arc.Radius > 0 &&
        IsDomainValid(arc.AngleDomain);

    /// <summary>The centre of curvature, which is the arc plane's origin.</summary>
    public static Point3d Center(in Arc arc) => arc.Plane.Origin;

    /// <summary>The direction perpendicular to the arc, which is its plane's normal.</summary>
    /// <remarks>
    /// This is the plane's normal regardless of which way the arc sweeps, so a clockwise arc and its
    /// reversal share a normal. Use <see cref="IsClockwise"/> when the direction of travel matters.
    /// </remarks>
    public static Vector3d Normal(in Arc arc) => arc.Plane.Normal;

    /// <summary>The full circle the arc is part of.</summary>
    public static Circle ToCircle(in Arc arc) => new(arc.Plane, arc.Radius);

    /// <summary>
    /// The signed angle in radians the arc sweeps, negative when it runs clockwise about the normal.
    /// </summary>
    public static double SweepAngle(in Arc arc) => IntervalOps.Length(arc.AngleDomain);

    /// <summary><see langword="true"/> when the arc sweeps clockwise about its plane normal.</summary>
    public static bool IsClockwise(in Arc arc) => IntervalOps.IsDecreasing(arc.AngleDomain);

    /// <summary>The distance along the arc from end to end.</summary>
    public static double Length(in Arc arc) => arc.Radius * Math.Abs(SweepAngle(arc));

    /// <summary>The straight-line distance between the ends.</summary>
    public static double ChordLength(in Arc arc) =>
        PointOps.DistanceTo(StartPoint(arc), EndPoint(arc));

    /// <summary>The straight segment between the ends, in the direction the arc runs.</summary>
    public static Line Chord(in Arc arc) => new(StartPoint(arc), EndPoint(arc));

    /// <summary>
    /// The largest distance from the arc to its chord, at the arc's midpoint.
    /// </summary>
    /// <remarks>
    /// The number that decides how finely an arc has to be tessellated, and the same quantity
    /// <see cref="SegmentCountForTolerance"/> inverts. An arc sweeping more than half a turn bulges past
    /// its own centre, which is why this is computed from the half angle rather than from the chord.
    /// </remarks>
    public static double Sagitta(in Arc arc) =>
        arc.Radius * (1 - Math.Cos(Math.Abs(SweepAngle(arc)) * 0.5));

    /// <summary>Where the arc begins, at the start of its angle domain.</summary>
    public static Point3d StartPoint(in Arc arc) => PointAt(arc, arc.AngleDomain.T0);

    /// <summary>Where the arc ends, at the end of its angle domain.</summary>
    public static Point3d EndPoint(in Arc arc) => PointAt(arc, arc.AngleDomain.T1);

    /// <summary>The point halfway along the arc.</summary>
    public static Point3d MidPoint(in Arc arc) => PointAt(arc, IntervalOps.Mid(arc.AngleDomain));

    /// <summary>The point at an angle in radians, measured from the plane's X axis.</summary>
    /// <remarks>
    /// Not clamped to the domain: an angle outside it evaluates on the circle the arc came from, which is
    /// what makes this usable for extending an arc. Use <see cref="PointAtNormalized"/> to stay on the arc.
    /// </remarks>
    public static Point3d PointAt(in Arc arc, double angleRadians)
    {
        (double sin, double cos) = Math.SinCos(angleRadians);

        return arc.Plane.Origin +
            (arc.Plane.XAxis * (cos * arc.Radius)) +
            (arc.Plane.YAxis * (sin * arc.Radius));
    }

    /// <summary>The point at a normalised parameter, where 0 is the start and 1 is the end.</summary>
    /// <remarks>Not clamped, so a parameter outside 0 to 1 continues around the circle.</remarks>
    public static Point3d PointAtNormalized(in Arc arc, double normalizedParameter) =>
        PointAt(arc, IntervalOps.ParameterAt(arc.AngleDomain, normalizedParameter));

    /// <summary>The point a given distance along the arc from its start.</summary>
    /// <remarks>
    /// Clamped to the arc, the same way <see cref="PolylineOps.PointAtLength"/> is: a distance past
    /// either end gives that end rather than continuing round the circle.
    /// </remarks>
    public static Point3d PointAtLength(in Arc arc, double length)
    {
        double total = Length(arc);

        if (total <= 0)
        {
            return StartPoint(arc);
        }

        return PointAtNormalized(arc, Math.Clamp(length / total, 0, 1));
    }

    /// <summary>
    /// The unit direction the arc travels in at an angle, pointing from the start of the arc towards its
    /// end.
    /// </summary>
    /// <remarks>
    /// Follows the sweep, so a clockwise arc gives the reverse of what the underlying circle would. That is
    /// the useful definition: the tangent at the start point of an arc should point into the arc.
    /// </remarks>
    public static Vector3d TangentAt(in Arc arc, double angleRadians)
    {
        (double sin, double cos) = Math.SinCos(angleRadians);
        Vector3d tangent = (arc.Plane.YAxis * cos) - (arc.Plane.XAxis * sin);

        return IsClockwise(arc) ? -tangent : tangent;
    }

    /// <summary>The direction the arc leaves its start point in.</summary>
    public static Vector3d StartTangent(in Arc arc) => TangentAt(arc, arc.AngleDomain.T0);

    /// <summary>The direction the arc arrives at its end point in.</summary>
    public static Vector3d EndTangent(in Arc arc) => TangentAt(arc, arc.AngleDomain.T1);

    /// <summary>The same arc traced the other way, with its ends and its tangents swapped.</summary>
    public static Arc Reversed(in Arc arc) =>
        new(arc.Plane, arc.Radius, IntervalOps.Reversed(arc.AngleDomain));

    /// <summary>
    /// The normalised parameter of the point on the arc nearest to a point in space.
    /// </summary>
    /// <remarks>
    /// Clamped into 0 to 1, so a point off the end of the arc comes back as that end. The angle the point
    /// projects to may fall outside the domain in two different ways when the arc is short — past the start
    /// or past the end — and this picks whichever end is genuinely nearer rather than whichever the wrapped
    /// angle happens to sit next to.
    /// </remarks>
    public static double ClosestParameterNormalized(in Arc arc, Point3d point)
    {
        Circle circle = ToCircle(arc);
        double angle = CircleOps.ClosestParameter(circle, point);
        double normalized = IntervalOps.NormalizedParameterAt(arc.AngleDomain, angle);

        if (double.IsNaN(normalized))
        {
            return 0;
        }

        if (normalized >= 0 && normalized <= 1)
        {
            return normalized;
        }

        // The projected angle is outside the domain, but it was wrapped into a single turn on the way out,
        // so it may be an entire turn away from where the domain sits. Shifting by whole turns brings it
        // as close as it can get; if it still falls outside, the nearer end wins.
        double sweep = SweepAngle(arc);
        double turnsInParameter = Math.Tau / sweep;

        double best = normalized;

        for (int shift = -2; shift <= 2; shift++)
        {
            double candidate = normalized + (shift * turnsInParameter);

            if (candidate >= 0 && candidate <= 1)
            {
                return candidate;
            }

            if (Math.Abs(candidate - 0.5) < Math.Abs(best - 0.5))
            {
                best = candidate;
            }
        }

        return best <= 0.5 ? 0 : 1;
    }

    /// <summary>The point on the arc nearest to a point in space.</summary>
    public static Point3d ClosestPoint(in Arc arc, Point3d point) =>
        PointAtNormalized(arc, ClosestParameterNormalized(arc, point));

    /// <summary>The distance from a point in space to the nearest point on the arc.</summary>
    public static double DistanceTo(in Arc arc, Point3d point) =>
        PointOps.DistanceTo(ClosestPoint(arc, point), point);

    /// <summary>
    /// An open polyline through points on the arc, with the given number of equal segments.
    /// </summary>
    /// <remarks>
    /// The corners sit on the arc, including both ends exactly, so the polyline is inscribed and falls
    /// inside the true arc. Use <see cref="SegmentCountForTolerance"/> to pick a count from a tolerance
    /// rather than guessing.
    /// </remarks>
    /// <param name="arc">The arc to approximate.</param>
    /// <param name="segmentCount">How many segments to use; at least one.</param>
    /// <exception cref="ArgumentException">The arc is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Fewer than one segment was asked for.</exception>
    public static Polyline ToPolyline(in Arc arc, int segmentCount)
    {
        if (!IsValid(arc))
        {
            throw new ArgumentException("Cannot tessellate an invalid arc.", nameof(arc));
        }

        Guard.AtLeast(segmentCount, 1);

        Polyline polyline = new();
        polyline.Reserve(segmentCount + 1);

        for (int i = 0; i <= segmentCount; i++)
        {
            polyline.AddPoint(PointAtNormalized(arc, (double)i / segmentCount));
        }

        return polyline;
    }

    /// <summary>
    /// The fewest segments whose inscribed polyline stays within <paramref name="maxDeviation"/> of the arc.
    /// </summary>
    /// <remarks>
    /// The same inversion <see cref="CircleOps.SegmentCountForTolerance"/> performs, scaled to the
    /// arc's own sweep instead of a full turn — so a quarter turn costs a quarter of the segments at the
    /// same tolerance, rather than the full count a circle would need.
    /// </remarks>
    /// <param name="arc">The arc to approximate.</param>
    /// <param name="maxDeviation">
    /// The largest gap allowed between polyline and arc; must be finite and greater than zero.
    /// </param>
    /// <returns>A segment count of at least one.</returns>
    /// <exception cref="ArgumentException">The arc is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The deviation is not finite and positive.</exception>
    public static int SegmentCountForTolerance(in Arc arc, double maxDeviation)
    {
        if (!IsValid(arc))
        {
            throw new ArgumentException("Cannot tessellate an invalid arc.", nameof(arc));
        }

        if (!double.IsFinite(maxDeviation) || maxDeviation <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDeviation),
                maxDeviation,
                "The deviation must be finite and greater than zero.");
        }

        if (maxDeviation >= arc.Radius)
        {
            return 1;
        }

        double halfAnglePerSegment = Math.Acos(1 - (maxDeviation / arc.Radius));
        double count = Math.Abs(SweepAngle(arc)) / (2 * halfAnglePerSegment);

        return Math.Max(1, (int)Math.Ceiling(count));
    }

    /// <summary>
    /// The arc moved by a transformation matrix, or <see cref="Arc.Unset"/> when the matrix would turn it
    /// into something that is not an arc.
    /// </summary>
    /// <remarks>See <see cref="TryTransform"/> for why this can fail.</remarks>
    public static Arc Transform(in Arc arc, in TMatrix matrix) =>
        TryTransform(arc, matrix, out Arc? transformed) ? transformed.Value : Arc.Unset;

    /// <summary>
    /// The arc moved by a transformation matrix, reporting failure when the result would not be an arc.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="CircleOps.TryTransform"/>, so it fails on exactly the same
    /// transforms: anything that is not a similarity turns the arc into an elliptical one.
    /// <para>
    /// A mirror needs care. Reflecting flips the plane's handedness, and the transformed frame comes back
    /// with a normal pointing the other way, so the domain has to be negated to keep the arc covering the
    /// same points. That is done here by re-deriving the domain from the transformed start and end rather
    /// than by inspecting the determinant, which keeps it correct for mirrors combined with rotations.
    /// </para>
    /// </remarks>
    /// <param name="arc">The arc to move.</param>
    /// <param name="matrix">The transformation to apply.</param>
    /// <param name="transformed">The moved arc, or <see langword="null"/> when the call fails.</param>
    /// <returns><see langword="false"/> when the arc is invalid, or the matrix is not a similarity.</returns>
    public static bool TryTransform(
        in Arc arc,
        in TMatrix matrix,
        [NotNullWhen(true)] out Arc? transformed)
    {
        transformed = null;

        if (!IsValid(arc) ||
            !CircleOps.TryTransform(ToCircle(arc), matrix, out Circle? circle))
        {
            return false;
        }

        // Where the ends land decides the domain. The start's angle in the new frame need not be zero,
        // because the frame was carried through the transform rather than rebuilt from the start point.
        Point3d start = PointOps.Transform(StartPoint(arc), matrix);
        Point3d end = PointOps.Transform(EndPoint(arc), matrix);
        Point3d mid = PointOps.Transform(MidPoint(arc), matrix);

        double startAngle = CircleOps.ClosestParameter(circle.Value, start);
        double endAngle = CircleOps.ClosestParameter(circle.Value, end);
        double midAngle = CircleOps.ClosestParameter(circle.Value, mid);

        // Both candidate sweeps end at the same point; the midpoint says which way round is meant. Angles
        // are unwrapped relative to the start so the comparison is a plain ordering.
        double forward = Unwrap(endAngle - startAngle);
        double midForward = Unwrap(midAngle - startAngle);

        Interval domain = midForward <= forward
            ? new Interval(startAngle, startAngle + forward)
            : new Interval(startAngle, startAngle + forward - Math.Tau);

        if (!IsDomainValid(domain))
        {
            return false;
        }

        transformed = new Arc(circle.Value.Plane, circle.Value.Radius, domain);
        return true;

        static double Unwrap(double angle)
        {
            double wrapped = angle % Math.Tau;
            return wrapped < 0 ? wrapped + Math.Tau : wrapped;
        }
    }

    /// <summary>
    /// <see langword="true"/> when the two arcs have planes, radii and domains within
    /// <paramref name="tolerance"/> of each other.
    /// </summary>
    /// <remarks>
    /// The domain is compared with the same tolerance as the distances, which is the right thing on the
    /// scales this library works at but is worth knowing: an angle tolerance and a distance tolerance are
    /// not the same quantity.
    /// </remarks>
    public static bool EpsilonEquals(in Arc a, in Arc b, double tolerance = Tolerance.Distance) =>
        PlaneOps.EpsilonEquals(a.Plane, b.Plane, tolerance) &&
        Math.Abs(a.Radius - b.Radius) <= tolerance &&
        IntervalOps.EpsilonEquals(a.AngleDomain, b.AngleDomain, tolerance);

    private static bool IsDomainValid(Interval angleDomain) =>
        IntervalOps.IsValid(angleDomain) &&
        Math.Abs(IntervalOps.Length(angleDomain)) > Tolerance.Angle;
}
