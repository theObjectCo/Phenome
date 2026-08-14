using System.Diagnostics.CodeAnalysis;
namespace Phenome.Geometry.Modules;

/// <summary>
/// Builds meshes from scratch.
/// </summary>
/// <remarks>
/// Everything here produces geometry at the world origin. Placing it is a separate concern: compose with
/// <see cref="Transforms"/> and <see cref="MeshOps.Transform"/>. That keeps each builder to the parameters that
/// describe the shape, rather than doubling them with position and orientation.
/// <para>
/// A box centred on the origin, for instance, is the box plus one translation:
/// <c>MeshOps.Transform(box, Transforms.Translate(-w / 2, -d / 2, -h / 2))</c>.
/// </para>
/// <para>
/// Which point of the shape sits at the origin follows from the shape rather than from one rule. A box, a grid
/// and a pyramid have a corner there and grow into the positive octant, because a rectangular part is placed by
/// its corner. Anything with an axis of rotation — a prism, a cylinder, a cone — is centred on that axis with
/// its base on the XY plane, because that is what makes rotating or mirroring it about its own centre one
/// transform instead of three. A sphere is centred outright, having no base to stand on.
/// </para>
/// </remarks>
public static partial class MeshBuilders
{
    /// <summary>
    /// A closed box with one corner at the origin, extending along the positive axes.
    /// </summary>
    /// <remarks>
    /// Six quad faces, not twelve triangles: the mesh keeps n-gons, so a flat side stays one face until
    /// something has a reason to split it. All six are wound so their normals point outwards.
    /// </remarks>
    /// <param name="width">Extent along X.</param>
    /// <param name="depth">Extent along Y.</param>
    /// <param name="height">Extent along Z.</param>
    /// <exception cref="ArgumentOutOfRangeException">Any extent is zero or negative.</exception>
    public static Mesh CreateBox(double width, double depth, double height)
    {
        Guard.Positive(width);
        Guard.Positive(depth);
        Guard.Positive(height);

        Mesh mesh = new();
        mesh.Reserve(8, 6, 24);

        mesh.AddVertices(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(width, 0, 0),
            PointOps.Create(width, depth, 0),
            PointOps.Create(0, depth, 0),
            PointOps.Create(0, 0, height),
            PointOps.Create(width, 0, height),
            PointOps.Create(width, depth, height),
            PointOps.Create(0, depth, height),
        ]);

        mesh.AddFace(0, 3, 2, 1); // bottom, normal -Z
        mesh.AddFace(4, 5, 6, 7); // top,    normal +Z
        mesh.AddFace(0, 1, 5, 4); // front,  normal -Y
        mesh.AddFace(1, 2, 6, 5); // right,  normal +X
        mesh.AddFace(2, 3, 7, 6); // back,   normal +Y
        mesh.AddFace(3, 0, 4, 7); // left,   normal -X

        return mesh;
    }

    /// <summary>
    /// A flat rectangular grid of quads in the XY plane, with one corner at the origin.
    /// </summary>
    /// <remarks>
    /// Useful as a subdivided surface to deform, and as the honest way to exercise the mesh at scale: a
    /// thousand by a thousand is a million quads.
    /// </remarks>
    /// <param name="columns">How many cells along X.</param>
    /// <param name="rows">How many cells along Y.</param>
    /// <param name="cellWidth">Size of one cell along X.</param>
    /// <param name="cellDepth">Size of one cell along Y.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A count or a cell size is zero or negative.
    /// </exception>
    public static Mesh CreateGrid(int columns, int rows, double cellWidth, double cellDepth)
    {
        Guard.Positive(columns);
        Guard.Positive(rows);
        Guard.Positive(cellWidth);
        Guard.Positive(cellDepth);

        int acrossX = columns + 1;
        int acrossY = rows + 1;

        Mesh mesh = new();
        mesh.Reserve(acrossX * acrossY, columns * rows, columns * rows * 4);

        for (int row = 0; row < acrossY; row++)
        {
            for (int column = 0; column < acrossX; column++)
            {
                mesh.AddVertex(PointOps.Create(column * cellWidth, row * cellDepth, 0));
            }
        }

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int corner = (row * acrossX) + column;

                // Wound counter-clockwise seen from +Z, so every normal points up.
                mesh.AddFace(corner, corner + 1, corner + acrossX + 1, corner + acrossX);
            }
        }

        return mesh;
    }

    /// <summary>
    /// A box filling an axis-aligned range.
    /// </summary>
    /// <remarks>
    /// The workhorse for anything built out of boards: a carcass side, a shelf, a drawer front, a handle.
    /// Specifying a part by the space it occupies rather than by a size plus a translation is what stops
    /// assembly turning into a column of hand-added offsets.
    /// </remarks>
    /// <param name="x">The range along X.</param>
    /// <param name="y">The range along Y.</param>
    /// <param name="z">The range along Z.</param>
    /// <exception cref="ArgumentException">A range is unset, or collapses to zero thickness.</exception>
    public static Mesh CreateBox(Interval x, Interval y, Interval z) => CreateBox(Plane.WorldXY, x, y, z);

    /// <summary>A box filling a bounding box.</summary>
    /// <remarks>
    /// The other direction from <see cref="BoundingBoxOps.Bound(Mesh)"/>, and useful for exactly what it
    /// sounds like: showing a part's extents, or turning a measured range back into geometry.
    /// </remarks>
    /// <param name="box">The range to fill.</param>
    /// <exception cref="ArgumentException">
    /// The box is unset, or flat on some axis so the result would have no volume.
    /// </exception>
    public static Mesh CreateBox(in BoundingBox box) => CreateBox(Plane.WorldXY, box.X, box.Y, box.Z);

    /// <summary>
    /// A box filling a range measured in a plane's own axes.
    /// </summary>
    /// <remarks>
    /// The oriented version, and the reason a tilted part costs nothing extra: put the plane along the part
    /// and give its extents, rather than building it upright and rotating it into place. A splayed leg is
    /// one plane and three ranges.
    /// <para>
    /// All six faces are wound so their normals point outwards, whichever way round the ranges are given —
    /// a decreasing range describes the same box as its increasing twin, and neither turns the box
    /// inside out.
    /// </para>
    /// </remarks>
    /// <param name="plane">The frame the ranges are measured in.</param>
    /// <param name="x">The range along the plane's X axis.</param>
    /// <param name="y">The range along the plane's Y axis.</param>
    /// <param name="z">The range along the plane's normal.</param>
    /// <exception cref="ArgumentException">
    /// The plane is invalid, a range is unset, or a range collapses to zero thickness.
    /// </exception>
    public static Mesh CreateBox(in Plane plane, Interval x, Interval y, Interval z)
    {
        if (!PlaneOps.IsValid(plane))
        {
            throw new ArgumentException("Cannot build a box in an invalid plane.", nameof(plane));
        }

        ThrowIfRangeUnusable(x, nameof(x));
        ThrowIfRangeUnusable(y, nameof(y));
        ThrowIfRangeUnusable(z, nameof(z));

        // Sorting the ranges is what keeps the winding right regardless of how they were given; a box has no
        // direction of its own, so there is nothing to lose by it.
        double x0 = IntervalOps.Min(x), x1 = IntervalOps.Max(x);
        double y0 = IntervalOps.Min(y), y1 = IntervalOps.Max(y);
        double z0 = IntervalOps.Min(z), z1 = IntervalOps.Max(z);

        Mesh mesh = new();
        mesh.Reserve(8, 6, 24);

        mesh.AddVertices(
        [
            At(plane, x0, y0, z0),
            At(plane, x1, y0, z0),
            At(plane, x1, y1, z0),
            At(plane, x0, y1, z0),
            At(plane, x0, y0, z1),
            At(plane, x1, y0, z1),
            At(plane, x1, y1, z1),
            At(plane, x0, y1, z1),
        ]);

        mesh.AddFace(0, 3, 2, 1);
        mesh.AddFace(4, 5, 6, 7);
        mesh.AddFace(0, 1, 5, 4);
        mesh.AddFace(1, 2, 6, 5);
        mesh.AddFace(2, 3, 7, 6);
        mesh.AddFace(3, 0, 4, 7);

        return mesh;
    }

    /// <summary>
    /// A regular polygon prism standing on the XY plane, centred on the Z axis.
    /// </summary>
    /// <remarks>
    /// Two rings of vertices and one quad per side, with the caps left as single n-gon faces. The corners of
    /// the base ring sit on a circle of <paramref name="radius"/>, so the flats are inside it — a hexagon of
    /// radius 10 measures 20 across its corners and 17.3 across its flats, which is the distinction worth
    /// knowing before cutting anything to fit.
    /// <para>
    /// Centred on the axis rather than in the positive octant, unlike <see cref="CreateBox(double, double, double)"/>.
    /// Anything with an axis of rotation is anchored on it: that is what makes stacking, mirroring and
    /// rotating such a part about its own centre one transform rather than three.
    /// </para>
    /// </remarks>
    /// <param name="sides">How many flats. Three or more.</param>
    /// <param name="radius">Distance from the axis to a corner.</param>
    /// <param name="height">Extent along Z, from the base upwards.</param>
    /// <param name="capped">Whether to close the two ends.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sides"/> is under three, or a size is zero or negative.
    /// </exception>
    public static Mesh CreatePrism(int sides, double radius, double height, bool capped = true)
    {
        Guard.AtLeast(sides, 3);
        Guard.Positive(radius);
        Guard.Positive(height);

        Mesh mesh = new();
        mesh.Reserve(sides * 2, sides + 2, (sides * 4) + (sides * 2));

        // Counter-clockwise seen from +Z, so the winding rules below follow from one convention.
        for (int i = 0; i < sides; i++)
        {
            double angle = Math.Tau * i / sides;

            mesh.AddVertex(PointOps.Create(radius * Math.Cos(angle), radius * Math.Sin(angle), 0));
        }

        for (int i = 0; i < sides; i++)
        {
            double angle = Math.Tau * i / sides;

            mesh.AddVertex(PointOps.Create(radius * Math.Cos(angle), radius * Math.Sin(angle), height));
        }

        for (int i = 0; i < sides; i++)
        {
            int next = (i + 1) % sides;

            // Up the near edge and back down the far one, which points the normal away from the axis.
            mesh.AddFace(i, next, next + sides, i + sides);
        }

        if (capped)
        {
            AddRingCap(mesh, 0, sides, downwards: true);
            AddRingCap(mesh, sides, sides, downwards: false);
        }

        return mesh;
    }

    /// <summary>
    /// A cylinder standing on the XY plane, centred on the Z axis.
    /// </summary>
    /// <remarks>
    /// The same mesh a <see cref="CreatePrism"/> of that many sides is — a cylinder has no exact mesh, and the two
    /// differ in what they are for rather than in what they produce. Both are here because a prism of six
    /// sides is a hexagon on purpose, while a cylinder of sixty is a circle within a tolerance, and
    /// <see cref="CircleOps.SegmentCountForTolerance"/> is how that tolerance becomes a number.
    /// <para>
    /// The corners lie on the radius and the flats fall inside it, so an approximated cylinder is always
    /// slightly under size. For a hole that has to clear a shaft, ask for the segment count from the
    /// deviation you can afford.
    /// </para>
    /// </remarks>
    /// <param name="radius">Radius of the axis-aligned circle the corners sit on.</param>
    /// <param name="height">Extent along Z, from the base upwards.</param>
    /// <param name="segments">How many facets around. Three or more.</param>
    /// <param name="capped">Whether to close the two ends.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="segments"/> is under three, or a size is zero or negative.
    /// </exception>
    public static Mesh CreateCylinder(double radius, double height, int segments, bool capped = true) =>
        CreatePrism(segments, radius, height, capped);

    /// <summary>
    /// A cone standing on the XY plane, with its apex on the Z axis.
    /// </summary>
    /// <remarks>
    /// One ring and one apex, so every side is a triangle and they all share the tip. Sharing it means the
    /// tip is one vertex: averaging normals there produces a single direction straight up the axis, which is
    /// the wrong answer for a cone and unavoidable without splitting it. Run
    /// <see cref="MeshOps.SplitAtCreases"/> first if the shading matters.
    /// </remarks>
    /// <param name="radius">Radius of the base.</param>
    /// <param name="height">Height of the apex above the base.</param>
    /// <param name="segments">How many facets around. Three or more.</param>
    /// <param name="capped">Whether to close the base.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="segments"/> is under three, or a size is zero or negative.
    /// </exception>
    public static Mesh CreateCone(double radius, double height, int segments, bool capped = true)
    {
        Guard.AtLeast(segments, 3);
        Guard.Positive(radius);
        Guard.Positive(height);

        Mesh mesh = new();
        mesh.Reserve(segments + 1, segments + 1, (segments * 3) + segments);

        for (int i = 0; i < segments; i++)
        {
            double angle = Math.Tau * i / segments;

            mesh.AddVertex(PointOps.Create(radius * Math.Cos(angle), radius * Math.Sin(angle), 0));
        }

        int apex = mesh.AddVertex(PointOps.Create(0, 0, height));

        for (int i = 0; i < segments; i++)
        {
            // Along the base counter-clockwise and then up to the tip, which faces the normal outwards.
            mesh.AddFace(i, (i + 1) % segments, apex);
        }

        if (capped)
        {
            AddRingCap(mesh, 0, segments, downwards: true);
        }

        return mesh;
    }

    /// <summary>
    /// A pyramid on a rectangular base, with one base corner at the origin.
    /// </summary>
    /// <remarks>
    /// Anchored like <see cref="CreateBox(double, double, double)"/> rather than centred, because its base is a
    /// rectangle and not a circle: there is no axis of rotation to hang it on, and a rectangular part is
    /// placed by its corner. The apex sits over the middle of the base.
    /// </remarks>
    /// <param name="width">Extent of the base along X.</param>
    /// <param name="depth">Extent of the base along Y.</param>
    /// <param name="height">Height of the apex above the base.</param>
    /// <param name="capped">Whether to close the base.</param>
    /// <exception cref="ArgumentOutOfRangeException">Any size is zero or negative.</exception>
    public static Mesh CreatePyramid(double width, double depth, double height, bool capped = true)
    {
        Guard.Positive(width);
        Guard.Positive(depth);
        Guard.Positive(height);

        Mesh mesh = new();
        mesh.Reserve(5, 5, 16);

        mesh.AddVertices(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(width, 0, 0),
            PointOps.Create(width, depth, 0),
            PointOps.Create(0, depth, 0),
        ]);

        int apex = mesh.AddVertex(PointOps.Create(width / 2, depth / 2, height));

        mesh.AddFace(0, 1, apex);
        mesh.AddFace(1, 2, apex);
        mesh.AddFace(2, 3, apex);
        mesh.AddFace(3, 0, apex);

        if (capped)
        {
            // Reversed against the base ring, so the normal points down and out of the solid.
            mesh.AddFace(0, 3, 2, 1);
        }

        return mesh;
    }

    /// <summary>
    /// A sphere centred on the origin, divided by longitude and latitude.
    /// </summary>
    /// <remarks>
    /// Quads everywhere except the two rings at the poles, which are triangles because they meet at a point.
    /// The distribution is even in angle rather than in area, so the quads near the poles are much smaller
    /// than those at the equator — the usual bargain for a mesh that is trivial to index.
    /// <para>
    /// Centred rather than resting on the plane. A sphere has no base, and its centre is the only anchor that
    /// does not need explaining; to sit one on the XY plane, translate it up by its radius.
    /// </para>
    /// </remarks>
    /// <param name="radius">The radius.</param>
    /// <param name="segments">How many divisions around the axis. Three or more.</param>
    /// <param name="stacks">How many bands from pole to pole. Two or more.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A count is below its minimum, or the radius is zero or negative.
    /// </exception>
    public static Mesh CreateSphere(double radius, int segments, int stacks)
    {
        Guard.Positive(radius);
        Guard.AtLeast(segments, 3);
        Guard.AtLeast(stacks, 2);

        int rings = stacks - 1;

        Mesh mesh = new();
        mesh.Reserve(
            (rings * segments) + 2,
            stacks * segments,
            (2 * segments * 3) + ((stacks - 2) * segments * 4));

        int north = mesh.AddVertex(PointOps.Create(0, 0, radius));

        for (int ring = 0; ring < rings; ring++)
        {
            double polar = Math.PI * (ring + 1) / stacks;
            double z = radius * Math.Cos(polar);
            double across = radius * Math.Sin(polar);

            for (int i = 0; i < segments; i++)
            {
                double angle = Math.Tau * i / segments;

                mesh.AddVertex(PointOps.Create(across * Math.Cos(angle), across * Math.Sin(angle), z));
            }
        }

        int south = mesh.AddVertex(PointOps.Create(0, 0, -radius));

        // The first ring's vertices start straight after the north pole.
        int first = north + 1;

        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;

            mesh.AddFace(north, first + i, first + next);
            mesh.AddFace(south, first + ((rings - 1) * segments) + next, first + ((rings - 1) * segments) + i);
        }

        for (int ring = 0; ring < rings - 1; ring++)
        {
            int upper = first + (ring * segments);
            int lower = upper + segments;

            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;

                // Down the near edge and back up the far one, the same order the prism's sides use.
                mesh.AddFace(upper + i, lower + i, lower + next, upper + next);
            }
        }

        return mesh;
    }

    /// <summary>
    /// Closes a ring of consecutive vertices with a single n-gon face.
    /// </summary>
    /// <param name="mesh">The mesh to add the face to.</param>
    /// <param name="start">Index of the ring's first vertex.</param>
    /// <param name="count">How many vertices the ring has.</param>
    /// <param name="downwards">
    /// <see langword="true"/> to wind the face against the ring, which points its normal the other way.
    /// </param>
    private static void AddRingCap(Mesh mesh, int start, int count, bool downwards)
    {
        int[] corners = new int[count];

        for (int i = 0; i < count; i++)
        {
            corners[i] = start + (downwards ? count - 1 - i : i);
        }

        mesh.AddFace(corners);
    }

    private static Point3d At(in Plane plane, double x, double y, double z) =>
        plane.Origin + (plane.XAxis * x) + (plane.YAxis * y) + (plane.ZAxis * z);

    private static void ThrowIfRangeUnusable(Interval range, string name)
    {
        if (!IntervalOps.IsValid(range))
        {
            throw new ArgumentException($"The {name} range is unset or infinite.", name);
        }

        if (Math.Abs(IntervalOps.Length(range)) <= Tolerance.Distance)
        {
            throw new ArgumentException(
                $"The {name} range collapses to zero thickness, so the box would have no volume.",
                name);
        }
    }
}
