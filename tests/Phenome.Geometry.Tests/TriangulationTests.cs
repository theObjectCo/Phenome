namespace Phenome.Geometry.Tests;

public class TriangulationTests
{
    /// <summary>The area the triangles cover, which must match the region for the result to be right.</summary>
    private static double TriangleArea(ReadOnlySpan<Point3d> vertices, ReadOnlySpan<int> triangles)
    {
        double total = 0;

        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3d a = vertices[triangles[i + 1]] - vertices[triangles[i]];
            Vector3d b = vertices[triangles[i + 2]] - vertices[triangles[i]];
            total += VectorOps.Length(VectorOps.Cross(a, b)) * 0.5;
        }

        return total;
    }

    /// <summary>
    /// The signed area about a plane normal, so that a flipped triangle shows up as a cancellation.
    /// </summary>
    private static double SignedTriangleArea(
        ReadOnlySpan<Point3d> vertices,
        ReadOnlySpan<int> triangles,
        Vector3d normal)
    {
        double total = 0;

        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3d a = vertices[triangles[i + 1]] - vertices[triangles[i]];
            Vector3d b = vertices[triangles[i + 2]] - vertices[triangles[i]];
            total += VectorOps.Dot(VectorOps.Cross(a, b), normal) * 0.5;
        }

        return total;
    }

    private static Point3d[] Square(double size) =>
    [
        PointOps.Create(0, 0, 0),
        PointOps.Create(size, 0, 0),
        PointOps.Create(size, size, 0),
        PointOps.Create(0, size, 0),
    ];

    /// <summary>An L, whose inner corner is reflex and defeats fanning from the first corner.</summary>
    private static Point3d[] LShape() =>
    [
        PointOps.Create(0, 0, 0),
        PointOps.Create(3, 0, 0),
        PointOps.Create(3, 1, 0),
        PointOps.Create(1, 1, 0),
        PointOps.Create(1, 3, 0),
        PointOps.Create(0, 3, 0),
    ];

    [Fact]
    public void TriangleCount_IsTwoFewerThanTheCorners()
    {
        Assert.Equal(1, Triangulation.TriangleCount(3));
        Assert.Equal(2, Triangulation.TriangleCount(4));
        Assert.Equal(18, Triangulation.TriangleCount(20));
        Assert.Equal(0, Triangulation.TriangleCount(2));
    }

    [Fact]
    public void RegionTriangleCount_ChargesTwoExtraTrianglesPerHole()
    {
        // A square with a triangular hole: 4 + 3 corners, plus the two repeats the bridge adds, less two.
        Assert.Equal(7, Triangulation.RegionTriangleCount(4, [3]));
        Assert.Equal(2, Triangulation.RegionTriangleCount(4, []));
    }

    [Fact]
    public void TryTriangulate_SplitsASquareIntoTwoTriangles()
    {
        Point3d[] square = Square(2);

        OperationResult result = Triangulation.TryTriangulate(square, Plane.WorldXY, out int[]? triangles);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.NotNull(triangles);
        Assert.Equal(6, triangles.Length);
        Assert.Equal(4, TriangleArea(square, triangles), 12);
    }

    [Fact]
    public void TryTriangulate_CoversAConcaveOutlineWithoutSpillingOutsideIt()
    {
        // This is the case fanning from the first corner gets wrong: the L's reflex corner puts part of the
        // fan outside the shape, so the total area comes out too large.
        Point3d[] shape = LShape();

        OperationResult result = Triangulation.TryTriangulate(shape, Plane.WorldXY, out int[]? triangles);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.NotNull(triangles);
        Assert.Equal(4 * 3, triangles.Length);

        // The L is a 3x1 leg plus a 1x2 leg, so five square units.
        Assert.Equal(5, TriangleArea(shape, triangles), 12);
    }

    [Fact]
    public void TryTriangulate_WindsEveryTriangleTheSameWayAsTheOutline()
    {
        Point3d[] shape = LShape();

        Assert.True(Triangulation.TryTriangulate(shape, Plane.WorldXY, out int[]? triangles).IsSuccess);

        // Signed area equals unsigned area only if no triangle is wound backwards.
        Assert.Equal(
            TriangleArea(shape, triangles!),
            SignedTriangleArea(shape, triangles!, Vector3d.ZAxis),
            12);
    }

    [Fact]
    public void TryTriangulate_AcceptsAClockwiseOutlineAndKeepsItsWinding()
    {
        // Winding must not decide whether the call works, but it must decide the output's winding, or a mesh
        // face built from the triangles would face the wrong way.
        Point3d[] clockwise = [.. Square(2).Reverse()];

        Assert.True(Triangulation.TryTriangulate(clockwise, Plane.WorldXY, out int[]? triangles).IsSuccess);

        Assert.Equal(4, TriangleArea(clockwise, triangles!), 12);
        Assert.Equal(-4, SignedTriangleArea(clockwise, triangles!, Vector3d.ZAxis), 12);
    }

    [Fact]
    public void TryTriangulate_UsesEveryCornerOfAConvexOutline()
    {
        // A convex polygon has no corner that can be skipped, so every index must appear.
        Polyline circle = CircleOps.ToPolyline(CircleOps.Create(Plane.WorldXY, 5), 12);

        Assert.True(Triangulation.TryTriangulate(circle, Plane.WorldXY, out int[]? triangles).IsSuccess);

        HashSet<int> used = [.. triangles!];
        Assert.Equal(12, used.Count);
    }

    [Fact]
    public void TryTriangulate_DropsTheRepeatedClosingPointOfAClosedPolyline()
    {
        Polyline closed = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(2, 0, 0),
            PointOps.Create(2, 2, 0),
            PointOps.Create(0, 2, 0),
            PointOps.Create(0, 0, 0),
        ]);

        Assert.True(PolylineOps.IsClosed(closed));
        Assert.True(Triangulation.TryTriangulate(closed, Plane.WorldXY, out int[]? triangles).IsSuccess);

        // Four corners, not five, so two triangles and no zero-area extra.
        Assert.Equal(6, triangles!.Length);
        Assert.DoesNotContain(4, triangles);
    }

    [Fact]
    public void TryTriangulate_WorksOnAPlaneThatIsNotAxisAligned()
    {
        Plane tilted = PlaneOps.CreateFromNormal(
            PointOps.Create(1, -2, 3),
            VectorOps.Create(1, 2, 3));

        // Build the L in the tilted plane's own coordinates so it is genuinely planar there.
        Point3d[] flat = LShape();
        Point3d[] shape = new Point3d[flat.Length];

        for (int i = 0; i < flat.Length; i++)
        {
            shape[i] = PlaneOps.PointAt(tilted, flat[i].X, flat[i].Y);
        }

        OperationResult result = Triangulation.TryTriangulate(shape, tilted, out int[]? triangles);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(5, TriangleArea(shape, triangles!), 9);
    }

    [Fact]
    public void TryTriangulate_FitsAPlaneWhenNoneIsGiven()
    {
        Point3d[] shape = LShape();

        OperationResult result = Triangulation.TryTriangulate(shape, out int[]? triangles);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(5, TriangleArea(shape, triangles!), 9);
    }

    [Fact]
    public void TryTriangulate_FailsWithoutEnoughCorners()
    {
        OperationResult result = Triangulation.TryTriangulate(
            [PointOps.Create(0, 0, 0), PointOps.Create(1, 0, 0)],
            Plane.WorldXY,
            out int[]? triangles);

        Assert.True(result.IsFailed);
        Assert.False(result.HasOutput);
        Assert.Null(triangles);
    }

    [Fact]
    public void TryTriangulate_FailsOnANonFinitePoint()
    {
        Point3d[] shape = [.. Square(1)];
        shape[2] = Point3d.Unset;

        OperationResult result = Triangulation.TryTriangulate(shape, Plane.WorldXY, out int[]? triangles);

        Assert.True(result.IsFailed);
        Assert.Null(triangles);
        Assert.Contains("finite", result.Message);
    }

    [Fact]
    public void TryTriangulate_FailsOnAnInvalidPlane()
    {
        OperationResult result = Triangulation.TryTriangulate(Square(1), Plane.Unset, out int[]? triangles);

        Assert.True(result.IsFailed);
        Assert.Null(triangles);
    }

    [Fact]
    public void TryTriangulate_HandlesACollinearCornerInTheMiddleOfAnEdge()
    {
        // A point sitting on an edge is not an ear until a neighbour has gone, so this exercises the case
        // where the ear search has to come back round.
        Point3d[] shape =
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(2, 0, 0),
            PointOps.Create(2, 2, 0),
            PointOps.Create(0, 2, 0),
        ];

        OperationResult result = Triangulation.TryTriangulate(shape, Plane.WorldXY, out int[]? triangles);

        Assert.True(result.HasOutput);
        Assert.Equal(3 * 3, triangles!.Length);

        // The collinear corner costs a zero-area triangle but must not cost any area.
        Assert.Equal(4, TriangleArea(shape, triangles), 12);
    }

    [Fact]
    public void TryTriangulate_HandlesAnOutlineWithADuplicatedCorner()
    {
        Point3d[] shape =
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(2, 0, 0),
            PointOps.Create(2, 0, 0),
            PointOps.Create(2, 2, 0),
            PointOps.Create(0, 2, 0),
        ];

        OperationResult result = Triangulation.TryTriangulate(shape, Plane.WorldXY, out int[]? triangles);

        Assert.True(result.HasOutput);
        Assert.Equal(4, TriangleArea(shape, triangles!), 12);
    }

    [Fact]
    public void TryTriangulate_ReportsPartialForACollinearOutlineRatherThanLoopingForever()
    {
        // Zero area, no ear anywhere. The count still has to come out right so a caller's buffer is filled,
        // and the status has to say the geometry means nothing.
        Point3d[] degenerate =
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(2, 0, 0),
            PointOps.Create(3, 0, 0),
        ];

        OperationResult result = Triangulation.TryTriangulate(degenerate, Plane.WorldXY, out int[]? triangles);

        Assert.True(result.HasOutput);
        Assert.Equal(2 * 3, triangles!.Length);
        Assert.Equal(0, TriangleArea(degenerate, triangles), 12);
    }

    [Fact]
    public void TryTriangulate_FailsOnASelfCrossingOutlineRatherThanClippingItAnyway()
    {
        // A bowtie has no valid triangulation, but ear clipping does not notice: it finds an ear at every
        // step and hands back two overlapping triangles covering twice the real area, reporting success.
        // Nothing about the clipping loop can catch that, which is why the crossing is tested for up front.
        Point3d[] bowtie =
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(2, 2, 0),
            PointOps.Create(2, 0, 0),
            PointOps.Create(0, 2, 0),
        ];

        Assert.True(Triangulation.SelfIntersects(bowtie, Plane.WorldXY));

        OperationResult result = Triangulation.TryTriangulate(bowtie, Plane.WorldXY, out int[]? triangles);

        Assert.True(result.IsFailed);
        Assert.Null(triangles);
        Assert.Contains("crosses itself", result.Message);
    }

    [Fact]
    public void SelfIntersects_IgnoresTheEdgesThatShareACorner()
    {
        // Every consecutive pair of edges meets at a corner and the last meets the first; none of that is a
        // crossing, and a test that thought otherwise would reject every outline there is.
        Assert.False(Triangulation.SelfIntersects(Square(2), Plane.WorldXY));
        Assert.False(Triangulation.SelfIntersects(LShape(), Plane.WorldXY));
    }

    [Fact]
    public void SelfIntersects_DoesNotClaimToCatchTouchingOrOverlappingEdges()
    {
        // Documented limitation: only proper crossings are found. A collinear run of corners means edges
        // lying along each other, and that comes back false.
        Point3d[] collinear =
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(2, 0, 0),
            PointOps.Create(3, 0, 0),
        ];

        Assert.False(Triangulation.SelfIntersects(collinear, Plane.WorldXY));
    }

    [Fact]
    public void TryTriangulateRegion_CutsASquareHoleOutOfASquare()
    {
        Polyline outer = PolylineOps.Create(Square(10));
        Polyline hole = PolylineOps.Create(
        [
            PointOps.Create(4, 4, 0),
            PointOps.Create(6, 4, 0),
            PointOps.Create(6, 6, 0),
            PointOps.Create(4, 6, 0),
        ]);

        OperationResult result = Triangulation.TryTriangulateRegion(
            outer,
            [hole],
            Plane.WorldXY,
            out Point3d[]? vertices,
            out int[]? triangles);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.NotNull(vertices);
        Assert.NotNull(triangles);

        // 100 for the square less 4 for the hole.
        Assert.Equal(96, TriangleArea(vertices, triangles), 9);
        Assert.Equal(Triangulation.RegionTriangleCount(4, [4]) * 3, triangles.Length);
    }

    [Fact]
    public void TryTriangulateRegion_AddsNoVerticesSoTheOutputNeedsNoWelding()
    {
        // Bridging repeats corners in the traversal only. If it invented positions instead, every region
        // would come out of here needing a weld pass.
        Polyline outer = PolylineOps.Create(Square(10));
        Polyline hole = CircleOps.ToPolyline(
            CircleOps.Create(PlaneOps.CreateFromNormal(PointOps.Create(5, 5, 0), Vector3d.ZAxis), 2),
            8);

        Assert.True(Triangulation.TryTriangulateRegion(
            outer,
            [hole],
            Plane.WorldXY,
            out Point3d[]? vertices,
            out int[]? triangles).IsSuccess);

        // Four outer corners plus eight hole corners; the hole's repeated closing point is dropped.
        Assert.Equal(12, vertices!.Length);

        foreach (int index in triangles!)
        {
            Assert.InRange(index, 0, vertices.Length - 1);
        }
    }

    [Fact]
    public void TryTriangulateRegion_CutsTwoHolesLikeTheCableHolesInATabletop()
    {
        // The shape this was written for: a rectangle with two round openings.
        Polyline outer = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(160, 0, 0),
            PointOps.Create(160, 80, 0),
            PointOps.Create(0, 80, 0),
        ]);

        Circle left = CircleOps.Create(
            PlaneOps.CreateFromNormal(PointOps.Create(40, 60, 0), Vector3d.ZAxis), 4);

        Circle right = CircleOps.Create(
            PlaneOps.CreateFromNormal(PointOps.Create(120, 60, 0), Vector3d.ZAxis), 4);

        int segments = CircleOps.SegmentCountForTolerance(4, 0.05);
        Polyline leftHole = CircleOps.ToPolyline(left, segments);
        Polyline rightHole = CircleOps.ToPolyline(right, segments);

        OperationResult result = Triangulation.TryTriangulateRegion(
            outer,
            [leftHole, rightHole],
            Plane.WorldXY,
            out Point3d[]? vertices,
            out int[]? triangles);

        Assert.True(result.IsSuccess, result.ToString());

        // Measure against the areas actually removed, not against the true circles: the inscribed polygons
        // are smaller than the circles they approximate, so the region left over is correspondingly larger.
        double removed =
            Math.Abs(PolylineOps.SignedArea(leftHole, Plane.WorldXY)) +
            Math.Abs(PolylineOps.SignedArea(rightHole, Plane.WorldXY));

        Assert.Equal((160 * 80) - removed, TriangleArea(vertices!, triangles!), 9);

        // And the deficit against the true circles is the tessellation error, nothing more.
        double againstTrueCircles = (160 * 80) - (2 * CircleOps.Area(left));
        Assert.InRange(TriangleArea(vertices!, triangles!) - againstTrueCircles, 0, 2);
    }

    [Fact]
    public void TryTriangulateRegion_HandlesAHoleTouchingTheOuterOutlineAtOnePoint()
    {
        Polyline outer = PolylineOps.Create(Square(10));
        Polyline hole = PolylineOps.Create(
        [
            PointOps.Create(10, 5, 0),
            PointOps.Create(6, 3, 0),
            PointOps.Create(6, 7, 0),
        ]);

        OperationResult result = Triangulation.TryTriangulateRegion(
            outer,
            [hole],
            Plane.WorldXY,
            out Point3d[]? vertices,
            out int[]? triangles);

        Assert.True(result.HasOutput, result.ToString());
        Assert.Equal(100 - 8, TriangleArea(vertices!, triangles!), 9);
    }

    [Fact]
    public void TryTriangulateRegion_NormalisesTheWindingOfBothTheOutlineAndItsHoles()
    {
        // Neither has to be given any particular way round; a caller composing outlines from other
        // operations should not have to think about it.
        Polyline outer = PolylineOps.Create([.. Square(10).Reverse()]);
        Polyline hole = PolylineOps.Create(
        [
            PointOps.Create(4, 4, 0),
            PointOps.Create(6, 4, 0),
            PointOps.Create(6, 6, 0),
            PointOps.Create(4, 6, 0),
        ]);

        OperationResult result = Triangulation.TryTriangulateRegion(
            outer,
            [hole],
            Plane.WorldXY,
            out Point3d[]? vertices,
            out int[]? triangles);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(96, TriangleArea(vertices!, triangles!), 9);
    }

    [Fact]
    public void TryTriangulateRegion_SkipsAHoleOutsideTheOutlineAndSaysSo()
    {
        Polyline outer = PolylineOps.Create(Square(10));
        Polyline elsewhere = PolylineOps.Create(
        [
            PointOps.Create(40, 40, 0),
            PointOps.Create(42, 40, 0),
            PointOps.Create(42, 42, 0),
        ]);

        OperationResult result = Triangulation.TryTriangulateRegion(
            outer,
            [elsewhere],
            Plane.WorldXY,
            out Point3d[]? vertices,
            out int[]? triangles);

        Assert.True(result.IsPartial, result.ToString());
        Assert.Contains("outside", result.Message);

        // The outline itself still triangulates.
        Assert.Equal(100, TriangleArea(vertices!, triangles!), 9);
    }

    [Fact]
    public void TryTriangulateRegion_IgnoresAHoleWithTooFewCornersAndSaysSo()
    {
        Polyline outer = PolylineOps.Create(Square(10));
        Polyline sliver = PolylineOps.Create(
            [PointOps.Create(4, 4, 0), PointOps.Create(6, 6, 0)]);

        OperationResult result = Triangulation.TryTriangulateRegion(
            outer,
            [sliver],
            Plane.WorldXY,
            out _,
            out _);

        Assert.True(result.IsPartial);
        Assert.Contains("corners", result.Message);
    }

    [Fact]
    public void TryTriangulateRegion_WorksWithNoHolesAtAll()
    {
        OperationResult result = Triangulation.TryTriangulateRegion(
            PolylineOps.Create(LShape()),
            [],
            Plane.WorldXY,
            out Point3d[]? vertices,
            out int[]? triangles);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(5, TriangleArea(vertices!, triangles!), 12);
    }

    [Fact]
    public void TryTriangulateRegion_FailsWhenTheOuterOutlineIsTooShort()
    {
        OperationResult result = Triangulation.TryTriangulateRegion(
            PolylineOps.Create(
                [PointOps.Create(0, 0, 0), PointOps.Create(1, 0, 0)]),
            [],
            Plane.WorldXY,
            out Point3d[]? vertices,
            out int[]? triangles);

        Assert.True(result.IsFailed);
        Assert.Null(vertices);
        Assert.Null(triangles);
    }

    [Fact]
    public void SplitsOnFirstDiagonal_PicksTheShorterOne()
    {
        // A long thin quad: splitting across the short diagonal gives the better pair of triangles.
        Point3d a = PointOps.Create(0, 0, 0);
        Point3d b = PointOps.Create(10, 0, 0);
        Point3d c = PointOps.Create(10, 1, 0);
        Point3d d = PointOps.Create(0, 1, 0);

        // a-c and b-d are the same length here, so the tie goes to the first.
        Assert.True(Triangulation.SplitsOnFirstDiagonal(a, b, c, d));

        // Skew the quad so b-d becomes clearly shorter.
        Point3d skewed = PointOps.Create(9, 1, 0);
        Assert.False(Triangulation.SplitsOnFirstDiagonal(a, b, c, skewed));
    }

    [Fact]
    public void WriteFaceTriangles_CopiesATriangleThrough()
    {
        Mesh mesh = MeshOps.Create();
        mesh.AddVertices(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(0, 1, 0),
        ]);
        mesh.AddFace(0, 1, 2);

        int[] triangles = new int[3];
        Assert.True(Triangulation.WriteFaceTriangles(mesh, 0, triangles).IsSuccess);
        Assert.Equal([0, 1, 2], triangles);
    }

    [Fact]
    public void WriteFaceTriangles_TriangulatesAConcaveFace()
    {
        Mesh mesh = MeshOps.Create();
        mesh.AddVertices(LShape());
        mesh.AddFace([0, 1, 2, 3, 4, 5]);

        int[] triangles = new int[4 * 3];
        OperationResult result = Triangulation.WriteFaceTriangles(mesh, 0, triangles);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(5, TriangleArea(mesh.Vertices, triangles), 12);
    }

    [Fact]
    public void WriteFaceTriangles_FansADegenerateFaceAndReportsIt()
    {
        Mesh mesh = MeshOps.Create();
        mesh.AddVertices(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(2, 0, 0),
            PointOps.Create(3, 0, 0),
            PointOps.Create(4, 0, 0),
        ]);
        mesh.AddFace([0, 1, 2, 3, 4]);

        int[] triangles = new int[3 * 3];
        OperationResult result = Triangulation.WriteFaceTriangles(mesh, 0, triangles);

        Assert.True(result.IsPartial);
        Assert.True(result.HasOutput);
        Assert.Equal(0, TriangleArea(mesh.Vertices, triangles), 12);
    }

    [Fact]
    public void WriteFaceTriangles_RejectsATooSmallBuffer()
    {
        Mesh mesh = MeshBuilders.CreateBox(1, 1, 1);

        Assert.Throws<ArgumentException>(() => Triangulation.WriteFaceTriangles(mesh, 0, new int[3]));
    }
}
