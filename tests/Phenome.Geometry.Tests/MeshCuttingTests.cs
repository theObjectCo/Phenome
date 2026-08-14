namespace Phenome.Geometry.Tests;

public class MeshCuttingTests
{
    /// <summary>
    /// The enclosed volume from the divergence theorem. Only meaningful for a closed mesh, which is why it
    /// doubles as the watertightness check: an open mesh gives a number that does not match the shape.
    /// </summary>
    /// <remarks>
    /// The mesh is triangulated properly first rather than each face being fanned from its first corner.
    /// Fanning is only right for a convex face, and cutting produces concave ones — including the bridged
    /// loop a face split in two comes back as, where fanning silently loses whole lobes.
    /// </remarks>
    private static double SignedVolume(Mesh mesh)
    {
        Assert.True(MeshOps.Triangulate(mesh, out Mesh? triangles).HasOutput);

        double total = 0;
        ReadOnlySpan<Point3d> vertices = triangles!.Vertices;

        for (int face = 0; face < triangles.FaceCount; face++)
        {
            ReadOnlySpan<int> corners = triangles.Face(face);
            Point3d a = vertices[corners[0]];

            total += VectorOps.Dot(
                (Vector3d)a,
                VectorOps.Cross(vertices[corners[1]] - a, vertices[corners[2]] - a)) / 6.0;
        }

        return total;
    }

    private static double SurfaceArea(Mesh mesh)
    {
        double total = 0;

        for (int i = 0; i < mesh.FaceCount; i++)
        {
            total += MeshOps.FaceArea(mesh, i);
        }

        return total;
    }

    /// <summary>
    /// How many edges are used by exactly one face. Zero means every edge is shared, which is what closed
    /// means — and the property that a per-face clip destroys unless cut points are shared.
    /// </summary>
    private static int BoundaryEdgeCount(Mesh mesh)
    {
        Dictionary<(int, int), int> uses = [];

        for (int face = 0; face < mesh.FaceCount; face++)
        {
            ReadOnlySpan<int> corners = mesh.Face(face);

            for (int i = 0; i < corners.Length; i++)
            {
                int a = corners[i];
                int b = corners[(i + 1) % corners.Length];
                (int, int) key = a < b ? (a, b) : (b, a);
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }
        }

        return uses.Values.Count(count => count == 1);
    }

    /// <summary>A plane at the given height, normal pointing up, so "below" is the lower part.</summary>
    private static Plane Horizontal(double z) =>
        PlaneOps.CreateFromNormal(PointOps.Create(0, 0, z), Vector3d.ZAxis);

    [Fact]
    public void SplitByPlane_CutsABoxIntoTwoSolidsThatAddUpToTheOriginal()
    {
        Mesh box = MeshBuilders.CreateBox(2, 2, 10);

        OperationResult result = MeshCutting.SplitByPlane(
            box, Horizontal(4), capped: true, out Mesh? below, out Mesh? above);

        Assert.True(result.IsSuccess, result.ToString());

        Assert.Equal(16, SignedVolume(below!), 9);
        Assert.Equal(24, SignedVolume(above!), 9);
        Assert.Equal(SignedVolume(box), SignedVolume(below!) + SignedVolume(above!), 9);
    }

    [Fact]
    public void SplitByPlane_LeavesBothHalvesWatertightWhenCapped()
    {
        // The property the shared cut-point cache exists for. Clipping each face on its own would put two
        // vertices at the same place on every cut edge, and both halves would come apart along the seam.
        Mesh box = MeshBuilders.CreateBox(3, 4, 5);

        Assert.True(MeshCutting.SplitByPlane(
            box, Horizontal(2), capped: true, out Mesh? below, out Mesh? above).IsSuccess);

        Assert.Equal(0, BoundaryEdgeCount(below!));
        Assert.Equal(0, BoundaryEdgeCount(above!));
    }

    [Fact]
    public void SplitByPlane_LeavesTheCutOpenWhenNotCapped()
    {
        Mesh box = MeshBuilders.CreateBox(1, 1, 4);

        Assert.True(MeshCutting.SplitByPlane(
            box, Horizontal(2), capped: false, out Mesh? below, out Mesh? above).IsSuccess);

        // Measured by length, not by count. Splitting the straddling faces into triangles first puts a cut
        // point wherever a diagonal crosses the plane, so the square opening comes back as more than four
        // edges — the same loop, more finely divided. Its perimeter is what has to be right.
        Assert.Equal(4, BoundaryLength(below!), 9);
        Assert.Equal(4, BoundaryLength(above!), 9);

        Assert.True(BoundaryEdgeCount(below!) >= 4);
    }

    /// <summary>The total length of the edges used by exactly one face.</summary>
    private static double BoundaryLength(Mesh mesh)
    {
        Dictionary<(int, int), int> uses = [];

        for (int face = 0; face < mesh.FaceCount; face++)
        {
            ReadOnlySpan<int> corners = mesh.Face(face);

            for (int i = 0; i < corners.Length; i++)
            {
                int a = corners[i];
                int b = corners[(i + 1) % corners.Length];
                (int, int) key = a < b ? (a, b) : (b, a);
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }
        }

        double total = 0;

        foreach (((int a, int b) edge, int count) in uses)
        {
            if (count == 1)
            {
                total += PointOps.DistanceTo(mesh.Vertices[edge.a], mesh.Vertices[edge.b]);
            }
        }

        return total;
    }

    [Fact]
    public void SplitByPlane_PutsTheSeamAtIdenticalPointsInBothHalves()
    {
        // Same cache, same arithmetic, so the two halves can be joined back together without welding.
        Mesh box = MeshBuilders.CreateBox(2, 3, 7);
        Plane slanted = PlaneOps.CreateFromNormal(
            PointOps.Create(1, 1.5, 3), VectorOps.Create(1, 2, 5));

        Assert.True(MeshCutting.SplitByPlane(
            box, slanted, capped: false, out Mesh? below, out Mesh? above).IsSuccess);

        HashSet<Point3d> belowOnPlane = [];

        foreach (Point3d point in below!.Vertices)
        {
            if (PlaneOps.Contains(slanted, point))
            {
                belowOnPlane.Add(point);
            }
        }

        int matched = 0;

        foreach (Point3d point in above!.Vertices)
        {
            if (PlaneOps.Contains(slanted, point))
            {
                Assert.Contains(point, belowOnPlane);
                matched++;
            }
        }

        Assert.True(matched > 0, "the slanted plane should have cut something");
        Assert.Equal(belowOnPlane.Count, matched);
    }

    [Fact]
    public void SplitByPlane_CapsNormalPointsOutOfEachHalf()
    {
        Mesh box = MeshBuilders.CreateBox(2, 2, 6);

        Assert.True(MeshCutting.SplitByPlane(
            box, Horizontal(3), capped: true, out Mesh? below, out Mesh? above).IsSuccess);

        // A positive volume is only possible if every face, cap included, faces outwards.
        Assert.True(SignedVolume(below!) > 0, "the lower half came out inside-out");
        Assert.True(SignedVolume(above!) > 0, "the upper half came out inside-out");

        // And the cap of the lower half faces the way the plane does.
        int capBelow = FindFaceWithNormal(below!, Vector3d.ZAxis, 3);
        int capAbove = FindFaceWithNormal(above!, -Vector3d.ZAxis, 3);

        Assert.True(capBelow >= 0, "the lower half has no upward-facing cap");
        Assert.True(capAbove >= 0, "the upper half has no downward-facing cap");
        Assert.Equal(4, MeshOps.FaceArea(below!, capBelow), 9);
    }

    private static int FindFaceWithNormal(Mesh mesh, Vector3d direction, double atZ)
    {
        for (int i = 0; i < mesh.FaceCount; i++)
        {
            if (MeshOps.TryFaceNormal(mesh, i, out Vector3d? normal) &&
                VectorOps.EpsilonEquals(normal.Value, direction, 1e-9) &&
                Math.Abs(MeshOps.FaceCenter(mesh, i).Z - atZ) <= 1e-9)
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public void SplitByPlane_LeavesAMeshEntirelyOnOneSideUntouched()
    {
        // The plane misses the box, so nothing should be rewritten and the other half should be empty rather
        // than null.
        Mesh box = MeshBuilders.CreateBox(1, 1, 1);

        Assert.True(MeshCutting.SplitByPlane(
            box, Horizontal(50), capped: true, out Mesh? below, out Mesh? above).IsSuccess);

        Assert.Equal(box.FaceCount, below!.FaceCount);
        Assert.Equal(box.VertexCount, below.VertexCount);
        Assert.Equal(SignedVolume(box), SignedVolume(below), 9);

        Assert.NotNull(above);
        Assert.Equal(0, above.FaceCount);
    }

    [Fact]
    public void SplitByPlane_AddsNoVerticesWhenThePlaneOnlyGrazesAVertex()
    {
        // The plane sits exactly on the top face. The tolerance band should classify those corners as on the
        // plane, so nothing is cut and no near-duplicate vertices appear.
        Mesh box = MeshBuilders.CreateBox(2, 2, 5);

        Assert.True(MeshCutting.SplitByPlane(
            box, Horizontal(5), capped: false, out Mesh? below, out Mesh? above).IsSuccess);

        Assert.Equal(8, below!.VertexCount);
        Assert.Equal(SignedVolume(box), SignedVolume(below), 9);
        Assert.Equal(0, above!.FaceCount);
    }

    [Fact]
    public void SplitByPlane_SendsACoplanarFaceToTheHalfItsOwnNormalBounds()
    {
        // The top face lies in the plane. It is the outward skin of the material below, and its normal says
        // so, so no convention is needed to place it.
        Mesh box = MeshBuilders.CreateBox(2, 2, 5);

        Assert.True(MeshCutting.SplitByPlane(
            box, Horizontal(5), capped: false, out Mesh? below, out Mesh? above).IsSuccess);

        Assert.Equal(6, below!.FaceCount);
        Assert.Equal(0, above!.FaceCount);

        // Flipping the plane must move it to the other half rather than duplicating or losing it.
        Assert.True(MeshCutting.SplitByPlane(
            box,
            PlaneOps.Flipped(Horizontal(5)),
            capped: false,
            out Mesh? flippedBelow,
            out Mesh? flippedAbove).IsSuccess);

        Assert.Equal(0, flippedBelow!.FaceCount);
        Assert.Equal(6, flippedAbove!.FaceCount);
    }

    [Fact]
    public void TrimByPlane_IsSplitWithTheOtherHalfDiscarded()
    {
        Mesh box = MeshBuilders.CreateBox(2, 2, 10);
        Plane cut = Horizontal(6);

        Assert.True(MeshCutting.SplitByPlane(box, cut, true, out Mesh? below, out _).IsSuccess);
        Assert.True(MeshCutting.TrimByPlane(MeshBuilders.CreateBox(2, 2, 10), cut, true, out Mesh? trimmed).IsSuccess);

        Assert.Equal(SignedVolume(below!), SignedVolume(trimmed!), 9);
        Assert.Equal(below!.FaceCount, trimmed!.FaceCount);
    }

    [Fact]
    public void TrimByPlane_CutsAMitredEndOnAPost()
    {
        // The case this was written for: a leg cut at an angle where it meets something. One call.
        Mesh post = MeshBuilders.CreateBox(
            IntervalOps.CreateFromCenter(0, 2),
            IntervalOps.CreateFromCenter(0, 2),
            IntervalOps.Create(0, 20));

        Plane mitre = PlaneOps.CreateFromNormal(
            PointOps.Create(0, 0, 18), VectorOps.Create(0, 1, 1));

        OperationResult result = MeshCutting.TrimByPlane(post, mitre, capped: true, out Mesh? cut);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(0, BoundaryEdgeCount(cut!));

        // The slanted cut removes a wedge, so the volume drops but stays positive and the post keeps its foot.
        Assert.True(SignedVolume(cut!) < SignedVolume(post));
        Assert.True(SignedVolume(cut!) > 0);

        // Nothing survives above the mitre plane.
        foreach (Point3d vertex in cut!.Vertices)
        {
            Assert.True(PlaneOps.SignedDistanceTo(mitre, vertex) <= Tolerance.Distance);
        }
    }

    [Fact]
    public void TrimByPlane_RepeatedTrimsFitAPartInsideABox()
    {
        // Six half-spaces make a box, which is the payoff of the clip being against a convex region.
        Mesh mesh = MeshBuilders.CreateBox(
            IntervalOps.Create(-10, 10), IntervalOps.Create(-10, 10), IntervalOps.Create(-10, 10));

        BoundingBox target = BoundingBoxOps.Create(
            IntervalOps.Create(-2, 3), IntervalOps.Create(-1, 4), IntervalOps.Create(-5, 1));

        (Point3d origin, Vector3d normal)[] sides =
        [
            (BoundingBoxOps.Max(target), Vector3d.XAxis),
            (BoundingBoxOps.Min(target), -Vector3d.XAxis),
            (BoundingBoxOps.Max(target), Vector3d.YAxis),
            (BoundingBoxOps.Min(target), -Vector3d.YAxis),
            (BoundingBoxOps.Max(target), Vector3d.ZAxis),
            (BoundingBoxOps.Min(target), -Vector3d.ZAxis),
        ];

        foreach ((Point3d origin, Vector3d normal) in sides)
        {
            OperationResult result = MeshCutting.TrimByPlane(
                mesh, PlaneOps.CreateFromNormal(origin, normal), capped: true, out Mesh? trimmed);

            Assert.True(result.IsSuccess, result.ToString());
            mesh = trimmed!;
        }

        Assert.Equal(BoundingBoxOps.Volume(target), SignedVolume(mesh), 9);
        Assert.Equal(0, BoundaryEdgeCount(mesh));
        Assert.True(BoundingBoxOps.EpsilonEquals(target, BoundingBoxOps.Bound(mesh)));
    }

    [Fact]
    public void SplitByPlane_CapsATubeAsAnAnnulusRatherThanADisc()
    {
        // Two boundary loops, one inside the other. Treating them as separate caps would fill the bore in.
        Polyline outer = CircleOps.ToPolyline(CircleOps.Create(Plane.WorldXY, 5), 24);
        Polyline inner = CircleOps.ToPolyline(CircleOps.Create(Plane.WorldXY, 3), 24);

        Assert.True(MeshBuilders.CreateExtrusion(outer, VectorOps.Create(0, 0, 10), false, out Mesh? outerWall).IsSuccess);
        Assert.True(MeshBuilders.CreateExtrusion(inner, VectorOps.Create(0, 0, 10), false, out Mesh? innerWall).IsSuccess);

        MeshOps.Flip(innerWall!);
        Assert.True(MeshOps.Join([outerWall!, innerWall!], out Mesh tube).IsSuccess);

        OperationResult result = MeshCutting.SplitByPlane(
            tube, Horizontal(4), capped: true, out Mesh? below, out Mesh? above);

        Assert.True(result.IsSuccess, result.ToString());

        double outerArea = Math.Abs(PolylineOps.SignedArea(outer, Plane.WorldXY));
        double innerArea = Math.Abs(PolylineOps.SignedArea(inner, Plane.WorldXY));
        double annulus = outerArea - innerArea;

        // The lower half gains one annulus of cap area over the bare walls it started with.
        double wallsBelow = (SurfaceArea(outerWall!) + SurfaceArea(innerWall!)) * 0.4;

        Assert.Equal(wallsBelow + annulus, SurfaceArea(below!), 6);
        Assert.Equal((SurfaceArea(outerWall!) + SurfaceArea(innerWall!)) * 0.6 + annulus, SurfaceArea(above!), 6);
    }

    [Fact]
    public void SplitByPlane_CapsTwoSeparateOpeningsAsTwoCaps()
    {
        // Two posts cut at once. Neither loop contains the other, so both are outlines and each gets its own
        // cap rather than one being mistaken for a hole in the other.
        Mesh left = MeshBuilders.CreateBox(
            IntervalOps.Create(0, 2), IntervalOps.Create(0, 2), IntervalOps.Create(0, 10));

        Mesh right = MeshBuilders.CreateBox(
            IntervalOps.Create(10, 12), IntervalOps.Create(0, 2), IntervalOps.Create(0, 10));

        Assert.True(MeshOps.Join([left, right], out Mesh both).IsSuccess);

        OperationResult result = MeshCutting.SplitByPlane(
            both, Horizontal(5), capped: true, out Mesh? below, out Mesh? above);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(0, BoundaryEdgeCount(below!));
        Assert.Equal(40, SignedVolume(below!), 9);
        Assert.Equal(40, SignedVolume(above!), 9);
    }

    [Fact]
    public void SplitByPlane_InterpolatesAttributesAlongTheCutEdge()
    {
        // A new vertex takes its colour from the edge, not from either end, or a seam appears exactly where
        // the cut is.
        Mesh box = MeshBuilders.CreateBox(1, 1, 10);
        Color32[] colors = new Color32[box.VertexCount];

        for (int i = 0; i < box.VertexCount; i++)
        {
            // Black at the bottom, white at the top.
            colors[i] = box.Vertices[i].Z > 5 ? Color32.White : Color32.Black;
        }

        box.SetVertexColors(colors);

        Assert.True(MeshCutting.SplitByPlane(
            box, Horizontal(5), capped: false, out Mesh? below, out _).IsSuccess);

        Assert.True(below!.HasVertexColors);
        Assert.Equal(below.VertexCount, below.VertexColors.Length);

        // Halfway up, so the cut vertices should be mid grey rather than either extreme.
        bool foundGrey = false;

        for (int i = 0; i < below.VertexCount; i++)
        {
            if (Math.Abs(below.Vertices[i].Z - 5) > Tolerance.Distance)
            {
                continue;
            }

            Assert.InRange(below.VertexColors[i].R, 100, 155);
            foundGrey = true;
        }

        Assert.True(foundGrey, "no vertex was created on the cut");
    }

    [Fact]
    public void SplitByPlane_CarriesFaceGroupsOntoTheFacesThatCameFromThem()
    {
        Mesh box = MeshBuilders.CreateBox(1, 1, 4);
        box.SetFaceGroups([0, 1, 2, 3, 4, 5]);

        Assert.True(MeshCutting.SplitByPlane(
            box, Horizontal(2), capped: false, out Mesh? below, out _).IsSuccess);

        Assert.True(below!.HasFaceGroups);
        Assert.Equal(below.FaceCount, below.FaceGroups.Length);

        // The bottom face keeps group 0; the four sides keep 2 to 5. The top face is gone.
        Assert.Contains(0, below.FaceGroups.ToArray());
        Assert.DoesNotContain(1, below.FaceGroups.ToArray());
    }

    [Fact]
    public void SplitByPlane_CutsAConcaveFaceWithoutLosingArea()
    {
        // Sutherland-Hodgman answers a concave face that falls in two with one loop bridged along the cut
        // rather than with two faces. The bridge has no area, so the total must still be right.
        Polyline uShape = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(6, 0, 0),
            PointOps.Create(6, 6, 0),
            PointOps.Create(4, 6, 0),
            PointOps.Create(4, 2, 0),
            PointOps.Create(2, 2, 0),
            PointOps.Create(2, 6, 0),
            PointOps.Create(0, 6, 0),
            PointOps.Create(0, 0, 0),
        ]);

        Assert.True(MeshBuilders.CreateExtrusion(uShape, VectorOps.Create(0, 0, 3), true, out Mesh? solid).IsSuccess);

        double before = SignedVolume(solid!);

        // Cut across both arms of the U, which is the case that splits the top cap into two pieces.
        Plane across = PlaneOps.CreateFromNormal(PointOps.Create(0, 4, 0), Vector3d.YAxis);

        OperationResult result = MeshCutting.SplitByPlane(
            solid!, across, capped: true, out Mesh? below, out Mesh? above);

        Assert.True(result.IsSuccess, result.ToString());

        // The lower half is one piece; the upper half is the two arms, which is the case that would come back
        // bridged into one uncappable loop if the face were clipped as an n-gon.
        Assert.Equal(60, SignedVolume(below!), 6);
        Assert.Equal(24, SignedVolume(above!), 6);
        Assert.Equal(before, SignedVolume(below!) + SignedVolume(above!), 6);

        Assert.Equal(0, BoundaryEdgeCount(below!));
        Assert.Equal(0, BoundaryEdgeCount(above!));
    }

    [Fact]
    public void SplitByPlane_FailsOnAnInvalidPlane()
    {
        OperationResult result = MeshCutting.SplitByPlane(
            MeshBuilders.CreateBox(1, 1, 1), Plane.Unset, true, out Mesh? below, out Mesh? above);

        Assert.True(result.IsFailed);
        Assert.Null(below);
        Assert.Null(above);
    }

    [Fact]
    public void SplitByPlane_FailsOnANonFiniteVertex()
    {
        Mesh mesh = MeshOps.Create();
        mesh.AddVertices([PointOps.Create(0, 0, 0), PointOps.Create(1, 0, 0), Point3d.Unset]);
        mesh.AddFace(0, 1, 2);

        OperationResult result = MeshCutting.SplitByPlane(
            mesh, Horizontal(0.5), false, out Mesh? below, out _);

        Assert.True(result.IsFailed);
        Assert.Null(below);
        Assert.Contains("finite", result.Message);
    }

    [Fact]
    public void SplitByPlane_OfAnOpenSheetCutsItWithoutClaimingToCapIt()
    {
        // An open mesh has a boundary before the cut, so capping has nothing closed to work with. It must not
        // invent a face across the sheet.
        Mesh grid = MeshBuilders.CreateGrid(4, 4, 1, 1);

        OperationResult result = MeshCutting.SplitByPlane(
            grid,
            PlaneOps.CreateFromNormal(PointOps.Create(2, 0, 0), Vector3d.XAxis),
            capped: true,
            out Mesh? below,
            out Mesh? above);

        Assert.True(result.HasOutput, result.ToString());
        Assert.Equal(8, SurfaceArea(below!), 9);
        Assert.Equal(8, SurfaceArea(above!), 9);
    }
}
