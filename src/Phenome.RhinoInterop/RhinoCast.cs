using System.Diagnostics.CodeAnalysis;
using Phenome.Geometry;
using RG = Rhino.Geometry;
using Vector2f = System.Numerics.Vector2;
using Vector3f = System.Numerics.Vector3;

namespace Phenome.RhinoInterop;

/// <summary>
/// Conversions between the kernel's types and RhinoCommon's.
/// </summary>
/// <remarks>
/// The edge of the test rig, and the only place in the solution that knows both vocabularies. A wire carries the
/// kernel's own value; RhinoCommon appears when Grasshopper needs to draw something, or when a Rhino component
/// upstream hands over geometry that has to be brought in. Nothing computes here — a conversion that quietly
/// improved the geometry on the way through would make the rig lie about what the kernel does.
/// <para>
/// Both directions are lossy in ways worth knowing. A mesh with faces of five corners or more is triangulated on
/// the way out, because Rhino meshes hold triangles and quads; the kernel's n-gons are not preserved. Coming in,
/// a Rhino mesh brings its quads across as quads, so a round trip is not the identity and is not meant to be.
/// </para>
/// </remarks>
public static class RhinoCast
{
    /// <summary>The kernel's types this can convert, which is what a wire may carry.</summary>
    /// <remarks>
    /// Read by the component factory to decide whether a parameter gets a Rhino-aware wire or a plain one, so
    /// adding a conversion below is the whole of adding support for a type.
    /// </remarks>
    public static readonly IReadOnlyList<Type> Convertible =
    [
        typeof(Point3d),
        typeof(Vector3d),
        typeof(Plane),
        typeof(Line),
        typeof(Interval),
        typeof(Circle),
        typeof(Arc),
        typeof(BoundingBox),
        typeof(Polyline),
        typeof(Mesh),
        typeof(TMatrix),
        typeof(Color32),
    ];

    /// <summary>Converts a kernel value to the RhinoCommon type that stands for it.</summary>
    /// <param name="value">The value to convert.</param>
    /// <param name="converted">The RhinoCommon value, or <see langword="null"/> when there is no equivalent.</param>
    /// <returns><see langword="true"/> when a conversion exists.</returns>
    public static bool TryToRhino(object? value, [NotNullWhen(true)] out object? converted)
    {
        converted = value switch
        {
            Point3d point => ToRhino(point),
            Vector3d vector => ToRhino(vector),
            Plane plane => ToRhino(plane),
            Line line => ToRhino(line),
            Interval interval => ToRhino(interval),
            Circle circle => ToRhino(circle),
            Arc arc => ToRhino(arc),
            BoundingBox box => ToRhino(box),
            Polyline polyline => ToRhino(polyline),
            Mesh mesh => ToRhino(mesh),
            TMatrix matrix => ToRhino(matrix),
            Color32 colour => ToRhino(colour),
            _ => null,
        };

        return converted is not null;
    }

    /// <summary>Converts a RhinoCommon value to the kernel type asked for.</summary>
    /// <param name="value">The RhinoCommon value.</param>
    /// <param name="wanted">The kernel type wanted.</param>
    /// <param name="converted">The kernel value, or <see langword="null"/> when there is no conversion.</param>
    /// <returns><see langword="true"/> when a conversion exists.</returns>
    public static bool TryFromRhino(object? value, Type wanted, [NotNullWhen(true)] out object? converted)
    {
        converted = null;

        if (value is null)
        {
            return false;
        }

        // Already ours, which happens whenever one Phenome component feeds another.
        if (wanted.IsInstanceOfType(value))
        {
            converted = value;
            return true;
        }

        converted = (value, wanted) switch
        {
            (RG.Point3d point, _) when wanted == typeof(Point3d) => FromRhino(point),
            (RG.Point3f point, _) when wanted == typeof(Point3d) => FromRhino(new RG.Point3d(point)),
            (RG.Vector3d vector, _) when wanted == typeof(Vector3d) => FromRhino(vector),
            (RG.Plane plane, _) when wanted == typeof(Plane) => FromRhino(plane),
            (RG.Line line, _) when wanted == typeof(Line) => FromRhino(line),
            (RG.Interval interval, _) when wanted == typeof(Interval) => FromRhino(interval),
            (RG.Circle circle, _) when wanted == typeof(Circle) => FromRhino(circle),
            (RG.Arc arc, _) when wanted == typeof(Arc) => FromRhino(arc),
            (RG.BoundingBox box, _) when wanted == typeof(BoundingBox) => FromRhino(box),
            (RG.Polyline polyline, _) when wanted == typeof(Polyline) => FromRhino(polyline),
            (RG.Curve curve, _) when wanted == typeof(Polyline) => FromRhino(curve),
            (RG.Mesh mesh, _) when wanted == typeof(Mesh) => FromRhino(mesh),
            (RG.Transform matrix, _) when wanted == typeof(TMatrix) => FromRhino(matrix),
            (System.Drawing.Color colour, _) when wanted == typeof(Color32) => FromRhino(colour),
            _ => null,
        };

        return converted is not null;
    }

    /// <summary>A point, as Rhino spells it.</summary>
    /// <param name="point">The point.</param>
    public static RG.Point3d ToRhino(Point3d point) => new(point.X, point.Y, point.Z);

    /// <summary>A point, as the kernel spells it.</summary>
    /// <param name="point">The point.</param>
    public static Point3d FromRhino(RG.Point3d point) => PointOps.Create(point.X, point.Y, point.Z);

    /// <summary>A vector, as Rhino spells it.</summary>
    /// <param name="vector">The vector.</param>
    public static RG.Vector3d ToRhino(Vector3d vector) => new(vector.X, vector.Y, vector.Z);

    /// <summary>A vector, as the kernel spells it.</summary>
    /// <param name="vector">The vector.</param>
    public static Vector3d FromRhino(RG.Vector3d vector) => VectorOps.Create(vector.X, vector.Y, vector.Z);

    /// <summary>An interval, as Rhino spells it.</summary>
    /// <param name="interval">The interval.</param>
    public static RG.Interval ToRhino(Interval interval) => new(interval.T0, interval.T1);

    /// <summary>An interval, as the kernel spells it.</summary>
    /// <param name="interval">The interval.</param>
    public static Interval FromRhino(RG.Interval interval) => IntervalOps.Create(interval.T0, interval.T1);

    /// <summary>A line, as Rhino spells it.</summary>
    /// <param name="line">The line.</param>
    public static RG.Line ToRhino(Line line) => new(ToRhino(line.From), ToRhino(line.To));

    /// <summary>A line, as the kernel spells it.</summary>
    /// <param name="line">The line.</param>
    public static Line FromRhino(RG.Line line) => LineOps.Create(FromRhino(line.From), FromRhino(line.To));

    /// <summary>
    /// A plane, as Rhino spells it.
    /// </summary>
    /// <remarks>
    /// Built from origin and both axes rather than from a normal, so the frame arrives as it was rather than as
    /// Rhino would have chosen it. A plane's X axis is not decoration: it is where a circle starts and which way
    /// it sweeps.
    /// </remarks>
    /// <param name="plane">The plane.</param>
    public static RG.Plane ToRhino(in Plane plane) =>
        new(ToRhino(plane.Origin), ToRhino(plane.XAxis), ToRhino(plane.YAxis));

    /// <summary>A plane, as the kernel spells it.</summary>
    /// <param name="plane">The plane.</param>
    public static Plane FromRhino(RG.Plane plane) => PlaneOps.CreateFromAxes(
        FromRhino(plane.Origin),
        FromRhino(plane.XAxis),
        FromRhino(plane.YAxis));

    /// <summary>A circle, as Rhino spells it.</summary>
    /// <param name="circle">The circle.</param>
    public static RG.Circle ToRhino(in Circle circle) => new(ToRhino(circle.Plane), circle.Radius);

    /// <summary>A circle, as the kernel spells it.</summary>
    /// <param name="circle">The circle.</param>
    public static Circle FromRhino(RG.Circle circle) => CircleOps.Create(FromRhino(circle.Plane), circle.Radius);

    /// <summary>An arc, as Rhino spells it.</summary>
    /// <param name="arc">The arc.</param>
    public static RG.Arc ToRhino(in Arc arc) =>
        new(ToRhino(arc.Plane), arc.Radius, arc.AngleDomain.T1 - arc.AngleDomain.T0)
        {
            AngleDomain = ToRhino(arc.AngleDomain),
        };

    /// <summary>An arc, as the kernel spells it.</summary>
    /// <param name="arc">The arc.</param>
    public static Arc FromRhino(RG.Arc arc) => ArcOps.Create(
        FromRhino(arc.Plane),
        arc.Radius,
        FromRhino(arc.AngleDomain));

    /// <summary>A bounding box, as Rhino spells it.</summary>
    /// <param name="box">The box.</param>
    public static RG.BoundingBox ToRhino(in BoundingBox box) => new(
        box.X.T0,
        box.Y.T0,
        box.Z.T0,
        box.X.T1,
        box.Y.T1,
        box.Z.T1);

    /// <summary>A bounding box, as the kernel spells it.</summary>
    /// <param name="box">The box.</param>
    public static BoundingBox FromRhino(RG.BoundingBox box) => BoundingBoxOps.Create(
        IntervalOps.Create(box.Min.X, box.Max.X),
        IntervalOps.Create(box.Min.Y, box.Max.Y),
        IntervalOps.Create(box.Min.Z, box.Max.Z));

    /// <summary>A polyline, as Rhino spells it.</summary>
    /// <param name="polyline">The polyline.</param>
    public static RG.Polyline ToRhino(Polyline polyline)
    {
        RG.Polyline converted = new(polyline.PointCount);
        ReadOnlySpan<Point3d> points = polyline.Points;

        for (int i = 0; i < points.Length; i++)
        {
            converted.Add(ToRhino(points[i]));
        }

        return converted;
    }

    /// <summary>A polyline, as the kernel spells it.</summary>
    /// <param name="polyline">The polyline.</param>
    public static Polyline FromRhino(RG.Polyline polyline)
    {
        Point3d[] points = new Point3d[polyline.Count];

        for (int i = 0; i < points.Length; i++)
        {
            points[i] = FromRhino(polyline[i]);
        }

        return PolylineOps.Create(points);
    }

    /// <summary>
    /// A curve brought in as a polyline, or <see langword="null"/> when it is not one.
    /// </summary>
    /// <remarks>
    /// Refused rather than sampled. A NURBS curve turned into a polyline behind the author's back would put a
    /// tolerance nobody chose into the middle of a definition, and the divide that ought to be a visible step
    /// would be invisible.
    /// </remarks>
    /// <param name="curve">The curve.</param>
    public static Polyline? FromRhino(RG.Curve curve) =>
        curve.TryGetPolyline(out RG.Polyline? polyline) ? FromRhino(polyline) : null;

    /// <summary>A transform, as Rhino spells it.</summary>
    /// <param name="matrix">The matrix.</param>
    public static RG.Transform ToRhino(in TMatrix matrix)
    {
        RG.Transform converted = default;

        converted.M00 = matrix.M11;
        converted.M01 = matrix.M12;
        converted.M02 = matrix.M13;
        converted.M03 = matrix.M14;

        converted.M10 = matrix.M21;
        converted.M11 = matrix.M22;
        converted.M12 = matrix.M23;
        converted.M13 = matrix.M24;

        converted.M20 = matrix.M31;
        converted.M21 = matrix.M32;
        converted.M22 = matrix.M33;
        converted.M23 = matrix.M34;

        converted.M30 = matrix.M41;
        converted.M31 = matrix.M42;
        converted.M32 = matrix.M43;
        converted.M33 = matrix.M44;

        return converted;
    }

    /// <summary>A transform, as the kernel spells it.</summary>
    /// <param name="matrix">The matrix.</param>
    public static TMatrix FromRhino(RG.Transform matrix) => Transforms.CreateFromRowMajor(
    [
        matrix.M00, matrix.M01, matrix.M02, matrix.M03,
        matrix.M10, matrix.M11, matrix.M12, matrix.M13,
        matrix.M20, matrix.M21, matrix.M22, matrix.M23,
        matrix.M30, matrix.M31, matrix.M32, matrix.M33,
    ]);

    /// <summary>A colour, as the drawing library spells it.</summary>
    /// <param name="colour">The colour.</param>
    public static System.Drawing.Color ToRhino(Color32 colour) =>
        System.Drawing.Color.FromArgb(colour.A, colour.R, colour.G, colour.B);

    /// <summary>A colour, as the kernel spells it.</summary>
    /// <param name="colour">The colour.</param>
    public static Color32 FromRhino(System.Drawing.Color colour) =>
        ColorOps.Create(colour.R, colour.G, colour.B, colour.A);

    /// <summary>
    /// A mesh, as Rhino spells it.
    /// </summary>
    /// <remarks>
    /// Faces of five corners or more are triangulated on the way out, because a Rhino mesh face is a triangle or
    /// a quad. Vertex normals and colours come across when they are there; texture coordinates too. Face groups
    /// have no equivalent and are dropped, which is worth knowing before reading anything into a round trip.
    /// </remarks>
    /// <param name="mesh">The mesh.</param>
    public static RG.Mesh ToRhino(Mesh mesh)
    {
        Mesh source = mesh;

        if (NeedsTriangulating(mesh))
        {
            MeshOps.Triangulate(mesh, out Mesh? triangulated);

            if (triangulated is not null)
            {
                source = triangulated;
            }
        }

        RG.Mesh converted = new();
        ReadOnlySpan<Point3d> vertices = source.Vertices;

        for (int i = 0; i < vertices.Length; i++)
        {
            converted.Vertices.Add(vertices[i].X, vertices[i].Y, vertices[i].Z);
        }

        for (int face = 0; face < source.FaceCount; face++)
        {
            ReadOnlySpan<int> corners = source.Face(face);

            if (corners.Length == 3)
            {
                converted.Faces.AddFace(corners[0], corners[1], corners[2]);
            }
            else if (corners.Length == 4)
            {
                converted.Faces.AddFace(corners[0], corners[1], corners[2], corners[3]);
            }
            else
            {
                // Only reachable when triangulation failed, and a fan is better than dropping the face: it is
                // wrong for a concave outline and visible, rather than absent and silent.
                for (int corner = 2; corner < corners.Length; corner++)
                {
                    converted.Faces.AddFace(corners[0], corners[corner - 1], corners[corner]);
                }
            }
        }

        if (source.HasNormals)
        {
            ReadOnlySpan<Vector3f> normals = source.Normals;

            for (int i = 0; i < normals.Length; i++)
            {
                converted.Normals.Add(normals[i].X, normals[i].Y, normals[i].Z);
            }
        }

        if (source.HasVertexColors)
        {
            ReadOnlySpan<Color32> colours = source.VertexColors;

            for (int i = 0; i < colours.Length; i++)
            {
                converted.VertexColors.Add(ToRhino(colours[i]));
            }
        }

        if (source.HasTextureCoordinates)
        {
            ReadOnlySpan<Vector2f> coordinates = source.TextureCoordinates;

            for (int i = 0; i < coordinates.Length; i++)
            {
                converted.TextureCoordinates.Add(coordinates[i].X, coordinates[i].Y);
            }
        }

        return converted;
    }

    /// <summary>A mesh, as the kernel spells it.</summary>
    /// <param name="mesh">The mesh.</param>
    public static Mesh FromRhino(RG.Mesh mesh)
    {
        Point3d[] vertices = new Point3d[mesh.Vertices.Count];

        for (int i = 0; i < vertices.Length; i++)
        {
            RG.Point3d vertex = mesh.Vertices[i];
            vertices[i] = PointOps.Create(vertex.X, vertex.Y, vertex.Z);
        }

        List<int[]> faces = new(mesh.Faces.Count);

        for (int i = 0; i < mesh.Faces.Count; i++)
        {
            RG.MeshFace face = mesh.Faces[i];

            faces.Add(face.IsQuad ? [face.A, face.B, face.C, face.D] : [face.A, face.B, face.C]);
        }

        return MeshOps.Create(vertices, faces);
    }

    private static bool NeedsTriangulating(Mesh mesh)
    {
        for (int face = 0; face < mesh.FaceCount; face++)
        {
            if (mesh.CornersInFace(face) > 4)
            {
                return true;
            }
        }

        return false;
    }
}
