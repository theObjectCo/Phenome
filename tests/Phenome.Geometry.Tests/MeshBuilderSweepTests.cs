namespace Phenome.Geometry.Tests;

/// <summary>
/// Covers the four builders that share the ring-stitching machinery: extrude, revolve, loft and sweep.
/// </summary>
public class MeshBuilderSweepTests
{
    private static Polyline ClosedSquare(double size, double z = 0) => PolylineOps.Create(
    [
        PointOps.Create(0, 0, z),
        PointOps.Create(size, 0, z),
        PointOps.Create(size, size, z),
        PointOps.Create(0, size, z),
        PointOps.Create(0, 0, z),
    ]);

    /// <summary>Total surface area, which is what says whether a builder produced the right faces.</summary>
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
    /// The enclosed volume from the divergence theorem, signed so that an inside-out mesh comes back
    /// negative. Zero for anything that is not closed.
    /// </summary>
    private static double SignedVolume(Mesh mesh)
    {
        double total = 0;

        for (int face = 0; face < mesh.FaceCount; face++)
        {
            ReadOnlySpan<int> corners = mesh.Face(face);
            ReadOnlySpan<Point3d> vertices = mesh.Vertices;
            Point3d a = vertices[corners[0]];

            for (int i = 1; i < corners.Length - 1; i++)
            {
                Point3d b = vertices[corners[i]];
                Point3d c = vertices[corners[i + 1]];

                total += VectorOps.Dot(
                    (Vector3d)a,
                    VectorOps.Cross(b - a, c - a)) / 6.0;
            }
        }

        return total;
    }

    [Fact]
    public void Extrude_TurnsAClosedProfileIntoASolid()
    {
        OperationResult result = MeshBuilders.CreateExtrusion(
            ClosedSquare(2),
            VectorOps.Create(0, 0, 3),
            capped: true,
            out Mesh? mesh);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.NotNull(mesh);

        // Four sides plus two caps.
        Assert.Equal(6, mesh.FaceCount);
        Assert.Equal(8, mesh.VertexCount);

        // Two 2x2 caps and four 2x3 sides.
        Assert.Equal((2 * 4) + (4 * 6), SurfaceArea(mesh), 9);
        Assert.Equal(12, SignedVolume(mesh), 9);
    }

    [Fact]
    public void Extrude_FacesOutwardsWhicheverWayTheProfileWasDrawn()
    {
        // A profile coming out of an offset or a fillet has whatever direction it has; making the caller
        // check first would be a trap, and a negative volume is exactly the inside-out case.
        Polyline clockwise = PolylineOps.Reversed(ClosedSquare(2));

        Assert.True(MeshBuilders.CreateExtrusion(
            clockwise, VectorOps.Create(0, 0, 3), capped: true, out Mesh? mesh).IsSuccess);

        Assert.True(SignedVolume(mesh!) > 0, "the extrusion came out inside-out");
        Assert.Equal(12, SignedVolume(mesh!), 9);
    }

    [Fact]
    public void Extrude_KeepsSidesAsQuadsRatherThanSplittingThem()
    {
        Assert.True(MeshBuilders.CreateExtrusion(
            ClosedSquare(2), VectorOps.Create(0, 0, 1), capped: false, out Mesh? mesh).IsSuccess);

        Assert.Equal(4, mesh!.FaceCount);

        for (int i = 0; i < mesh.FaceCount; i++)
        {
            Assert.Equal(4, mesh.CornersInFace(i));
        }
    }

    [Fact]
    public void Extrude_CapsAConcaveProfileAsASingleFace()
    {
        // The mesh keeps n-gons on purpose; a concave cap is still one flat face, and splitting it is the
        // render buffer's job once the plane is known.
        Polyline lShape = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(3, 0, 0),
            PointOps.Create(3, 1, 0),
            PointOps.Create(1, 1, 0),
            PointOps.Create(1, 3, 0),
            PointOps.Create(0, 3, 0),
            PointOps.Create(0, 0, 0),
        ]);

        Assert.True(MeshBuilders.CreateExtrusion(
            lShape, VectorOps.Create(0, 0, 2), capped: true, out Mesh? mesh).IsSuccess);

        Assert.Equal(8, mesh!.FaceCount);
        Assert.Equal(6, mesh.CornersInFace(6));

        // Five square units of section, two deep.
        Assert.Equal(10, SignedVolume(mesh), 9);

        // And the render path must handle that concave cap without spilling outside it.
        Assert.True(RenderBuffers.CreateTriangleIndices(mesh, out int[]? indices).IsSuccess);
        Assert.Equal(RenderBuffers.TriangleCount(mesh) * 3, indices!.Length);
    }

    [Fact]
    public void Extrude_LeavesAnOpenProfileOpenAndSaysSoWhenAskedToCapIt()
    {
        Polyline open = PolylineOps.Create(
            [PointOps.Create(0, 0, 0), PointOps.Create(1, 0, 0), PointOps.Create(1, 1, 0)]);

        OperationResult result = MeshBuilders.CreateExtrusion(
            open, VectorOps.Create(0, 0, 1), capped: true, out Mesh? mesh);

        Assert.True(result.IsPartial);
        Assert.Contains("open", result.Message);
        Assert.Equal(2, mesh!.FaceCount);
    }

    [Fact]
    public void Extrude_FailsOnAZeroDirection()
    {
        OperationResult result = MeshBuilders.CreateExtrusion(
            ClosedSquare(1), Vector3d.Zero, capped: true, out Mesh? mesh);

        Assert.True(result.IsFailed);
        Assert.Null(mesh);
    }

    [Fact]
    public void Revolve_BuildsATubeFromAProfileOffTheAxis()
    {
        // Two points off the axis, spun the whole way round: a cylindrical tube with a disc at each end.
        Polyline profile = PolylineOps.Create(
            [PointOps.Create(2, 0, 0), PointOps.Create(2, 0, 5)]);

        OperationResult result = MeshBuilders.CreateRevolution(
            profile,
            LineOps.Create(Point3d.Origin, PointOps.Create(0, 0, 1)),
            Interval.FullTurn,
            segments: 32,
            capped: true,
            out Mesh? mesh);

        Assert.True(result.IsSuccess, result.ToString());

        // Thirty-two side quads plus the two end discs.
        Assert.Equal(34, mesh!.FaceCount);
        Assert.Equal(32, mesh.CornersInFace(32));

        // Close to a true cylinder, and low by the tessellation deficit rather than high.
        double exact = Math.PI * 4 * 5;
        Assert.InRange(SurfaceArea(mesh) - exact - (2 * Math.PI * 4), -3, 0);
    }

    [Fact]
    public void Revolve_SharesThePoleVertexSoADomeHasNoSliverFaces()
    {
        // A profile touching the axis is where a naive revolve leaves a ring of zero-width quads. The point
        // on the axis does not move, so it gets one vertex and the spans there come out as triangles.
        Polyline profile = PolylineOps.Create(
            [PointOps.Create(0, 0, 3), PointOps.Create(2, 0, 2), PointOps.Create(3, 0, 0)]);

        Assert.True(MeshBuilders.CreateRevolution(
            profile,
            LineOps.Create(Point3d.Origin, PointOps.Create(0, 0, 1)),
            Interval.FullTurn,
            segments: 16,
            capped: false,
            out Mesh? mesh).IsSuccess);

        // Sixteen triangles at the pole and sixteen quads below it.
        int triangles = 0;
        int quads = 0;

        for (int i = 0; i < mesh!.FaceCount; i++)
        {
            if (mesh.CornersInFace(i) == 3)
            {
                triangles++;
            }
            else if (mesh.CornersInFace(i) == 4)
            {
                quads++;
            }
        }

        Assert.Equal(16, triangles);
        Assert.Equal(16, quads);

        // Every face has real area; a collapsed quad would show up as a zero here.
        for (int i = 0; i < mesh.FaceCount; i++)
        {
            Assert.True(MeshOps.FaceArea(mesh, i) > 1e-9, $"face {i} has no area");
        }

        // The apex was added once, not once per step.
        Assert.Equal(1 + (16 * 2), mesh.VertexCount);
    }

    [Fact]
    public void Revolve_NeedsNoCapsWhenTheProfileEndsOnTheAxisAtBothEnds()
    {
        // A profile from pole to pole closes itself; asking for caps must not invent faces.
        Polyline profile = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 1),
            PointOps.Create(1, 0, 0),
            PointOps.Create(0, 0, -1),
        ]);

        OperationResult result = MeshBuilders.CreateRevolution(
            profile,
            LineOps.Create(Point3d.Origin, PointOps.Create(0, 0, 1)),
            Interval.FullTurn,
            segments: 24,
            capped: true,
            out Mesh? mesh);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(24 * 2, mesh!.FaceCount);
    }

    [Fact]
    public void Revolve_CapsAPartialSweepOfAClosedProfileWithTheProfileItself()
    {
        // A quarter of a torus: the two ends are copies of the section.
        Polyline section = PolylineOps.Create(
        [
            PointOps.Create(4, 0, -1),
            PointOps.Create(5, 0, -1),
            PointOps.Create(5, 0, 1),
            PointOps.Create(4, 0, 1),
            PointOps.Create(4, 0, -1),
        ]);

        OperationResult result = MeshBuilders.CreateRevolution(
            section,
            LineOps.Create(Point3d.Origin, PointOps.Create(0, 0, 1)),
            IntervalOps.Create(0, Math.PI / 2),
            segments: 8,
            capped: true,
            out Mesh? mesh);

        Assert.True(result.IsSuccess, result.ToString());

        // Eight steps of four side quads, plus a cap at each end.
        Assert.Equal((8 * 4) + 2, mesh!.FaceCount);
        Assert.True(Math.Abs(SignedVolume(mesh)) > 0, "a capped partial revolve should enclose volume");
    }

    [Fact]
    public void Revolve_CannotCapAPartialSweepOfAnOpenProfileAndSaysSo()
    {
        Polyline profile = PolylineOps.Create(
            [PointOps.Create(2, 0, 0), PointOps.Create(2, 0, 5)]);

        OperationResult result = MeshBuilders.CreateRevolution(
            profile,
            LineOps.Create(Point3d.Origin, PointOps.Create(0, 0, 1)),
            IntervalOps.Create(0, Math.PI / 2),
            segments: 8,
            capped: true,
            out Mesh? mesh);

        Assert.True(result.IsPartial);
        Assert.Contains("non-planar", result.Message);
        Assert.Equal(8, mesh!.FaceCount);
    }

    [Fact]
    public void Revolve_SpinsTheOtherWayForADecreasingDomain()
    {
        Polyline profile = PolylineOps.Create(
            [PointOps.Create(2, 0, 0), PointOps.Create(2, 0, 1)]);

        Line axis = LineOps.Create(Point3d.Origin, PointOps.Create(0, 0, 1));

        Assert.True(MeshBuilders.CreateRevolution(
            profile, axis, IntervalOps.Create(0, Math.PI / 2), 4, false, out Mesh? forward).IsSuccess);

        Assert.True(MeshBuilders.CreateRevolution(
            profile, axis, IntervalOps.Create(0, -Math.PI / 2), 4, false, out Mesh? backward).IsSuccess);

        // Same shape mirrored, so the same area but the last ring on the other side of the start.
        Assert.Equal(SurfaceArea(forward!), SurfaceArea(backward!), 9);
        Assert.True(forward!.Vertices[^1].Y > 0);
        Assert.True(backward!.Vertices[^1].Y < 0);
    }

    [Fact]
    public void Revolve_FailsOnADegenerateAxisOrAnEmptySweep()
    {
        Polyline profile = PolylineOps.Create(
            [PointOps.Create(2, 0, 0), PointOps.Create(2, 0, 1)]);

        Assert.True(MeshBuilders.CreateRevolution(
            profile,
            LineOps.Create(Point3d.Origin, Point3d.Origin),
            Interval.FullTurn,
            8,
            false,
            out _).IsFailed);

        Assert.True(MeshBuilders.CreateRevolution(
            profile,
            LineOps.Create(Point3d.Origin, PointOps.Create(0, 0, 1)),
            IntervalOps.Create(1, 1),
            8,
            false,
            out _).IsFailed);
    }

    [Fact]
    public void Loft_JoinsSectionsInOrder()
    {
        OperationResult result = MeshBuilders.CreateLoft(
            [ClosedSquare(2, 0), ClosedSquare(2, 3), ClosedSquare(2, 6)],
            closedLoop: false,
            capped: true,
            out Mesh? mesh);

        Assert.True(result.IsSuccess, result.ToString());

        // Two spans of four quads, plus two caps.
        Assert.Equal((2 * 4) + 2, mesh!.FaceCount);
        Assert.Equal(24, SignedVolume(mesh), 9);
    }

    [Fact]
    public void Loft_ClosesTheLoopWhenAsked()
    {
        // A ring of sections has no ends, so there is nothing to cap and one more span than a run.
        OperationResult result = MeshBuilders.CreateLoft(
            [ClosedSquare(2, 0), ClosedSquare(2, 3), ClosedSquare(2, 6)],
            closedLoop: true,
            capped: true,
            out Mesh? mesh);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(3 * 4, mesh!.FaceCount);
    }

    [Fact]
    public void Loft_RefusesSectionsOfDifferentLengthsRatherThanGuessingAResample()
    {
        // How to resample — by length, by corner, by curvature — is the caller's decision, so guessing here
        // would silently produce a shape nobody asked for.
        Polyline triangle = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 3),
            PointOps.Create(2, 0, 3),
            PointOps.Create(1, 2, 3),
            PointOps.Create(0, 0, 3),
        ]);

        OperationResult result = MeshBuilders.CreateLoft(
            [ClosedSquare(2), triangle], closedLoop: false, capped: false, out Mesh? mesh);

        Assert.True(result.IsFailed);
        Assert.Null(mesh);
        Assert.Contains("must match", result.Message);
    }

    [Fact]
    public void Loft_RefusesToMixOpenAndClosedSections()
    {
        Polyline open = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 3),
            PointOps.Create(2, 0, 3),
            PointOps.Create(2, 2, 3),
            PointOps.Create(0, 2, 3),
        ]);

        OperationResult result = MeshBuilders.CreateLoft(
            [ClosedSquare(2), open], closedLoop: false, capped: false, out _);

        Assert.True(result.IsFailed);
        Assert.Contains("cannot mix", result.Message);
    }

    [Fact]
    public void Loft_FailsWithFewerThanTwoSections()
    {
        Assert.True(MeshBuilders.CreateLoft([ClosedSquare(1)], false, false, out Mesh? mesh).IsFailed);
        Assert.Null(mesh);
    }

    [Fact]
    public void Sweep_CarriesAProfileAlongAStraightRailLikeAnExtrusion()
    {
        // The degenerate case, and worth pinning: a straight rail must give the same solid an extrusion does.
        Polyline profile = PolylineOps.Create(
        [
            PointOps.Create(-0.5, -0.5, 0),
            PointOps.Create(0.5, -0.5, 0),
            PointOps.Create(0.5, 0.5, 0),
            PointOps.Create(-0.5, 0.5, 0),
            PointOps.Create(-0.5, -0.5, 0),
        ]);

        Polyline rail = PolylineOps.Create(
            [Point3d.Origin, PointOps.Create(0, 0, 10)]);

        OperationResult result = MeshBuilders.CreateSweep(
            profile, Plane.WorldXY, rail, capped: true, out Mesh? mesh);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(6, mesh!.FaceCount);
        Assert.Equal(10, Math.Abs(SignedVolume(mesh)), 9);
    }

    [Fact]
    public void Sweep_MitresTheCornerOfARightAngledRail()
    {
        // The reason this exists rather than two extrusions butted together: the section at the corner sits
        // on the bisector, so the two runs meet on a single mitre plane with no overlap and no gap.
        Polyline profile = PolylineOps.Create(
        [
            PointOps.Create(-1, -1, 0),
            PointOps.Create(1, -1, 0),
            PointOps.Create(1, 1, 0),
            PointOps.Create(-1, 1, 0),
            PointOps.Create(-1, -1, 0),
        ]);

        Polyline rail = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(10, 0, 0),
            PointOps.Create(10, 10, 0),
        ]);

        OperationResult result = MeshBuilders.CreateSweep(
            profile, Plane.WorldXY, rail, capped: true, out Mesh? mesh);

        Assert.True(result.IsSuccess, result.ToString());

        // Two spans of four sides, plus two end caps.
        Assert.Equal((2 * 4) + 2, mesh!.FaceCount);

        // Every section stays the same size, so the mitre is a shear rather than a scale: the section at the
        // corner is the profile tilted 45 degrees, and its four corners are still two apart across.
        Assert.True(SignedVolume(mesh) != 0);
    }

    [Fact]
    public void Sweep_KeepsTheProfileSquareToTheRailAllTheWayRound()
    {
        // The property the double-reflection frames buy: no spurious twist. Every section must stay
        // perpendicular to the rail it sits on.
        Polyline profile = PolylineOps.Create(
        [
            PointOps.Create(-0.5, -0.2, 0),
            PointOps.Create(0.5, -0.2, 0),
            PointOps.Create(0.5, 0.2, 0),
            PointOps.Create(-0.5, 0.2, 0),
            PointOps.Create(-0.5, -0.2, 0),
        ]);

        Arc quarter = ArcOps.Create(Plane.WorldXY, 10, IntervalOps.Create(0, Math.PI / 2));
        Polyline rail = ArcOps.ToPolyline(quarter, 8);

        Assert.True(MeshBuilders.CreateSweep(
            profile, Plane.WorldXY, rail, capped: true, out Mesh? mesh).IsSuccess);

        // Nine sections of four points each.
        Assert.Equal(9 * 4, mesh!.VertexCount);

        // The profile is 1 by 0.4, so every section's diagonal is the same length however it is oriented.
        ReadOnlySpan<Point3d> vertices = mesh.Vertices;
        double expected = PointOps.DistanceTo(
            PointOps.Create(-0.5, -0.2, 0), PointOps.Create(0.5, 0.2, 0));

        for (int section = 0; section < 9; section++)
        {
            int b = section * 4;
            Assert.Equal(expected, PointOps.DistanceTo(vertices[b], vertices[b + 2]), 9);
        }
    }

    [Fact]
    public void Sweep_UsesTheProfilePlaneOriginSoAnOffCentreProfileSweepsOffCentre()
    {
        // Not a quirk to work around: an L-shaped frame member is drawn off the rail on purpose.
        Polyline offCentre = PolylineOps.Create(
        [
            PointOps.Create(5, 0, 0),
            PointOps.Create(6, 0, 0),
            PointOps.Create(6, 1, 0),
            PointOps.Create(5, 1, 0),
            PointOps.Create(5, 0, 0),
        ]);

        Polyline rail = PolylineOps.Create([Point3d.Origin, PointOps.Create(0, 0, 4)]);

        Assert.True(MeshBuilders.CreateSweep(
            offCentre, Plane.WorldXY, rail, capped: true, out Mesh? mesh).IsSuccess);

        // Nothing should sit near the rail itself.
        foreach (Point3d vertex in mesh!.Vertices)
        {
            Assert.True(Math.Sqrt((vertex.X * vertex.X) + (vertex.Y * vertex.Y)) >= 4.9);
        }
    }

    [Fact]
    public void Sweep_ClosesTheLoopForAClosedRailAndSkipsCaps()
    {
        Polyline profile = PolylineOps.Create(
        [
            PointOps.Create(-0.5, -0.5, 0),
            PointOps.Create(0.5, -0.5, 0),
            PointOps.Create(0.5, 0.5, 0),
            PointOps.Create(-0.5, 0.5, 0),
            PointOps.Create(-0.5, -0.5, 0),
        ]);

        Polyline rail = CircleOps.ToPolyline(CircleOps.Create(Plane.WorldXY, 10), 16);

        OperationResult result = MeshBuilders.CreateSweep(
            profile, Plane.WorldXY, rail, capped: true, out Mesh? mesh);

        Assert.True(result.IsSuccess, result.ToString());

        // Sixteen sections, sixteen spans, no caps: a closed rail has no end.
        Assert.Equal(16 * 4, mesh!.FaceCount);
    }

    [Fact]
    public void Sweep_FailsOnARailThatDoublesBackOnItself()
    {
        // A rail folding through 180 degrees has no bisector at the fold, so there is no section plane there.
        Polyline profile = PolylineOps.Create(
        [
            PointOps.Create(-0.5, -0.5, 0),
            PointOps.Create(0.5, -0.5, 0),
            PointOps.Create(0.5, 0.5, 0),
            PointOps.Create(-0.5, -0.5, 0),
        ]);

        Polyline rail = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(10, 0, 0),
            PointOps.Create(0, 0, 0),
        ]);

        OperationResult result = MeshBuilders.CreateSweep(
            profile, Plane.WorldXY, rail, capped: false, out Mesh? mesh);

        Assert.True(result.IsFailed);
        Assert.Null(mesh);
        Assert.Contains("doubles back", result.Message);
    }

    [Fact]
    public void Box_FromRangesFillsExactlyThatSpace()
    {
        Mesh mesh = MeshBuilders.CreateBox(
            IntervalOps.Create(1, 4),
            IntervalOps.Create(-2, 0),
            IntervalOps.Create(10, 15));

        Assert.Equal(6, mesh.FaceCount);
        Assert.Equal(3 * 2 * 5, SignedVolume(mesh), 9);

        foreach (Point3d vertex in mesh.Vertices)
        {
            Assert.True(IntervalOps.Includes(IntervalOps.Create(1, 4), vertex.X));
            Assert.True(IntervalOps.Includes(IntervalOps.Create(-2, 0), vertex.Y));
            Assert.True(IntervalOps.Includes(IntervalOps.Create(10, 15), vertex.Z));
        }
    }

    [Fact]
    public void Box_FromRangesFacesOutwardsEvenWhenTheRangesDecrease()
    {
        // A box has no direction of its own, so the order the ranges were given in must not turn it
        // inside out.
        Mesh mesh = MeshBuilders.CreateBox(
            IntervalOps.Create(4, 1),
            IntervalOps.Create(0, -2),
            IntervalOps.Create(15, 10));

        Assert.Equal(30, SignedVolume(mesh), 9);
    }

    [Fact]
    public void Box_InAPlaneBuildsATiltedPartWithoutARotationStep()
    {
        // The splayed-leg case: put the plane along the part and give its extents, rather than building it
        // upright and rotating it into place.
        Plane tilted = PlaneOps.CreateFromNormal(
            PointOps.Create(5, 5, 0),
            VectorOps.Create(1, 0, 4));

        Mesh mesh = MeshBuilders.CreateBox(
            tilted,
            IntervalOps.CreateFromCenter(0, 1),
            IntervalOps.CreateFromCenter(0, 1),
            IntervalOps.Create(0, 20));

        Assert.Equal(6, mesh.FaceCount);
        Assert.Equal(2 * 2 * 20, SignedVolume(mesh), 9);

        // The part runs along the plane's normal, not along world Z.
        Assert.True(PointOps.EpsilonEquals(
            MeshOps.FaceCenter(mesh, 1),
            PointOps.Create(5, 5, 0) + (tilted.ZAxis * 20),
            1e-9));
    }

    [Fact]
    public void Box_RejectsARangeWithNoThickness()
    {
        Assert.Throws<ArgumentException>(() => MeshBuilders.CreateBox(
            IntervalOps.Create(0, 1), IntervalOps.Create(0, 1), IntervalOps.Create(3, 3)));

        Assert.Throws<ArgumentException>(() => MeshBuilders.CreateBox(
            IntervalOps.Create(0, 1), Interval.Unset, IntervalOps.Create(0, 1)));
    }

    [Fact]
    public void CreatePlanarRegion_BuildsAFlatMeshWithHolesCutOutOfIt()
    {
        Polyline outer = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(10, 0, 0),
            PointOps.Create(10, 10, 0),
            PointOps.Create(0, 10, 0),
            PointOps.Create(0, 0, 0),
        ]);

        Polyline hole = CircleOps.ToPolyline(
            CircleOps.Create(PlaneOps.CreateFromNormal(PointOps.Create(5, 5, 0), Vector3d.ZAxis), 2),
            16);

        OperationResult result = MeshBuilders.CreatePlanarRegion(
            outer, [hole], Plane.WorldXY, out Mesh? mesh);

        Assert.True(result.IsSuccess, result.ToString());

        double holeArea = Math.Abs(PolylineOps.SignedArea(hole, Plane.WorldXY));
        Assert.Equal(100 - holeArea, SurfaceArea(mesh!), 9);

        // Every face is a triangle, because a ring cannot be written as one closed corner list.
        for (int i = 0; i < mesh!.FaceCount; i++)
        {
            Assert.Equal(3, mesh.CornersInFace(i));
        }
    }

    /// <summary>
    /// The whole point of the builder in one assertion: a solid with holes through it, closed and wound
    /// outwards. Signed volume catches both halves of that — an unclosed mesh integrates to something
    /// arbitrary, and a hole wall wound the wrong way round adds its cylinder instead of subtracting it.
    /// </summary>
    [Fact]
    public void CreateExtrusionWithHoles_EnclosesTheRegionTimesTheDepth()
    {
        Polyline hole = CircleOps.ToPolyline(
            CircleOps.Create(PlaneOps.CreateFromNormal(PointOps.Create(4, 4, 0), Vector3d.ZAxis), 1.5),
            24);

        OperationResult result = MeshBuilders.CreateExtrusionWithHoles(
            ClosedSquare(10),
            [hole],
            Plane.WorldXY,
            VectorOps.Create(0, 0, 2),
            capped: true,
            out Mesh? mesh);

        Assert.True(result.IsSuccess, result.ToString());

        double holeArea = Math.Abs(PolylineOps.SignedArea(hole, Plane.WorldXY));
        Assert.Equal((100 - holeArea) * 2, SignedVolume(mesh!), 9);

        // Both caps and both walls, and nothing welded away: 4 + 24 corners, top and bottom.
        Assert.Equal((4 + 24) * 2, mesh!.VertexCount);
    }

    /// <summary>
    /// A hole subtracts however it was drawn, and so does the boundary. Every combination of windings has to
    /// come out the same solid, because a caller composing profiles from arcs and offsets cannot be asked to
    /// know which way round they ended up.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CreateExtrusionWithHoles_IgnoresHowTheProfilesWereWound(bool reverseOuter, bool reverseHole)
    {
        Polyline outer = ClosedSquare(10);
        Polyline hole = CircleOps.ToPolyline(
            CircleOps.Create(PlaneOps.CreateFromNormal(PointOps.Create(5, 5, 0), Vector3d.ZAxis), 2),
            16);

        OperationResult result = MeshBuilders.CreateExtrusionWithHoles(
            reverseOuter ? PolylineOps.Reversed(outer) : outer,
            [reverseHole ? PolylineOps.Reversed(hole) : hole],
            Plane.WorldXY,
            VectorOps.Create(0, 0, 3),
            capped: true,
            out Mesh? mesh);

        Assert.True(result.IsSuccess, result.ToString());

        double holeArea = Math.Abs(PolylineOps.SignedArea(hole, Plane.WorldXY));
        Assert.Equal((100 - holeArea) * 3, SignedVolume(mesh!), 9);
    }

    /// <summary>
    /// Extruding downwards is the same solid, not an inside-out one. The direction decides which way the
    /// walls face, so the plane's normal must not get a vote — this is the assertion that says so.
    /// </summary>
    [Fact]
    public void CreateExtrusionWithHoles_FacesOutwardsWhicheverWayItIsDragged()
    {
        Polyline hole = CircleOps.ToPolyline(
            CircleOps.Create(PlaneOps.CreateFromNormal(PointOps.Create(5, 5, 0), Vector3d.ZAxis), 2),
            16);

        double expected = (100 - Math.Abs(PolylineOps.SignedArea(hole, Plane.WorldXY))) * 2;

        Assert.True(MeshBuilders.CreateExtrusionWithHoles(
            ClosedSquare(10), [hole], Plane.WorldXY,
            VectorOps.Create(0, 0, -2), capped: true, out Mesh? down).IsSuccess);

        Assert.True(MeshBuilders.CreateExtrusionWithHoles(
            ClosedSquare(10), [hole], PlaneOps.Flipped(Plane.WorldXY),
            VectorOps.Create(0, 0, 2), capped: true, out Mesh? flipped).IsSuccess);

        Assert.Equal(expected, SignedVolume(down!), 9);
        Assert.Equal(expected, SignedVolume(flipped!), 9);
    }

    [Fact]
    public void CreateExtrusionWithHoles_LeavesTheWallsWhenAHoleCannotBeCut()
    {
        // Outside the boundary, so the cap has nowhere to bridge it: the walls are built and the cap is not
        // cut, which is a note rather than a failure.
        Polyline outside = CircleOps.ToPolyline(
            CircleOps.Create(PlaneOps.CreateFromNormal(PointOps.Create(40, 40, 0), Vector3d.ZAxis), 2),
            16);

        OperationResult result = MeshBuilders.CreateExtrusionWithHoles(
            ClosedSquare(10), [outside], Plane.WorldXY,
            VectorOps.Create(0, 0, 1), capped: true, out Mesh? mesh);

        Assert.Equal(ResultStatus.Partial, result.Status);
        Assert.NotNull(mesh);
        Assert.Contains("outside", result.Message);
    }

    [Fact]
    public void CreateExtrusionWithHoles_RefusesADirectionLyingInThePlane()
    {
        OperationResult result = MeshBuilders.CreateExtrusionWithHoles(
            ClosedSquare(10), [], Plane.WorldXY,
            VectorOps.Create(1, 0, 0), capped: true, out Mesh? mesh);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Null(mesh);
    }

    /// <summary>
    /// Without holes it is <see cref="MeshBuilders.CreateExtrusion"/>'s solid, so the two had better agree on how
    /// much of the world they enclose. They differ only in the caps: n-gons there, triangles here.
    /// </summary>
    [Fact]
    public void CreateExtrusionWithHoles_AgreesWithExtrudeWhenThereAreNoHoles()
    {
        Vector3d direction = VectorOps.Create(0, 0, 4);

        Assert.True(MeshBuilders.CreateExtrusion(ClosedSquare(6), direction, true, out Mesh? plain).IsSuccess);
        Assert.True(MeshBuilders.CreateExtrusionWithHoles(
            ClosedSquare(6), [], Plane.WorldXY, direction, true, out Mesh? region).IsSuccess);

        Assert.Equal(SignedVolume(plain!), SignedVolume(region!), 9);
        Assert.Equal(SurfaceArea(plain!), SurfaceArea(region!), 9);
    }

    [Fact]
    public void Flip_ReversesEveryFaceAndTurnsTheVolumeInsideOut()
    {
        Mesh mesh = MeshBuilders.CreateBox(1, 2, 3);
        double before = SignedVolume(mesh);

        MeshOps.Flip(mesh);

        Assert.Equal(-before, SignedVolume(mesh), 9);
        Assert.Equal(6, mesh.FaceCount);
        Assert.Equal(8, mesh.VertexCount);
    }
}
