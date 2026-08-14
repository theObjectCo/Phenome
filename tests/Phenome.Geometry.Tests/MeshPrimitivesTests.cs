namespace Phenome.Geometry.Tests;

/// <summary>
/// Checks the solid primitives: prism, cylinder, cone, pyramid, sphere.
/// </summary>
/// <remarks>
/// Two of these tests do most of the work. One pairs up every directed edge, which is the actual definition of
/// a closed mesh whose faces agree about which way round they go — a single face wound backwards leaves two
/// edges travelling the same way and no partner for either. The other measures the enclosed volume from the
/// triangulation and compares it with the formula: that catches winding, closure and size together, and a mesh
/// built inside out comes out with a negative volume rather than looking fine.
/// </remarks>
public class MeshPrimitivesTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(64)]
    public void Prism_HasTwoRingsAndOneQuadPerSide(int sides)
    {
        Mesh prism = MeshBuilders.CreatePrism(sides, 2, 5);

        Assert.Equal(sides * 2, prism.VertexCount);
        Assert.Equal(sides + 2, prism.FaceCount);
        Assert.True(MeshOps.IsValid(prism));

        // The caps stay single n-gon faces rather than being fanned into triangles.
        Assert.Equal(sides, prism.CornersInFace(prism.FaceCount - 1));
        Assert.Equal(sides, prism.CornersInFace(prism.FaceCount - 2));

        for (int face = 0; face < sides; face++)
        {
            Assert.Equal(4, prism.CornersInFace(face));
        }
    }

    [Fact]
    public void Prism_StandsOnThePlaneCentredOnTheAxis()
    {
        Mesh prism = MeshBuilders.CreatePrism(6, 2, 5);
        BoundingBox bounds = BoundingBoxOps.Bound(prism);

        Assert.Equal(0, BoundingBoxOps.Min(bounds).Z, 12);
        Assert.Equal(5, BoundingBoxOps.Max(bounds).Z, 12);

        // Centred: the corners sit on the radius, so X and Y run from minus it to plus it.
        Assert.Equal(-2, BoundingBoxOps.Min(bounds).X, 12);
        Assert.Equal(2, BoundingBoxOps.Max(bounds).X, 12);
    }

    [Fact]
    public void Prism_HasItsCornersOnTheRadiusAndItsFlatsInside()
    {
        // The distinction that matters before cutting anything to fit: a hexagon of radius 10 measures 20
        // across its corners and 17.32 across its flats.
        Mesh hexagon = MeshBuilders.CreatePrism(6, 10, 1);

        Assert.Equal(10, PointOps.DistanceTo(hexagon.Vertices[0], Point3d.Origin), 12);

        Line flat = LineOps.Create(hexagon.Vertices[0], hexagon.Vertices[1]);

        Assert.Equal(10 * Math.Sqrt(3) / 2, LineOps.DistanceTo(flat, Point3d.Origin), 12);
    }

    [Fact]
    public void Prism_LeftUncappedIsOpenAtBothEnds()
    {
        Mesh open = MeshBuilders.CreatePrism(8, 1, 1, capped: false);

        Assert.Equal(8, open.FaceCount);
        Assert.True(MeshOps.IsValid(open));
    }

    [Fact]
    public void Cylinder_IsThePrismOfThatManySides()
    {
        // Said in the documentation, so worth holding to: they differ in what they are for, not in what they
        // produce.
        Mesh cylinder = MeshBuilders.CreateCylinder(3, 7, 24);
        Mesh prism = MeshBuilders.CreatePrism(24, 3, 7);

        Assert.Equal(prism.VertexCount, cylinder.VertexCount);
        Assert.Equal(prism.FaceCount, cylinder.FaceCount);

        for (int i = 0; i < prism.VertexCount; i++)
        {
            Assert.True(PointOps.EpsilonEquals(prism.Vertices[i], cylinder.Vertices[i]));
        }
    }

    [Fact]
    public void Cone_HasOneApexSharedByEverySide()
    {
        Mesh cone = MeshBuilders.CreateCone(2, 6, 12);

        Assert.Equal(13, cone.VertexCount);
        Assert.Equal(13, cone.FaceCount);
        Assert.True(MeshOps.IsValid(cone));

        // The tip is one vertex, which is why the documentation points at SplitAtCreases for shading.
        Assert.Equal(PointOps.Create(0, 0, 6), cone.Vertices[12]);

        for (int face = 0; face < 12; face++)
        {
            Assert.Equal(3, cone.CornersInFace(face));
        }
    }

    [Fact]
    public void Pyramid_HasFourTrianglesOverARectangle()
    {
        Mesh pyramid = MeshBuilders.CreatePyramid(4, 6, 3);

        Assert.Equal(5, pyramid.VertexCount);
        Assert.Equal(5, pyramid.FaceCount);
        Assert.True(MeshOps.IsValid(pyramid));

        // Anchored by a corner like a box, with the apex over the middle.
        Assert.Equal(Point3d.Origin, pyramid.Vertices[0]);
        Assert.Equal(PointOps.Create(2, 3, 3), pyramid.Vertices[4]);
    }

    [Fact]
    public void Sphere_HasTrianglesAtThePolesAndQuadsBetween()
    {
        Mesh sphere = MeshBuilders.CreateSphere(1, 8, 4);

        // Two poles plus three rings of eight.
        Assert.Equal((3 * 8) + 2, sphere.VertexCount);
        Assert.Equal(4 * 8, sphere.FaceCount);
        Assert.True(MeshOps.IsValid(sphere));

        int triangles = 0;
        int quads = 0;

        for (int face = 0; face < sphere.FaceCount; face++)
        {
            if (sphere.CornersInFace(face) == 3)
            {
                triangles++;
            }
            else if (sphere.CornersInFace(face) == 4)
            {
                quads++;
            }
        }

        Assert.Equal(16, triangles);
        Assert.Equal(16, quads);
    }

    [Fact]
    public void Sphere_HasEveryVertexOnTheRadius()
    {
        Mesh sphere = MeshBuilders.CreateSphere(2.5, 16, 8);

        foreach (Point3d vertex in sphere.Vertices)
        {
            Assert.Equal(2.5, PointOps.DistanceTo(vertex, Point3d.Origin), 12);
        }
    }

    [Fact]
    public void Sphere_AtTwoStacksIsTwoConesBackToBack()
    {
        // The smallest sphere the parameters allow. Worth pinning because it is the case with no quad band at
        // all, and the loop that writes those bands has to notice.
        Mesh sphere = MeshBuilders.CreateSphere(1, 4, 2);

        Assert.Equal(6, sphere.VertexCount);
        Assert.Equal(8, sphere.FaceCount);
        Assert.True(MeshOps.IsValid(sphere));
    }

    [Theory]
    [MemberData(nameof(ClosedSolids))]
    public void EveryClosedSolidHasEveryFaceWoundSoItsNormalPointsOutwards(Mesh solid, Point3d centre)
    {
        for (int face = 0; face < solid.FaceCount; face++)
        {
            Assert.True(MeshOps.TryFaceNormal(solid, face, out Vector3d? normal));

            Vector3d outward = MeshOps.FaceCenter(solid, face) - centre;

            Assert.True(
                VectorOps.Dot(normal!.Value, outward) > 0,
                $"Face {face} of a {solid} points back towards the centre.");
        }
    }

    [Theory]
    [MemberData(nameof(ClosedSolids))]
    public void EveryClosedSolidPairsUpEveryEdge(Mesh solid, Point3d centre)
    {
        _ = centre;

        // The definition of closed, and of consistently wound with it: each edge is walked once in each
        // direction. A single face put in backwards leaves two edges going the same way and no partner for
        // either, which no count of vertices or faces would notice.
        Dictionary<(int From, int To), int> walked = [];

        for (int face = 0; face < solid.FaceCount; face++)
        {
            ReadOnlySpan<int> corners = solid.Face(face);

            for (int i = 0; i < corners.Length; i++)
            {
                (int From, int To) edge = (corners[i], corners[(i + 1) % corners.Length]);

                walked[edge] = walked.TryGetValue(edge, out int already) ? already + 1 : 1;
            }
        }

        foreach (KeyValuePair<(int From, int To), int> edge in walked)
        {
            Assert.True(
                edge.Value == 1,
                $"Edge {edge.Key.From} to {edge.Key.To} is walked {edge.Value} times the same way.");

            Assert.True(
                walked.ContainsKey((edge.Key.To, edge.Key.From)),
                $"Edge {edge.Key.From} to {edge.Key.To} has no partner going the other way.");
        }
    }

    [Fact]
    public void APrismEnclosesTheVolumeItsFormulaSays()
    {
        // Measured through the triangulation, so this exercises the ear clipper on the n-gon caps as well.
        const int sides = 12;
        const double radius = 3;
        const double height = 5;

        double area = 0.5 * sides * radius * radius * Math.Sin(Math.Tau / sides);

        Assert.Equal(area * height, Volume(MeshBuilders.CreatePrism(sides, radius, height)), 9);
    }

    [Fact]
    public void AConeEnclosesAThirdOfItsBaseTimesItsHeight()
    {
        const int segments = 16;
        const double radius = 2;
        const double height = 6;

        double area = 0.5 * segments * radius * radius * Math.Sin(Math.Tau / segments);

        Assert.Equal(area * height / 3, Volume(MeshBuilders.CreateCone(radius, height, segments)), 9);
    }

    [Fact]
    public void APyramidEnclosesAThirdOfItsBoxWorth()
    {
        Assert.Equal(4 * 6 * 3 / 3.0, Volume(MeshBuilders.CreatePyramid(4, 6, 3)), 9);
    }

    [Fact]
    public void ASphereClosesInOnItsAnalyticVolumeAsItIsDivided()
    {
        // A mesh inscribed in a sphere is always under size, so the interesting property is not a tolerance but
        // the way the shortfall behaves: it must approach from below, and doubling the divisions in both
        // directions must cut it by more than half. Asserting a decimal place instead would be asserting a
        // division count — 64 by 32 is 0.4% short, which is correct for this method and not a defect.
        double exact = 4.0 / 3.0 * Math.PI;

        double coarse = Volume(MeshBuilders.CreateSphere(1, 16, 8));
        double fine = Volume(MeshBuilders.CreateSphere(1, 32, 16));
        double finer = Volume(MeshBuilders.CreateSphere(1, 64, 32));

        Assert.True(coarse < fine && fine < finer, $"Not monotone: {coarse}, {fine}, {finer}.");
        Assert.True(finer < exact, $"An inscribed mesh cannot enclose more than the sphere: {finer}.");

        Assert.True(
            exact - finer < (exact - fine) / 2,
            $"The shortfall went from {exact - fine} to {exact - finer}, which is not converging.");
    }

    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(3, 0, 1)]
    [InlineData(3, 1, 0)]
    [InlineData(3, -1, 1)]
    public void APrismRefusesSidesUnderThreeAndSizesAtOrUnderZero(int sides, double radius, double height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshBuilders.CreatePrism(sides, radius, height));
    }

    [Fact]
    public void TheOtherPrimitivesRefuseTheSameKindOfArgument()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshBuilders.CreateCylinder(1, 1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshBuilders.CreateCone(1, 1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshBuilders.CreateCone(0, 1, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshBuilders.CreatePyramid(1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshBuilders.CreateSphere(1, 3, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshBuilders.CreateSphere(1, 2, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshBuilders.CreateSphere(0, 8, 4));
    }

    public static TheoryData<Mesh, Point3d> ClosedSolids() => new()
    {
        { MeshBuilders.CreatePrism(3, 2, 4), PointOps.Create(0, 0, 2) },
        { MeshBuilders.CreatePrism(7, 2, 4), PointOps.Create(0, 0, 2) },
        { MeshBuilders.CreateCylinder(1.5, 3, 32), PointOps.Create(0, 0, 1.5) },
        { MeshBuilders.CreateCone(2, 5, 9), PointOps.Create(0, 0, 1) },
        { MeshBuilders.CreatePyramid(4, 6, 3), PointOps.Create(2, 3, 0.75) },
        { MeshBuilders.CreateSphere(2, 12, 6), Point3d.Origin },
        { MeshBuilders.CreateSphere(2, 5, 2), Point3d.Origin },
    };

    /// <summary>The volume a closed mesh encloses, signed so that an inside-out mesh reports a negative one.</summary>
    private static double Volume(Mesh mesh)
    {
        Assert.True(RenderBuffers.CreateTriangleIndices(mesh, out int[]? triangles).HasOutput);

        double total = 0;

        for (int i = 0; i < triangles!.Length; i += 3)
        {
            Point3d a = mesh.Vertices[triangles[i]];
            Point3d b = mesh.Vertices[triangles[i + 1]];
            Point3d c = mesh.Vertices[triangles[i + 2]];

            total += VectorOps.Dot(a, VectorOps.Cross(b, c)) / 6.0;
        }

        return total;
    }
}
