using System.Diagnostics.CodeAnalysis;

namespace Phenome.Geometry.Modules;

/// <summary>
/// Everything you can do with a <see cref="BoundingBox"/>.
/// </summary>
/// <remarks>
/// Two jobs, and they pull in different directions. As a measurement — how big is this, where is its middle,
/// does it sit flush with that — a bounding box is exact and is the tool that keeps assembly from turning
/// into a column of hand-added offsets. As a rejection test — is this anywhere near the camera, could these
/// two possibly touch — it is deliberately loose, and the whole point is that it answers in a handful of
/// comparisons.
/// <para>
/// What it is never is a shape. A box around a diagonal rod is mostly empty, and
/// <see cref="Overlaps"/> saying yes means only that a real test is worth running.
/// </para>
/// </remarks>
public static class BoundingBoxOps
{
    /// <summary>A box spanning three ranges, whichever way round they were given.</summary>
    /// <remarks>
    /// The ranges are sorted increasing, because a box has no direction. That is not information being
    /// thrown away: use an <see cref="Interval"/> directly when the order carries meaning.
    /// </remarks>
    public static BoundingBox Create(Interval x, Interval y, Interval z) => new(
        IntervalOps.Increasing(x),
        IntervalOps.Increasing(y),
        IntervalOps.Increasing(z));

    /// <summary>The smallest box containing two corners.</summary>
    public static BoundingBox Create(Point3d a, Point3d b) => new(
        IntervalOps.CreateFromSorted(a.X, b.X),
        IntervalOps.CreateFromSorted(a.Y, b.Y),
        IntervalOps.CreateFromSorted(a.Z, b.Z));

    /// <summary>A box of the given size centred on a point.</summary>
    /// <remarks>The sign of each extent is ignored, since a box has no direction.</remarks>
    public static BoundingBox CreateFromCenter(Point3d center, double width, double depth, double height) => new(
        IntervalOps.CreateFromCenter(center.X, width * 0.5),
        IntervalOps.CreateFromCenter(center.Y, depth * 0.5),
        IntervalOps.CreateFromCenter(center.Z, height * 0.5));

    /// <summary>The smallest box containing every point, or unset when there are none.</summary>
    public static BoundingBox Bound(ReadOnlySpan<Point3d> points)
    {
        BoundingBox box = BoundingBox.Unset;

        foreach (Point3d point in points)
        {
            box = Grown(box, point);
        }

        return box;
    }

    /// <summary>The smallest box containing every vertex of a mesh, or unset when it has none.</summary>
    /// <remarks>
    /// Bounds the vertices, not the faces, so a vertex no face uses still counts. That is the cheaper answer
    /// and the safer one for culling; drop unused vertices first if it matters.
    /// </remarks>
    public static BoundingBox Bound(Mesh mesh) =>
        Bound(mesh.Vertices);

    /// <summary>The smallest box containing every point of a polyline, or unset when it has none.</summary>
    public static BoundingBox Bound(Polyline polyline) =>
        Bound(polyline.Points);

    /// <summary>The smallest box containing every box given, or unset when there are none.</summary>
    public static BoundingBox Bound(ReadOnlySpan<BoundingBox> boxes)
    {
        BoundingBox total = BoundingBox.Unset;

        foreach (BoundingBox box in boxes)
        {
            total = Union(total, box);
        }

        return total;
    }

    /// <summary>The smallest box containing a circle.</summary>
    /// <remarks>
    /// Exact, not the box of the enclosing sphere. The extent along each axis is the radius scaled by how
    /// much of the circle's plane leans that way, which for an axis-aligned circle collapses to zero on the
    /// normal's axis as it should.
    /// </remarks>
    public static BoundingBox Bound(in Circle circle)
    {
        if (!CircleOps.IsValid(circle))
        {
            return BoundingBox.Unset;
        }

        // The circle's extent along an axis is the length of that axis projected into the circle's plane,
        // times the radius — the standard result for the shadow of a circle.
        Vector3d x = circle.Plane.XAxis;
        Vector3d y = circle.Plane.YAxis;
        Point3d center = circle.Plane.Origin;

        double spreadX = circle.Radius * Math.Sqrt((x.X * x.X) + (y.X * y.X));
        double spreadY = circle.Radius * Math.Sqrt((x.Y * x.Y) + (y.Y * y.Y));
        double spreadZ = circle.Radius * Math.Sqrt((x.Z * x.Z) + (y.Z * y.Z));

        return new BoundingBox(
            IntervalOps.CreateFromCenter(center.X, spreadX),
            IntervalOps.CreateFromCenter(center.Y, spreadY),
            IntervalOps.CreateFromCenter(center.Z, spreadZ));
    }

    /// <summary><see langword="true"/> when all three ranges are finite.</summary>
    /// <remarks>
    /// A flat box is valid: the bounding box of a planar mesh has one range of zero width, and that is the
    /// right answer rather than a failure. Use <see cref="IsDegenerate"/> to ask about volume.
    /// </remarks>
    public static bool IsValid(in BoundingBox box) =>
        IntervalOps.IsValid(box.X) && IntervalOps.IsValid(box.Y) && IntervalOps.IsValid(box.Z);

    /// <summary><see langword="true"/> when the box has no volume, because some range is flat.</summary>
    public static bool IsDegenerate(in BoundingBox box, double tolerance = Tolerance.Distance) =>
        !IsValid(box) ||
        IntervalOps.Length(box.X) <= tolerance ||
        IntervalOps.Length(box.Y) <= tolerance ||
        IntervalOps.Length(box.Z) <= tolerance;

    /// <summary>The corner with the smallest coordinate on every axis.</summary>
    public static Point3d Min(in BoundingBox box) => new(box.X.T0, box.Y.T0, box.Z.T0);

    /// <summary>The corner with the largest coordinate on every axis.</summary>
    public static Point3d Max(in BoundingBox box) => new(box.X.T1, box.Y.T1, box.Z.T1);

    /// <summary>The point in the middle of the box.</summary>
    public static Point3d Center(in BoundingBox box) => new(
        IntervalOps.Mid(box.X),
        IntervalOps.Mid(box.Y),
        IntervalOps.Mid(box.Z));

    /// <summary>The extent along each axis, as a vector from <see cref="Min"/> to <see cref="Max"/>.</summary>
    public static Vector3d Size(in BoundingBox box) => new(
        IntervalOps.Length(box.X),
        IntervalOps.Length(box.Y),
        IntervalOps.Length(box.Z));

    /// <summary>The length of the box's main diagonal.</summary>
    public static double DiagonalLength(in BoundingBox box) => VectorOps.Length(Size(box));

    /// <summary>The volume the box encloses, which is zero when it is flat.</summary>
    public static double Volume(in BoundingBox box)
    {
        Vector3d size = Size(box);
        return size.X * size.Y * size.Z;
    }

    /// <summary>The area of the box's six sides.</summary>
    public static double SurfaceArea(in BoundingBox box)
    {
        Vector3d size = Size(box);
        return 2 * ((size.X * size.Y) + (size.Y * size.Z) + (size.Z * size.X));
    }

    /// <summary>The point at normalised coordinates, where 0 is the minimum and 1 the maximum on each axis.</summary>
    /// <remarks>
    /// Not clamped, so values outside 0 to 1 land outside the box. Handy for placing something relative to a
    /// part without knowing its size: the middle of the top face is <c>(0.5, 0.5, 1)</c>.
    /// </remarks>
    public static Point3d PointAt(in BoundingBox box, double u, double v, double w) => new(
        IntervalOps.ParameterAt(box.X, u),
        IntervalOps.ParameterAt(box.Y, v),
        IntervalOps.ParameterAt(box.Z, w));

    /// <summary>The eight corners, in the order <see cref="MeshBuilders.CreateBox(Interval, Interval, Interval)"/> uses.</summary>
    /// <remarks>
    /// Bottom face first, counter-clockwise seen from above, then the top face the same way — so corners 0
    /// to 3 and 4 to 7 pair up along Z.
    /// </remarks>
    public static Point3d[] Corners(in BoundingBox box)
    {
        double x0 = box.X.T0, x1 = box.X.T1;
        double y0 = box.Y.T0, y1 = box.Y.T1;
        double z0 = box.Z.T0, z1 = box.Z.T1;

        return
        [
            new Point3d(x0, y0, z0),
            new Point3d(x1, y0, z0),
            new Point3d(x1, y1, z0),
            new Point3d(x0, y1, z0),
            new Point3d(x0, y0, z1),
            new Point3d(x1, y0, z1),
            new Point3d(x1, y1, z1),
            new Point3d(x0, y1, z1),
        ];
    }

    /// <summary><see langword="true"/> when a point is inside the box or on its surface.</summary>
    /// <param name="box">The box to test against.</param>
    /// <param name="point">The point to test.</param>
    /// <param name="includeSurface">
    /// Whether a point exactly on the box counts as inside. Defaults to <see langword="true"/>, which is what
    /// a flat box needs — with the surface excluded, nothing at all is inside one.
    /// </param>
    public static bool Contains(in BoundingBox box, Point3d point, bool includeSurface = true) =>
        IntervalOps.Includes(box.X, point.X, includeSurface) &&
        IntervalOps.Includes(box.Y, point.Y, includeSurface) &&
        IntervalOps.Includes(box.Z, point.Z, includeSurface);

    /// <summary><see langword="true"/> when the other box lies entirely within this one.</summary>
    /// <remarks>A box contains itself.</remarks>
    public static bool Contains(in BoundingBox box, in BoundingBox other) =>
        IsValid(box) && IsValid(other) &&
        other.X.T0 >= box.X.T0 && other.X.T1 <= box.X.T1 &&
        other.Y.T0 >= box.Y.T0 && other.Y.T1 <= box.Y.T1 &&
        other.Z.T0 >= box.Z.T0 && other.Z.T1 <= box.Z.T1;

    /// <summary><see langword="true"/> when the two boxes share at least one point.</summary>
    /// <remarks>
    /// Touching counts. Remember what this does and does not say: two boxes overlapping means the geometry
    /// inside them might touch, and is worth a real test. Two boxes not overlapping means it definitely does
    /// not, and that is the answer worth having.
    /// </remarks>
    public static bool Overlaps(in BoundingBox a, in BoundingBox b) =>
        IsValid(a) && IsValid(b) &&
        IntervalOps.Overlaps(a.X, b.X) &&
        IntervalOps.Overlaps(a.Y, b.Y) &&
        IntervalOps.Overlaps(a.Z, b.Z);

    /// <summary>The box covered by both, or <see langword="null"/> when they are disjoint.</summary>
    /// <param name="a">The first box.</param>
    /// <param name="b">The second box.</param>
    /// <param name="intersection">
    /// The shared box on success, <see langword="null"/> otherwise. Two boxes touching on a face succeed and
    /// give a flat box.
    /// </param>
    /// <returns><see langword="true"/> when the boxes overlap.</returns>
    public static bool TryIntersection(
        in BoundingBox a,
        in BoundingBox b,
        [NotNullWhen(true)] out BoundingBox? intersection)
    {
        if (!Overlaps(a, b))
        {
            intersection = null;
            return false;
        }

        intersection = new BoundingBox(
            new Interval(Math.Max(a.X.T0, b.X.T0), Math.Min(a.X.T1, b.X.T1)),
            new Interval(Math.Max(a.Y.T0, b.Y.T0), Math.Min(a.Y.T1, b.Y.T1)),
            new Interval(Math.Max(a.Z.T0, b.Z.T0), Math.Min(a.Z.T1, b.Z.T1)));

        return true;
    }

    /// <summary>The smallest box containing both.</summary>
    /// <remarks>An unset box contributes nothing, so it acts as the identity.</remarks>
    public static BoundingBox Union(in BoundingBox a, in BoundingBox b)
    {
        if (!IsValid(a))
        {
            return b;
        }

        if (!IsValid(b))
        {
            return a;
        }

        return new BoundingBox(
            IntervalOps.Union(a.X, b.X),
            IntervalOps.Union(a.Y, b.Y),
            IntervalOps.Union(a.Z, b.Z));
    }

    /// <summary>The box grown just enough to contain a point.</summary>
    /// <remarks>Growing an unset box gives the box of that single point, which is what makes accumulation work.</remarks>
    public static BoundingBox Grown(in BoundingBox box, Point3d point)
    {
        if (!PointOps.IsValid(point))
        {
            return box;
        }

        if (!IsValid(box))
        {
            return new BoundingBox(
                new Interval(point.X, point.X),
                new Interval(point.Y, point.Y),
                new Interval(point.Z, point.Z));
        }

        return new BoundingBox(
            IntervalOps.Grown(box.X, point.X),
            IntervalOps.Grown(box.Y, point.Y),
            IntervalOps.Grown(box.Z, point.Z));
    }

    /// <summary>The box moved outwards by the same amount on every side.</summary>
    /// <remarks>
    /// A negative amount shrinks it, and shrinking past the middle collapses the box onto its own centre
    /// rather than turning it inside out — the range that would have inverted becomes flat there instead.
    /// </remarks>
    public static BoundingBox Inflated(in BoundingBox box, double amount)
    {
        if (!IsValid(box) || !double.IsFinite(amount))
        {
            return box;
        }

        return new BoundingBox(
            InflateRange(box.X, amount),
            InflateRange(box.Y, amount),
            InflateRange(box.Z, amount));
    }

    /// <summary>The point in or on the box nearest to a point in space.</summary>
    /// <remarks>A point already inside comes back unchanged, so this is not a projection onto the surface.</remarks>
    public static Point3d ClosestPoint(in BoundingBox box, Point3d point)
    {
        if (!IsValid(box))
        {
            return Point3d.Unset;
        }

        return new Point3d(
            IntervalOps.Clamped(box.X, point.X),
            IntervalOps.Clamped(box.Y, point.Y),
            IntervalOps.Clamped(box.Z, point.Z));
    }

    /// <summary>The distance from a point to the box, zero for a point inside it.</summary>
    public static double DistanceTo(in BoundingBox box, Point3d point) =>
        PointOps.DistanceTo(ClosestPoint(box, point), point);

    /// <summary>
    /// The box around the eight transformed corners.
    /// </summary>
    /// <remarks>
    /// Grows under rotation, and there is no way round that: a box rotated forty-five degrees does not fit in
    /// a box of the same size, so the answer contains the transformed box but is bigger than what it bounds.
    /// Repeated transforming compounds it, so transform the geometry and bound that again rather than
    /// carrying a box through a chain of matrices.
    /// </remarks>
    public static BoundingBox Transform(in BoundingBox box, in TMatrix matrix)
    {
        if (!IsValid(box))
        {
            return BoundingBox.Unset;
        }

        Point3d[] corners = Corners(box);

        for (int i = 0; i < corners.Length; i++)
        {
            corners[i] = PointOps.Transform(corners[i], matrix);
        }

        return Bound(corners);
    }

    /// <summary>
    /// <see langword="true"/> when the two boxes have all three ranges within <paramref name="tolerance"/>
    /// of each other.
    /// </summary>
    public static bool EpsilonEquals(
        in BoundingBox a,
        in BoundingBox b,
        double tolerance = Tolerance.Distance) =>
        IntervalOps.EpsilonEquals(a.X, b.X, tolerance) &&
        IntervalOps.EpsilonEquals(a.Y, b.Y, tolerance) &&
        IntervalOps.EpsilonEquals(a.Z, b.Z, tolerance);

    private static Interval InflateRange(Interval range, double amount)
    {
        double low = range.T0 - amount;
        double high = range.T1 + amount;

        if (low <= high)
        {
            return new Interval(low, high);
        }

        // Shrunk past the middle. Collapsing onto the centre is the honest answer; an inverted range would
        // claim the box still holds something.
        double middle = IntervalOps.Mid(range);
        return new Interval(middle, middle);
    }
}
