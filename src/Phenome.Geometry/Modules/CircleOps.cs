using System.Diagnostics.CodeAnalysis;

namespace Phenome.Geometry.Modules;

/// <summary>
/// Everything you can do with a <see cref="Circle"/>.
/// </summary>
/// <remarks>
/// Angles are in radians, measured from the circle plane's X axis towards its Y axis. A radius must be
/// finite and greater than zero: a zero or negative radius is rejected rather than quietly repaired,
/// because a caller passing one has a bug upstream and silently taking the absolute value hides it.
/// </remarks>
public static class CircleOps
{
    /// <summary>A circle lying in a plane, centred on its origin.</summary>
    /// <exception cref="ArgumentException">The plane is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The radius is not finite and positive.</exception>
    public static Circle Create(in Plane plane, double radius)
    {
        if (!PlaneOps.IsValid(plane))
        {
            throw new ArgumentException("Cannot build a circle in an invalid plane.", nameof(plane));
        }

        ThrowIfRadiusInvalid(radius);
        return new Circle(plane, radius);
    }

    /// <summary>
    /// A circle lying in a plane, reporting failure instead of producing an unusable circle.
    /// </summary>
    /// <param name="plane">The plane to lie in, centred on its origin.</param>
    /// <param name="radius">The radius; must be finite and greater than zero.</param>
    /// <param name="circle">The circle, or <see langword="null"/> when the call fails.</param>
    /// <returns><see langword="false"/> when the plane is invalid or the radius is not usable.</returns>
    public static bool TryCreate(in Plane plane, double radius, [NotNullWhen(true)] out Circle? circle)
    {
        if (!PlaneOps.IsValid(plane) || !IsRadiusValid(radius))
        {
            circle = null;
            return false;
        }

        circle = new Circle(plane, radius);
        return true;
    }

    /// <summary>A circle around a centre, in the plane the normal is perpendicular to.</summary>
    /// <remarks>
    /// A normal does not pin down where angle zero sits, so the in-plane axes come from
    /// <see cref="PlaneOps.CreateFromNormal"/>. Build the plane yourself when the start point matters.
    /// </remarks>
    /// <exception cref="ArgumentException">The centre or normal is degenerate.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The radius is not finite and positive.</exception>
    public static Circle Create(Point3d center, Vector3d normal, double radius)
    {
        ThrowIfRadiusInvalid(radius);
        return new Circle(PlaneOps.CreateFromNormal(center, normal), radius);
    }

    /// <summary>
    /// A circle around a centre with a given normal, reporting failure instead of producing an unusable
    /// circle.
    /// </summary>
    /// <param name="center">The centre of the circle.</param>
    /// <param name="normal">The direction perpendicular to the circle; need not be normalised.</param>
    /// <param name="radius">The radius; must be finite and greater than zero.</param>
    /// <param name="circle">The circle, or <see langword="null"/> when the call fails.</param>
    /// <returns><see langword="false"/> when the centre or normal is degenerate, or the radius unusable.</returns>
    public static bool TryCreate(
        Point3d center,
        Vector3d normal,
        double radius,
        [NotNullWhen(true)] out Circle? circle)
    {
        circle = null;

        if (!IsRadiusValid(radius) ||
            !PlaneOps.TryCreateFromNormal(center, normal, out Plane? plane))
        {
            return false;
        }

        circle = new Circle(plane.Value, radius);
        return true;
    }

    /// <summary>The circle through three points.</summary>
    /// <exception cref="ArgumentException">
    /// The three points are coincident or collinear, so no circle passes through them.
    /// </exception>
    public static Circle CreateFromPoints(Point3d a, Point3d b, Point3d c)
    {
        if (!TryCreateFromPoints(a, b, c, out Circle? circle))
        {
            throw new ArgumentException(
                $"No circle passes through {a}, {b} and {c}: the points are coincident or collinear.");
        }

        return circle.Value;
    }

    /// <summary>
    /// The circle through three points, reporting failure instead of producing a circle full of NaN.
    /// </summary>
    /// <remarks>
    /// The resulting frame puts angle zero at <paramref name="a"/> and sweeps towards <paramref name="b"/>,
    /// so the three points appear in order as the angle increases. That is worth relying on: it is what
    /// makes <see cref="ArcOps.TryCreateFromPoints"/> able to cut the right arc out of the circle.
    /// </remarks>
    /// <param name="a">The first point, which ends up at angle zero.</param>
    /// <param name="b">The second point, which the sweep heads towards.</param>
    /// <param name="c">The third point.</param>
    /// <param name="circle">The circle, or <see langword="null"/> when the call fails.</param>
    /// <returns>
    /// <see langword="false"/> when any point is invalid, or when the three are coincident or collinear.
    /// </returns>
    public static bool TryCreateFromPoints(
        Point3d a,
        Point3d b,
        Point3d c,
        [NotNullWhen(true)] out Circle? circle)
    {
        circle = null;

        if (!PointOps.IsValid(a) || !PointOps.IsValid(b) || !PointOps.IsValid(c))
        {
            return false;
        }

        Vector3d ab = b - a;
        Vector3d ac = c - a;
        Vector3d normal = VectorOps.Cross(ab, ac);
        double normalLengthSquared = VectorOps.LengthSquared(normal);

        // The cross product vanishes exactly when the points are collinear, coincident points included.
        // Comparing its squared length against the squared lengths of the sides makes the test scale
        // invariant, so a tiny triangle is not mistaken for a degenerate one and a huge nearly straight
        // one is not mistaken for a valid one.
        double scale = VectorOps.LengthSquared(ab) * VectorOps.LengthSquared(ac);

        if (scale == 0 || normalLengthSquared <= Tolerance.ZeroSquared * scale)
        {
            return false;
        }

        // The circumcentre offset from a, from the standard construction: each side contributes a step
        // perpendicular to itself, weighted by the opposite side's squared length.
        Vector3d toCenter =
            ((VectorOps.Cross(normal, ab) * VectorOps.LengthSquared(ac)) +
             (VectorOps.Cross(ac, normal) * VectorOps.LengthSquared(ab))) /
            (2 * normalLengthSquared);

        Point3d center = a + toCenter;
        double radius = VectorOps.Length(toCenter);

        if (!IsRadiusValid(radius))
        {
            return false;
        }

        // X towards a puts angle zero on the first point; Y towards b fixes the sweep direction, and
        // Gram-Schmidt inside TryCreateFromAxes makes it perpendicular without moving the plane.
        if (!PlaneOps.TryCreateFromAxes(center, a - center, b - center, out Plane? plane))
        {
            return false;
        }

        circle = new Circle(plane.Value, radius);
        return true;
    }

    /// <summary>
    /// The circle inscribed in the corner between two segments meeting at a point, touching both.
    /// </summary>
    /// <remarks>
    /// The building block for filleting a corner: the circle is tangent to both legs at
    /// <paramref name="radius"/> back from the corner along each. It fails when the corner is straight or
    /// folded back on itself, and when either leg is shorter than the tangent distance the fillet needs —
    /// which is the case worth catching, because that is where a fillet would run past the end of its own
    /// leg and produce a self-crossing outline.
    /// </remarks>
    /// <param name="previous">The point the first leg comes from.</param>
    /// <param name="corner">Where the two legs meet.</param>
    /// <param name="next">The point the second leg goes to.</param>
    /// <param name="radius">The fillet radius; must be finite and greater than zero.</param>
    /// <param name="circle">
    /// The circle, oriented so that its plane normal is the corner's normal, or <see langword="null"/> when
    /// the call fails.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the corner is degenerate, straight, fully folded, or too tight for the
    /// radius given the leg lengths.
    /// </returns>
    public static bool TryCreateInCorner(
        Point3d previous,
        Point3d corner,
        Point3d next,
        double radius,
        [NotNullWhen(true)] out Circle? circle)
    {
        circle = null;

        if (!IsRadiusValid(radius) ||
            !VectorOps.TryNormalize(previous - corner, out Vector3d? towardsPrevious) ||
            !VectorOps.TryNormalize(next - corner, out Vector3d? towardsNext))
        {
            return false;
        }

        Vector3d u = towardsPrevious.Value;
        Vector3d v = towardsNext.Value;

        // Half the corner angle decides how far back along each leg the tangent point sits. atan2 of the
        // cross and dot magnitudes keeps this exact near both extremes, where acos of the dot loses all
        // its precision.
        double angle = VectorOps.AngleBetween(u, v);

        if (angle <= Tolerance.Angle || angle >= Math.PI - Tolerance.Angle)
        {
            return false;
        }

        double halfAngle = angle * 0.5;
        double tangentDistance = radius / Math.Tan(halfAngle);

        if (tangentDistance > PointOps.DistanceTo(corner, previous) ||
            tangentDistance > PointOps.DistanceTo(corner, next))
        {
            return false;
        }

        // The centre sits along the corner bisector, at the hypotenuse of the tangent triangle.
        if (!VectorOps.TryNormalize(u + v, out Vector3d? bisector))
        {
            return false;
        }

        Point3d center = corner + (bisector.Value * (radius / Math.Sin(halfAngle)));

        return TryCreate(center, VectorOps.Cross(u, v), radius, out circle);
    }

    /// <summary><see langword="true"/> when the plane is valid and the radius finite and positive.</summary>
    public static bool IsValid(in Circle circle) =>
        PlaneOps.IsValid(circle.Plane) && IsRadiusValid(circle.Radius);

    /// <summary>The centre of the circle, which is its plane's origin.</summary>
    public static Point3d Center(in Circle circle) => circle.Plane.Origin;

    /// <summary>The direction perpendicular to the circle, which is its plane's normal.</summary>
    public static Vector3d Normal(in Circle circle) => circle.Plane.Normal;

    /// <summary>Twice the radius.</summary>
    public static double Diameter(in Circle circle) => circle.Radius * 2;

    /// <summary>The distance all the way around.</summary>
    public static double Circumference(in Circle circle) => Math.Tau * circle.Radius;

    /// <summary>The area the circle encloses.</summary>
    public static double Area(in Circle circle) => Math.PI * circle.Radius * circle.Radius;

    /// <summary>The point at an angle in radians, measured from the plane's X axis.</summary>
    /// <remarks>Not wrapped: any angle evaluates, and angles a full turn apart give the same point.</remarks>
    public static Point3d PointAt(in Circle circle, double angleRadians)
    {
        (double sin, double cos) = Math.SinCos(angleRadians);

        return circle.Plane.Origin +
            (circle.Plane.XAxis * (cos * circle.Radius)) +
            (circle.Plane.YAxis * (sin * circle.Radius));
    }

    /// <summary>The point at a normalised parameter, where 0 and 1 are both the start point.</summary>
    public static Point3d PointAtNormalized(in Circle circle, double normalizedParameter) =>
        PointAt(circle, normalizedParameter * Math.Tau);

    /// <summary>
    /// The unit direction the circle runs in at an angle, pointing the way the angle increases.
    /// </summary>
    public static Vector3d TangentAt(in Circle circle, double angleRadians)
    {
        (double sin, double cos) = Math.SinCos(angleRadians);
        return (circle.Plane.YAxis * cos) - (circle.Plane.XAxis * sin);
    }

    /// <summary>The angle in radians of the point on the circle nearest to a point in space.</summary>
    /// <remarks>
    /// The result is wrapped into 0 to just under a full turn. A point on the circle's axis is equidistant
    /// from every point on it, so there is no nearest one; that case returns zero, which is a genuine point
    /// at the right distance rather than a wrong answer, but nothing about it is special otherwise.
    /// </remarks>
    public static double ClosestParameter(in Circle circle, Point3d point)
    {
        (double u, double v) = PlaneOps.ClosestParameter(circle.Plane, point);

        if (Math.Abs(u) <= Tolerance.Zero && Math.Abs(v) <= Tolerance.Zero)
        {
            return 0;
        }

        double angle = Math.Atan2(v, u);
        return angle < 0 ? angle + Math.Tau : angle;
    }

    /// <summary>The point on the circle nearest to a point in space.</summary>
    /// <remarks>See <see cref="ClosestParameter"/> for what happens on the circle's axis.</remarks>
    public static Point3d ClosestPoint(in Circle circle, Point3d point) =>
        PointAt(circle, ClosestParameter(circle, point));

    /// <summary>The distance from a point in space to the nearest point on the circle.</summary>
    public static double DistanceTo(in Circle circle, Point3d point) =>
        PointOps.DistanceTo(ClosestPoint(circle, point), point);

    /// <summary>
    /// The same circle traced the other way, keeping its start point and reversing its normal.
    /// </summary>
    public static Circle Reversed(in Circle circle) =>
        new(PlaneOps.Flipped(circle.Plane), circle.Radius);

    /// <summary>The circle as a full-turn arc.</summary>
    public static Arc ToArc(in Circle circle) => new(circle.Plane, circle.Radius, Interval.FullTurn);

    /// <summary>The part of the circle covered by an angle domain.</summary>
    public static Arc ToArc(in Circle circle, Interval angleDomain) =>
        new(circle.Plane, circle.Radius, angleDomain);

    /// <summary>
    /// A closed polyline through points on the circle, with the given number of equal segments.
    /// </summary>
    /// <remarks>
    /// The corners sit on the circle, so the polygon is inscribed and every part of it falls inside the
    /// true circle by up to <c>radius * (1 - cos(pi / segmentCount))</c>. That matters when the outline is
    /// a hole to be cut: the hole comes out slightly small. Use
    /// <see cref="SegmentCountForTolerance"/> to pick a count that keeps the error under something you can
    /// live with.
    /// <para>
    /// The first point is repeated at the end, so the result has <paramref name="segmentCount"/> + 1
    /// points and satisfies <see cref="PolylineOps.IsClosed"/>. The repeated point is copied from
    /// the first rather than recomputed from the angle, so it closes exactly rather than to within a
    /// rounding error.
    /// </para>
    /// </remarks>
    /// <param name="circle">The circle to approximate.</param>
    /// <param name="segmentCount">How many segments to use; at least three.</param>
    /// <exception cref="ArgumentException">The circle is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Fewer than three segments were asked for.</exception>
    public static Polyline ToPolyline(in Circle circle, int segmentCount)
    {
        if (!IsValid(circle))
        {
            throw new ArgumentException("Cannot tessellate an invalid circle.", nameof(circle));
        }

        Guard.AtLeast(segmentCount, 3);

        Polyline polyline = new();
        polyline.Reserve(segmentCount + 1);

        double step = Math.Tau / segmentCount;
        Point3d first = PointAt(circle, 0);
        polyline.AddPoint(first);

        for (int i = 1; i < segmentCount; i++)
        {
            polyline.AddPoint(PointAt(circle, i * step));
        }

        polyline.AddPoint(first);
        return polyline;
    }

    /// <summary>
    /// The fewest segments whose inscribed polygon stays within <paramref name="maxDeviation"/> of a circle
    /// of the given radius.
    /// </summary>
    /// <remarks>
    /// Inverts the sagitta of one segment, <c>radius * (1 - cos(pi / n))</c>. The count grows as the square
    /// root of the tolerance, so halving the allowed error costs about 40% more segments rather than double
    /// — which is why picking a count by tolerance beats guessing a round number.
    /// </remarks>
    /// <param name="radius">The circle radius; must be finite and greater than zero.</param>
    /// <param name="maxDeviation">
    /// The largest gap allowed between polygon and circle; must be finite and greater than zero.
    /// </param>
    /// <returns>A segment count of at least three.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The radius or the deviation is not finite and positive.
    /// </exception>
    public static int SegmentCountForTolerance(double radius, double maxDeviation)
    {
        ThrowIfRadiusInvalid(radius);

        if (!double.IsFinite(maxDeviation) || maxDeviation <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDeviation),
                maxDeviation,
                "The deviation must be finite and greater than zero.");
        }

        // A deviation of a radius or more is satisfied by any polygon at all, and would send acos out of
        // its domain.
        if (maxDeviation >= radius)
        {
            return 3;
        }

        double count = Math.PI / Math.Acos(1 - (maxDeviation / radius));
        return Math.Max(3, (int)Math.Ceiling(count));
    }

    /// <summary>
    /// The circle moved by a transformation matrix, or <see cref="Circle.Unset"/> when the matrix would
    /// turn it into something that is not a circle.
    /// </summary>
    /// <remarks>See <see cref="TryTransform"/> for why this can fail.</remarks>
    public static Circle Transform(in Circle circle, in TMatrix matrix) =>
        TryTransform(circle, matrix, out Circle? transformed) ? transformed.Value : Circle.Unset;

    /// <summary>
    /// The circle moved by a transformation matrix, reporting failure when the result would not be a
    /// circle.
    /// </summary>
    /// <remarks>
    /// A circle only survives a similarity — a rigid motion with a uniform scale. Under a non-uniform scale
    /// or a shear it becomes an ellipse, which this library has no type for, so the honest answer is
    /// failure rather than a circle of some averaged radius that no longer passes through the transformed
    /// points. The test is on the transformed axes: they must stay perpendicular and pick up the same
    /// scale factor.
    /// </remarks>
    /// <param name="circle">The circle to move.</param>
    /// <param name="matrix">The transformation to apply.</param>
    /// <param name="transformed">The moved circle, or <see langword="null"/> when the call fails.</param>
    /// <returns>
    /// <see langword="false"/> when the circle is invalid, or the matrix is not a similarity.
    /// </returns>
    public static bool TryTransform(
        in Circle circle,
        in TMatrix matrix,
        [NotNullWhen(true)] out Circle? transformed)
    {
        transformed = null;

        if (!IsValid(circle))
        {
            return false;
        }

        Vector3d x = VectorOps.Transform(circle.Plane.XAxis, matrix);
        Vector3d y = VectorOps.Transform(circle.Plane.YAxis, matrix);

        double xLength = VectorOps.Length(x);
        double yLength = VectorOps.Length(y);

        if (xLength <= Tolerance.Zero || yLength <= Tolerance.Zero)
        {
            return false;
        }

        // Relative comparisons, so that the test behaves the same on a millimetre circle and a kilometre
        // one.
        if (Math.Abs(xLength - yLength) > Tolerance.Distance * Math.Max(xLength, yLength))
        {
            return false;
        }

        if (Math.Abs(VectorOps.Dot(x, y)) > Tolerance.Angle * xLength * yLength)
        {
            return false;
        }

        Point3d origin = PointOps.Transform(circle.Plane.Origin, matrix);

        if (!PlaneOps.TryCreateFromAxes(origin, x, y, out Plane? plane))
        {
            return false;
        }

        double radius = circle.Radius * xLength;

        if (!IsRadiusValid(radius))
        {
            return false;
        }

        transformed = new Circle(plane.Value, radius);
        return true;
    }

    /// <summary>
    /// <see langword="true"/> when the two circles have planes and radii within
    /// <paramref name="tolerance"/> of each other.
    /// </summary>
    public static bool EpsilonEquals(in Circle a, in Circle b, double tolerance = Tolerance.Distance) =>
        PlaneOps.EpsilonEquals(a.Plane, b.Plane, tolerance) &&
        Math.Abs(a.Radius - b.Radius) <= tolerance;

    private static bool IsRadiusValid(double radius) => double.IsFinite(radius) && radius > 0;

    private static void ThrowIfRadiusInvalid(double radius)
    {
        if (!IsRadiusValid(radius))
        {
            throw new ArgumentOutOfRangeException(
                nameof(radius),
                radius,
                "The radius must be finite and greater than zero.");
        }
    }
}
