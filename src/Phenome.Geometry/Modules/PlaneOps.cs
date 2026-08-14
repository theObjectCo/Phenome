using System.Diagnostics.CodeAnalysis;

namespace Phenome.Geometry.Modules;

/// <summary>
/// Everything you can do with a <see cref="Plane"/>.
/// </summary>
/// <remarks>
/// A <see cref="Plane"/> guarantees that its axes are orthonormal and right-handed, and every way of
/// building one can fail to establish that — parallel axes, a zero normal, three collinear points. So all
/// of the factories live here, where failure has somewhere to go.
/// <para>
/// Relations between a plane and a point are here too, the plane being the richer of the two types.
/// </para>
/// </remarks>
public static class PlaneOps
{
    /// <summary><see langword="true"/> when the origin and all three axes are finite.</summary>
    /// <remarks>
    /// Orthonormality is not re-checked, because construction establishes it. The one way to break it is
    /// <see cref="Transform"/> with a shearing matrix, and that re-establishes the frame or hands back
    /// <see cref="Plane.Unset"/>.
    /// </remarks>
    public static bool IsValid(in Plane plane) =>
        PointOps.IsValid(plane.Origin) &&
        VectorOps.IsValid(plane.XAxis) &&
        VectorOps.IsValid(plane.YAxis) &&
        VectorOps.IsValid(plane.ZAxis);

    /// <summary>
    /// The same plane with its Y and Z axes reversed, so the normal points the other way while the frame
    /// stays right-handed.
    /// </summary>
    /// <remarks>
    /// The previous implementation flipped by building a rotation matrix and rotating half a turn about X.
    /// Negating two axes gets there with no trigonometry and no rounding.
    /// </remarks>
    public static Plane Flipped(in Plane plane) =>
        new(plane.Origin, plane.XAxis, -plane.YAxis, -plane.ZAxis);

    /// <summary>The point at frame coordinates <paramref name="u"/>, <paramref name="v"/>.</summary>
    public static Point3d PointAt(in Plane plane, double u, double v) =>
        plane.Origin + (plane.XAxis * u) + (plane.YAxis * v);

    /// <summary>
    /// Distance from <paramref name="point"/> to the plane, signed by which side it falls on.
    /// </summary>
    /// <remarks>
    /// Positive means the point lies on the normal side. The sign is what makes this useful for
    /// classifying geometry against a plane, so it is the primitive and <see cref="DistanceTo"/> is
    /// derived from it.
    /// </remarks>
    public static double SignedDistanceTo(in Plane plane, Point3d point) =>
        VectorOps.Dot(point - plane.Origin, plane.ZAxis);

    /// <summary>Unsigned distance from <paramref name="point"/> to the plane.</summary>
    public static double DistanceTo(in Plane plane, Point3d point) =>
        Math.Abs(SignedDistanceTo(plane, point));

    /// <summary>The point on the plane closest to <paramref name="point"/>.</summary>
    /// <remarks>
    /// Projects along the normal directly. The previous implementation built a four-term plane equation
    /// from three sampled points and divided by its own gradient length on every call.
    /// </remarks>
    public static Point3d ClosestPoint(in Plane plane, Point3d point) =>
        point - (plane.ZAxis * SignedDistanceTo(plane, point));

    /// <summary>
    /// The frame coordinates of the projection of <paramref name="point"/> onto the plane.
    /// </summary>
    /// <returns>
    /// The pair such that <see cref="PointAt"/> of them equals <see cref="ClosestPoint"/> of the same
    /// input.
    /// </returns>
    public static (double U, double V) ClosestParameter(in Plane plane, Point3d point)
    {
        Vector3d offset = point - plane.Origin;
        return (VectorOps.Dot(offset, plane.XAxis), VectorOps.Dot(offset, plane.YAxis));
    }

    /// <summary>
    /// The coefficients of the plane equation <c>Ax + By + Cz + D = 0</c>, with <c>(A, B, C)</c> the unit
    /// normal.
    /// </summary>
    /// <remarks>
    /// Because the normal is already unit length, substituting a point into this equation yields the
    /// signed distance directly, with no division.
    /// </remarks>
    public static (double A, double B, double C, double D) GetPlaneEquation(in Plane plane) =>
        (plane.ZAxis.X,
         plane.ZAxis.Y,
         plane.ZAxis.Z,
         -VectorOps.Dot(plane.Origin, plane.ZAxis));

    /// <summary>
    /// <see langword="true"/> when <paramref name="point"/> lies on the plane to within
    /// <paramref name="tolerance"/>.
    /// </summary>
    public static bool Contains(in Plane plane, Point3d point, double tolerance = Tolerance.Distance) =>
        DistanceTo(plane, point) <= tolerance;

    /// <summary>The plane moved by a transformation matrix.</summary>
    /// <remarks>
    /// A rigid transform carries the frame straight across. A matrix carrying a non-uniform scale or a
    /// shear does not preserve perpendicularity, so the transformed X and Y are re-orthonormalised and the
    /// normal rebuilt from them; the plane still passes through the transformed origin and still spans the
    /// transformed X and Y directions. If the frame collapses altogether — a scale that flattens the plane
    /// to a line, for instance — the result is <see cref="Plane.Unset"/>.
    /// </remarks>
    public static Plane Transform(in Plane plane, in TMatrix matrix)
    {
        Point3d origin = PointOps.Transform(plane.Origin, matrix);
        Vector3d x = VectorOps.Transform(plane.XAxis, matrix);
        Vector3d y = VectorOps.Transform(plane.YAxis, matrix);

        return TryCreateFromAxes(origin, x, y, out Plane? transformed) ? transformed.Value : Plane.Unset;
    }

    /// <summary>
    /// <see langword="true"/> when the origin and all three axes are within
    /// <paramref name="tolerance"/> of those of the other plane.
    /// </summary>
    public static bool EpsilonEquals(in Plane a, in Plane b, double tolerance = Tolerance.Distance) =>
        PointOps.EpsilonEquals(a.Origin, b.Origin, tolerance) &&
        VectorOps.EpsilonEquals(a.XAxis, b.XAxis, tolerance) &&
        VectorOps.EpsilonEquals(a.YAxis, b.YAxis, tolerance) &&
        VectorOps.EpsilonEquals(a.ZAxis, b.ZAxis, tolerance);

    /// <summary>A plane through <paramref name="origin"/> spanned by two directions.</summary>
    /// <exception cref="ArgumentException">
    /// The two directions are degenerate or parallel, so they span no plane.
    /// </exception>
    public static Plane CreateFromAxes(Point3d origin, Vector3d xAxis, Vector3d yAxis)
    {
        if (!TryCreateFromAxes(origin, xAxis, yAxis, out Plane? plane))
        {
            throw new ArgumentException(
                $"Cannot build a plane at {origin} from {xAxis} and {yAxis}: " +
                "the directions are degenerate or parallel.");
        }

        return plane.Value;
    }

    /// <summary>
    /// A plane through <paramref name="origin"/> spanned by two directions, reporting failure instead of
    /// producing a frame full of NaN.
    /// </summary>
    /// <remarks>
    /// <paramref name="xAxis"/> sets the first axis. <paramref name="yAxis"/> is then made perpendicular
    /// to it by subtracting its component along X, and normalised — the classic Gram-Schmidt step. This
    /// honours the side of X that <paramref name="yAxis"/> falls on, so swapping the two arguments flips
    /// the resulting normal, as it should.
    /// <para>
    /// The previous implementation discarded <paramref name="yAxis"/> entirely and used X rotated a quarter
    /// turn instead, which meant a left-handed pair produced a silently reversed normal.
    /// </para>
    /// </remarks>
    /// <param name="origin">Where to anchor the frame.</param>
    /// <param name="xAxis">The first axis; need not be normalised.</param>
    /// <param name="yAxis">
    /// A direction on the plane, not necessarily perpendicular to <paramref name="xAxis"/>.
    /// </param>
    /// <param name="plane">The plane, or <see langword="null"/> when the call fails.</param>
    /// <returns>
    /// <see langword="false"/> when either direction is degenerate, or when the two are parallel and
    /// therefore span no plane.
    /// </returns>
    public static bool TryCreateFromAxes(
        Point3d origin,
        Vector3d xAxis,
        Vector3d yAxis,
        [NotNullWhen(true)] out Plane? plane)
    {
        plane = null;

        if (!PointOps.IsValid(origin) ||
            !VectorOps.TryNormalize(xAxis, out Vector3d? normalizedX))
        {
            return false;
        }

        Vector3d x = normalizedX.Value;

        // Strip the component of Y that lies along X; whatever is left spans the plane with X.
        Vector3d perpendicular = yAxis - (x * VectorOps.Dot(yAxis, x));

        if (!VectorOps.TryNormalize(perpendicular, out Vector3d? normalizedY))
        {
            return false;
        }

        Vector3d y = normalizedY.Value;
        plane = new Plane(origin, x, y, VectorOps.Cross(x, y));
        return true;
    }

    /// <summary>A plane through <paramref name="origin"/> with the given normal.</summary>
    /// <exception cref="ArgumentException"><paramref name="normal"/> is degenerate or invalid.</exception>
    public static Plane CreateFromNormal(Point3d origin, Vector3d normal)
    {
        if (!TryCreateFromNormal(origin, normal, out Plane? plane))
        {
            throw new ArgumentException(
                $"Cannot build a plane at {origin} with normal {normal}: the normal is degenerate or invalid.",
                nameof(normal));
        }

        return plane.Value;
    }

    /// <summary>
    /// A plane through <paramref name="origin"/> with the given normal, reporting failure instead of
    /// producing a frame full of NaN.
    /// </summary>
    /// <remarks>
    /// A normal alone does not pin down the in-plane axes, so X is picked by
    /// <see cref="VectorOps.PerpendicularTo"/>, which crosses against whichever principal axis the
    /// normal is least aligned with. That stays well conditioned for every input, including normals nearly
    /// parallel to an axis — the previous implementation tested the normal against the world Z axis for
    /// exact equality, so a normal a hair off Z produced a near-degenerate frame.
    /// </remarks>
    /// <param name="origin">Where to anchor the frame.</param>
    /// <param name="normal">The plane normal; need not be normalised.</param>
    /// <param name="plane">The plane, or <see langword="null"/> when the call fails.</param>
    /// <returns><see langword="false"/> when the normal is degenerate or invalid.</returns>
    public static bool TryCreateFromNormal(
        Point3d origin,
        Vector3d normal,
        [NotNullWhen(true)] out Plane? plane)
    {
        plane = null;

        if (!PointOps.IsValid(origin) ||
            !VectorOps.TryNormalize(normal, out Vector3d? normalizedZ))
        {
            return false;
        }

        Vector3d z = normalizedZ.Value;

        if (!VectorOps.TryPerpendicularTo(z, out Vector3d? perpendicular))
        {
            return false;
        }

        Vector3d x = perpendicular.Value;

        // y = z cross x makes x cross y equal z, so the frame comes out right-handed.
        plane = new Plane(origin, x, VectorOps.Cross(z, x), z);
        return true;
    }

    /// <summary>
    /// A plane through three points, anchored at <paramref name="a"/> with X running towards
    /// <paramref name="b"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The three points are collinear or coincident, so they define no plane.
    /// </exception>
    public static Plane CreateFromPoints(Point3d a, Point3d b, Point3d c)
    {
        if (!TryCreateFromPoints(a, b, c, out Plane? plane))
        {
            throw new ArgumentException(
                $"Cannot build a plane through {a}, {b} and {c}: the points are collinear or coincident.");
        }

        return plane.Value;
    }

    /// <summary>
    /// A plane through three points, reporting failure instead of producing a frame full of NaN.
    /// </summary>
    /// <remarks>
    /// The normal follows the right-hand rule over <c>a, b, c</c>, so reversing the winding reverses the
    /// normal.
    /// </remarks>
    /// <param name="a">The origin of the frame.</param>
    /// <param name="b">Sets the direction of the X axis.</param>
    /// <param name="c">Together with the other two, fixes the plane.</param>
    /// <param name="plane">The plane, or <see langword="null"/> when the call fails.</param>
    /// <returns><see langword="false"/> when the points are collinear or coincident.</returns>
    public static bool TryCreateFromPoints(
        Point3d a,
        Point3d b,
        Point3d c,
        [NotNullWhen(true)] out Plane? plane) =>
        TryCreateFromAxes(a, b - a, c - a, out plane);

    /// <summary>The plane that best fits a set of points, in the least-squares sense.</summary>
    /// <param name="points">At least three points, not all collinear.</param>
    /// <param name="maxDeviation">
    /// The largest distance from any input point to the fitted plane, so the caller can judge whether
    /// treating the set as planar is defensible.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Fewer than three points, or the points are collinear.
    /// </exception>
    public static Plane CreateFromBestFit(ReadOnlySpan<Point3d> points, out double maxDeviation)
    {
        if (!TryCreateFromBestFit(points, out Plane? plane, out double? deviation))
        {
            throw new ArgumentException(
                $"Cannot fit a plane to {points.Length} point(s): " +
                "at least three are needed and they must not be collinear.",
                nameof(points));
        }

        maxDeviation = deviation.Value;
        return plane.Value;
    }

    /// <summary>The plane that best fits a set of points, reporting failure instead of guessing.</summary>
    /// <remarks>
    /// Builds the covariance matrix of the points about their centroid and takes the normal from whichever
    /// of its three principal 2x2 minors is largest, which is the numerically safest of the three
    /// equivalent expressions. Follows the derivation at
    /// <see href="https://www.ilikebigbits.com/2015_03_04_plane_from_points.html"/>.
    /// <para>
    /// The previous implementation accumulated the ZZ term as <c>r.Y * r.Z</c> instead of
    /// <c>r.Z * r.Z</c>, so the covariance was wrong and so was every plane it returned.
    /// </para>
    /// </remarks>
    /// <param name="points">The points to fit.</param>
    /// <param name="plane">The fitted plane, or <see langword="null"/> when the call fails.</param>
    /// <param name="maxDeviation">
    /// The largest distance from any input point to the fitted plane, or <see langword="null"/> when the
    /// call fails.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when there are fewer than three points, when any is invalid, or when they
    /// are collinear and so determine no unique plane.
    /// </returns>
    public static bool TryCreateFromBestFit(
        ReadOnlySpan<Point3d> points,
        [NotNullWhen(true)] out Plane? plane,
        [NotNullWhen(true)] out double? maxDeviation)
    {
        plane = null;
        maxDeviation = null;

        if (points.Length < 3)
        {
            return false;
        }

        Point3d centroid = PointOps.Centroid(points);

        if (!PointOps.IsValid(centroid))
        {
            return false;
        }

        double xx = 0;
        double xy = 0;
        double xz = 0;
        double yy = 0;
        double yz = 0;
        double zz = 0;

        foreach (Point3d point in points)
        {
            Vector3d r = point - centroid;

            xx += r.X * r.X;
            xy += r.X * r.Y;
            xz += r.X * r.Z;
            yy += r.Y * r.Y;
            yz += r.Y * r.Z;
            zz += r.Z * r.Z;
        }

        double detX = (yy * zz) - (yz * yz);
        double detY = (xx * zz) - (xz * xz);
        double detZ = (xx * yy) - (xy * xy);

        double detMax = Math.Max(detX, Math.Max(detY, detZ));

        // All three minors vanish when the points are collinear or coincident.
        if (detMax <= Tolerance.ZeroSquared)
        {
            return false;
        }

        Vector3d normal;

        if (detMax == detX)
        {
            normal = new Vector3d(detX, (xz * yz) - (xy * zz), (xy * yz) - (xz * yy));
        }
        else if (detMax == detY)
        {
            normal = new Vector3d((yz * xz) - (xy * zz), detY, (xy * xz) - (yz * xx));
        }
        else
        {
            normal = new Vector3d((yz * xy) - (xz * yy), (xz * xy) - (yz * xx), detZ);
        }

        if (!TryCreateFromNormal(centroid, normal, out Plane? fitted))
        {
            return false;
        }

        double worst = 0;

        foreach (Point3d point in points)
        {
            worst = Math.Max(worst, DistanceTo(fitted.Value, point));
        }

        plane = fitted;
        maxDeviation = worst;
        return true;
    }
}
