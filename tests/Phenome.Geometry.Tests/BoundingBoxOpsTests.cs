namespace Phenome.Geometry.Tests;

public class BoundingBoxOpsTests
{
    private static BoundingBox UnitCube => BoundingBox.Unit;

    [Fact]
    public void Create_SortsTheRangesSoABoxCannotBeInsideOut()
    {
        BoundingBox box = BoundingBoxOps.Create(
            IntervalOps.Create(4, 1),
            IntervalOps.Create(0, -2),
            IntervalOps.Create(15, 10));

        Assert.Equal(IntervalOps.Create(1, 4), box.X);
        Assert.Equal(IntervalOps.Create(-2, 0), box.Y);
        Assert.Equal(IntervalOps.Create(10, 15), box.Z);
    }

    [Fact]
    public void Create_FromTwoCornersDoesNotCareWhichIsWhich()
    {
        Point3d a = PointOps.Create(5, -1, 9);
        Point3d b = PointOps.Create(-3, 4, 2);

        Assert.Equal(
            BoundingBoxOps.Create(a, b),
            BoundingBoxOps.Create(b, a));

        Assert.True(PointOps.EpsilonEquals(
            BoundingBoxOps.Min(BoundingBoxOps.Create(a, b)),
            PointOps.Create(-3, -1, 2),
            1e-12));
    }

    [Fact]
    public void CreateFromCenter_IgnoresTheSignOfTheExtents()
    {
        BoundingBox box = BoundingBoxOps.CreateFromCenter(PointOps.Create(10, 10, 10), -4, 4, -6);

        Assert.True(PointOps.EpsilonEquals(
            BoundingBoxOps.Size(box), PointOps.Create(4, 4, 6), 1e-12));

        Assert.True(PointOps.EpsilonEquals(
            BoundingBoxOps.Center(box), PointOps.Create(10, 10, 10), 1e-12));
    }

    [Fact]
    public void Bound_OfNoPointsIsUnsetSoAccumulationNeedsNoSpecialCase()
    {
        BoundingBox empty = BoundingBoxOps.Bound(ReadOnlySpan<Point3d>.Empty);

        Assert.False(BoundingBoxOps.IsValid(empty));
        Assert.Equal(BoundingBox.Unset, empty);

        // Growing the unset box by one point gives the box of that point, which is what makes a running bound
        // work without checking whether it is the first item.
        BoundingBox one = BoundingBoxOps.Grown(empty, PointOps.Create(3, 4, 5));

        Assert.True(BoundingBoxOps.IsValid(one));
        Assert.True(PointOps.EpsilonEquals(
            BoundingBoxOps.Min(one), BoundingBoxOps.Max(one), 1e-12));
    }

    [Fact]
    public void Bound_CoversEveryPointGiven()
    {
        BoundingBox box = BoundingBoxOps.Bound(
        [
            PointOps.Create(1, 5, -2),
            PointOps.Create(-4, 0, 7),
            PointOps.Create(3, 3, 3),
        ]);

        Assert.True(PointOps.EpsilonEquals(
            BoundingBoxOps.Min(box), PointOps.Create(-4, 0, -2), 1e-12));

        Assert.True(PointOps.EpsilonEquals(
            BoundingBoxOps.Max(box), PointOps.Create(3, 5, 7), 1e-12));
    }

    [Fact]
    public void Bound_OfAMeshMatchesTheBoxItWasBuiltFrom()
    {
        // The round trip that makes this usable for assembly: measure a part, and the measurement is exact.
        BoundingBox range = BoundingBoxOps.Create(
            IntervalOps.Create(2, 7),
            IntervalOps.Create(-1, 4),
            IntervalOps.Create(0, 3));

        Mesh mesh = MeshBuilders.CreateBox(range);

        Assert.True(BoundingBoxOps.EpsilonEquals(range, BoundingBoxOps.Bound(mesh)));
    }

    [Fact]
    public void Bound_OfAPolylineCoversItsPoints()
    {
        Polyline polyline = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(10, 0, 0),
            PointOps.Create(10, 4, 0),
        ]);

        BoundingBox box = BoundingBoxOps.Bound(polyline);

        Assert.Equal(0, IntervalOps.Length(box.Z), 12);
        Assert.Equal(10, IntervalOps.Length(box.X), 12);
        Assert.Equal(4, IntervalOps.Length(box.Y), 12);
    }

    [Fact]
    public void Bound_OfSeveralBoxesIsTheirUnion()
    {
        BoundingBox total = BoundingBoxOps.Bound(
        [
            BoundingBoxOps.Create(PointOps.Create(0, 0, 0), PointOps.Create(1, 1, 1)),
            BoundingBox.Unset,
            BoundingBoxOps.Create(PointOps.Create(5, 5, 5), PointOps.Create(6, 6, 6)),
        ]);

        Assert.True(PointOps.EpsilonEquals(
            BoundingBoxOps.Max(total), PointOps.Create(6, 6, 6), 1e-12));
    }

    [Fact]
    public void Bound_OfACircleIsExactRatherThanTheBoxOfItsSphere()
    {
        // An axis-aligned circle is flat, so its box must be flat too. The box of the enclosing sphere would
        // be a cube, and that error is what makes culling and layout drift.
        Circle circle = CircleOps.Create(Plane.WorldXY, 5);
        BoundingBox box = BoundingBoxOps.Bound(circle);

        Assert.Equal(10, IntervalOps.Length(box.X), 12);
        Assert.Equal(10, IntervalOps.Length(box.Y), 12);
        Assert.Equal(0, IntervalOps.Length(box.Z), 12);
    }

    [Fact]
    public void Bound_OfATiltedCircleLeansTheRightAmount()
    {
        // A circle tilted forty-five degrees about X reaches the full radius in X, and the radius over root
        // two in Y and Z.
        Plane tilted = PlaneOps.CreateFromAxes(
            Point3d.Origin, Vector3d.XAxis, VectorOps.Create(0, 1, 1));

        BoundingBox box = BoundingBoxOps.Bound(CircleOps.Create(tilted, 4));

        Assert.Equal(8, IntervalOps.Length(box.X), 9);
        Assert.Equal(8 / Math.Sqrt(2), IntervalOps.Length(box.Y), 9);
        Assert.Equal(8 / Math.Sqrt(2), IntervalOps.Length(box.Z), 9);

        // And it really does contain the circle, checked against the curve itself.
        for (int i = 0; i < 64; i++)
        {
            Point3d point = CircleOps.PointAtNormalized(CircleOps.Create(tilted, 4), i / 64.0);
            Assert.True(BoundingBoxOps.Contains(box, point), $"point {i} fell outside the box");
        }
    }

    [Fact]
    public void IsValid_AcceptsAFlatBoxBecauseAPlanarThingHasOne()
    {
        BoundingBox flat = BoundingBoxOps.Create(
            IntervalOps.Create(0, 10), IntervalOps.Create(0, 10), IntervalOps.Create(3, 3));

        Assert.True(BoundingBoxOps.IsValid(flat));
        Assert.True(BoundingBoxOps.IsDegenerate(flat));
        Assert.Equal(0, BoundingBoxOps.Volume(flat), 12);

        Assert.False(BoundingBoxOps.IsValid(BoundingBox.Unset));
        Assert.True(BoundingBoxOps.IsDegenerate(BoundingBox.Unset));
    }

    [Fact]
    public void SizeAndVolumeAndSurfaceArea_MatchTheRanges()
    {
        BoundingBox box = BoundingBoxOps.Create(
            IntervalOps.Create(0, 2), IntervalOps.Create(0, 3), IntervalOps.Create(0, 4));

        Assert.True(PointOps.EpsilonEquals(BoundingBoxOps.Size(box), PointOps.Create(2, 3, 4), 1e-12));
        Assert.Equal(24, BoundingBoxOps.Volume(box), 12);
        Assert.Equal(2 * ((2 * 3) + (3 * 4) + (4 * 2)), BoundingBoxOps.SurfaceArea(box), 12);
        Assert.Equal(Math.Sqrt(4 + 9 + 16), BoundingBoxOps.DiagonalLength(box), 12);
    }

    [Fact]
    public void PointAt_PlacesSomethingRelativeToAPartWithoutKnowingItsSize()
    {
        // The middle of the top face, which is where a handle or a leg gets pinned.
        BoundingBox carcass = BoundingBoxOps.Create(
            IntervalOps.Create(0, 160), IntervalOps.Create(0, 40), IntervalOps.Create(0, 70));

        Assert.True(PointOps.EpsilonEquals(
            BoundingBoxOps.PointAt(carcass, 0.5, 0.5, 1),
            PointOps.Create(80, 20, 70),
            1e-12));

        // Not clamped, so it reaches outside on purpose.
        Assert.True(PointOps.EpsilonEquals(
            BoundingBoxOps.PointAt(carcass, 0.5, 0.5, 1.1),
            PointOps.Create(80, 20, 77),
            1e-12));
    }

    [Fact]
    public void Corners_AreInTheSameOrderTheBoxBuilderUses()
    {
        BoundingBox box = BoundingBoxOps.Create(
            IntervalOps.Create(0, 1), IntervalOps.Create(0, 2), IntervalOps.Create(0, 3));

        Point3d[] corners = BoundingBoxOps.Corners(box);
        Mesh mesh = MeshBuilders.CreateBox(box);

        Assert.Equal(8, corners.Length);

        for (int i = 0; i < 8; i++)
        {
            Assert.True(
                PointOps.EpsilonEquals(corners[i], mesh.Vertices[i], 1e-12),
                $"corner {i} does not match the mesh builder's vertex {i}");
        }
    }

    [Fact]
    public void Contains_CountsTheSurfaceByDefaultSoAFlatBoxHoldsSomething()
    {
        BoundingBox flat = BoundingBoxOps.Create(
            IntervalOps.Create(0, 10), IntervalOps.Create(0, 10), IntervalOps.Create(0, 0));

        Point3d onIt = PointOps.Create(5, 5, 0);

        Assert.True(BoundingBoxOps.Contains(flat, onIt));
        Assert.False(BoundingBoxOps.Contains(flat, onIt, includeSurface: false));
    }

    [Fact]
    public void Contains_RecognisesABoxInsideAnotherAndItself()
    {
        BoundingBox outer = BoundingBoxOps.Create(Point3d.Origin, PointOps.Create(10, 10, 10));
        BoundingBox inner = BoundingBoxOps.Create(
            PointOps.Create(1, 1, 1), PointOps.Create(2, 2, 2));

        Assert.True(BoundingBoxOps.Contains(outer, inner));
        Assert.True(BoundingBoxOps.Contains(outer, outer));
        Assert.False(BoundingBoxOps.Contains(inner, outer));
    }

    [Fact]
    public void Overlaps_CountsTouchingOnAFace()
    {
        BoundingBox left = BoundingBoxOps.Create(Point3d.Origin, PointOps.Create(1, 1, 1));
        BoundingBox flush = BoundingBoxOps.Create(
            PointOps.Create(1, 0, 0), PointOps.Create(2, 1, 1));

        BoundingBox apart = BoundingBoxOps.Create(
            PointOps.Create(1.5, 0, 0), PointOps.Create(2, 1, 1));

        Assert.True(BoundingBoxOps.Overlaps(left, flush));
        Assert.False(BoundingBoxOps.Overlaps(left, apart));
        Assert.False(BoundingBoxOps.Overlaps(left, BoundingBox.Unset));
    }

    [Fact]
    public void TryIntersection_GivesTheSharedBoxOrNull()
    {
        BoundingBox a = BoundingBoxOps.Create(Point3d.Origin, PointOps.Create(4, 4, 4));
        BoundingBox b = BoundingBoxOps.Create(
            PointOps.Create(2, 2, 2), PointOps.Create(9, 9, 9));

        Assert.True(BoundingBoxOps.TryIntersection(a, b, out BoundingBox? shared));
        Assert.Equal(8, BoundingBoxOps.Volume(shared.Value), 12);

        Assert.False(BoundingBoxOps.TryIntersection(
            a,
            BoundingBoxOps.Create(PointOps.Create(9, 9, 9), PointOps.Create(10, 10, 10)),
            out BoundingBox? none));

        Assert.Null(none);
    }

    [Fact]
    public void TryIntersection_OfBoxesTouchingOnAFaceGivesAFlatBox()
    {
        BoundingBox left = BoundingBoxOps.Create(Point3d.Origin, PointOps.Create(1, 1, 1));
        BoundingBox right = BoundingBoxOps.Create(
            PointOps.Create(1, 0, 0), PointOps.Create(2, 1, 1));

        Assert.True(BoundingBoxOps.TryIntersection(left, right, out BoundingBox? shared));
        Assert.True(BoundingBoxOps.IsDegenerate(shared.Value));
        Assert.Equal(0, IntervalOps.Length(shared.Value.X), 12);
    }

    [Fact]
    public void Union_TreatsAnUnsetBoxAsTheIdentity()
    {
        Assert.Equal(UnitCube, BoundingBoxOps.Union(UnitCube, BoundingBox.Unset));
        Assert.Equal(UnitCube, BoundingBoxOps.Union(BoundingBox.Unset, UnitCube));
        Assert.Equal(BoundingBox.Unset, BoundingBoxOps.Union(BoundingBox.Unset, BoundingBox.Unset));
    }

    [Fact]
    public void Inflated_GrowsEverySideAndShrinksForANegativeAmount()
    {
        BoundingBox grown = BoundingBoxOps.Inflated(UnitCube, 1);

        Assert.True(PointOps.EpsilonEquals(
            BoundingBoxOps.Min(grown), PointOps.Create(-1, -1, -1), 1e-12));

        Assert.True(PointOps.EpsilonEquals(
            BoundingBoxOps.Max(grown), PointOps.Create(2, 2, 2), 1e-12));

        BoundingBox shrunk = BoundingBoxOps.Inflated(UnitCube, -0.25);
        Assert.Equal(0.5, IntervalOps.Length(shrunk.X), 12);
    }

    [Fact]
    public void Inflated_CollapsesOntoTheCentreRatherThanTurningInsideOut()
    {
        // An inverted range would claim the box still holds something. Flat on the centre says the truth.
        BoundingBox collapsed = BoundingBoxOps.Inflated(UnitCube, -5);

        Assert.True(BoundingBoxOps.IsValid(collapsed));
        Assert.True(BoundingBoxOps.IsDegenerate(collapsed));
        Assert.Equal(0, BoundingBoxOps.Volume(collapsed), 12);

        Assert.True(PointOps.EpsilonEquals(
            BoundingBoxOps.Center(collapsed), PointOps.Create(0.5, 0.5, 0.5), 1e-12));
    }

    [Fact]
    public void ClosestPoint_LeavesAPointInsideWhereItIs()
    {
        // Not a projection onto the surface, which is what makes DistanceTo zero inside.
        Point3d inside = PointOps.Create(0.5, 0.5, 0.5);

        Assert.True(PointOps.EpsilonEquals(
            BoundingBoxOps.ClosestPoint(UnitCube, inside), inside, 1e-12));

        Assert.Equal(0, BoundingBoxOps.DistanceTo(UnitCube, inside), 12);
    }

    [Fact]
    public void ClosestPoint_ClampsOnEveryAxisAtOnce()
    {
        Point3d far = PointOps.Create(-3, 0.5, 9);

        Assert.True(PointOps.EpsilonEquals(
            BoundingBoxOps.ClosestPoint(UnitCube, far),
            PointOps.Create(0, 0.5, 1),
            1e-12));

        Assert.Equal(Math.Sqrt(9 + 64), BoundingBoxOps.DistanceTo(UnitCube, far), 12);
    }

    [Fact]
    public void Transform_CarriesAnAxisAlignedBoxExactlyThroughARigidMove()
    {
        TMatrix move = Transforms.Translate(VectorOps.Create(5, -2, 3));
        BoundingBox moved = BoundingBoxOps.Transform(UnitCube, move);

        Assert.True(PointOps.EpsilonEquals(
            BoundingBoxOps.Min(moved), PointOps.Create(5, -2, 3), 1e-12));

        Assert.Equal(1, BoundingBoxOps.Volume(moved), 12);
    }

    [Fact]
    public void Transform_GrowsUnderRotationAndThatIsUnavoidable()
    {
        // A box turned forty-five degrees does not fit in a box of the same size. The result contains the
        // rotated box but is bigger than the geometry inside it — which is why a chain of transforms should
        // go through the geometry, not through the box.
        BoundingBox rotated = BoundingBoxOps.Transform(
            UnitCube, Transforms.Rotate(Vector3d.ZAxis, Math.PI / 4));

        Assert.Equal(Math.Sqrt(2), IntervalOps.Length(rotated.X), 9);
        Assert.True(BoundingBoxOps.Volume(rotated) > BoundingBoxOps.Volume(UnitCube));

        // Rotating back does not undo the growth.
        BoundingBox andBack = BoundingBoxOps.Transform(
            rotated, Transforms.Rotate(Vector3d.ZAxis, -Math.PI / 4));

        Assert.True(BoundingBoxOps.Volume(andBack) > BoundingBoxOps.Volume(rotated));
    }

    [Fact]
    public void Transform_OfAnUnsetBoxStaysUnset()
    {
        Assert.False(BoundingBoxOps.IsValid(
            BoundingBoxOps.Transform(BoundingBox.Unset, Transforms.Translate(1, 2, 3))));
    }

    [Fact]
    public void ToString_NamesTheCornersAndSaysWhenItIsUnset()
    {
        Assert.Equal("BoundingBox(unset)", BoundingBox.Unset.ToString());
        Assert.Contains("..", UnitCube.ToString());
    }

    [Fact]
    public void Equality_TreatsUnsetAsEqualToItselfSoItWorksAsADictionaryKey()
    {
        Assert.True(BoundingBox.Unset.Equals(BoundingBox.Unset));
        Assert.False(BoundingBox.Unset == BoundingBox.Unset);

        Dictionary<BoundingBox, string> byBox = new() { [BoundingBox.Unset] = "unset" };
        Assert.Equal("unset", byBox[BoundingBox.Unset]);
    }

    [Fact]
    public void MeshBuilderBox_RejectsAFlatBoundingBoxBecauseItHasNoVolume()
    {
        BoundingBox flat = BoundingBoxOps.Create(
            IntervalOps.Create(0, 1), IntervalOps.Create(0, 1), IntervalOps.Create(2, 2));

        Assert.Throws<ArgumentException>(() => MeshBuilders.CreateBox(flat));
    }
}
