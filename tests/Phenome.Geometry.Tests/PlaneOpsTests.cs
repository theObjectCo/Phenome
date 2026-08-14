using Phenome.Geometry;

namespace Phenome.Geometry.Tests;

public class PlaneOpsTests
{
    private static readonly Plane Tilted = PlaneOps.CreateFromNormal(
        PointOps.Create(1, 2, 3),
        VectorOps.Create(1, 1, 1));

    [Fact]
    public void CreateFromAxes_KeepsTheXDirectionAndOrthonormalisesY()
    {
        // Y is deliberately not perpendicular to X; only its perpendicular component should survive.
        Plane plane = PlaneOps.CreateFromAxes(
            Point3d.Origin,
            VectorOps.Create(2, 0, 0),
            VectorOps.Create(3, 4, 0));

        Assert.True(VectorOps.EpsilonEquals(plane.XAxis, Vector3d.XAxis, 1e-12));
        Assert.True(VectorOps.EpsilonEquals(plane.YAxis, Vector3d.YAxis, 1e-12));
        Assert.True(VectorOps.EpsilonEquals(plane.ZAxis, Vector3d.ZAxis, 1e-12));
    }

    [Fact]
    public void CreateFromAxes_HonoursTheSideOfXThatYFallsOn()
    {
        // The previous implementation discarded Y and used X rotated a quarter turn, so a reversed Y
        // produced the same normal instead of the opposite one.
        Plane counterClockwise = PlaneOps.CreateFromAxes(
            Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis);

        Plane clockwise = PlaneOps.CreateFromAxes(
            Point3d.Origin, Vector3d.XAxis, -Vector3d.YAxis);

        Assert.True(VectorOps.EpsilonEquals(counterClockwise.Normal, Vector3d.ZAxis, 1e-12));
        Assert.True(VectorOps.EpsilonEquals(clockwise.Normal, -Vector3d.ZAxis, 1e-12));
    }

    [Fact]
    public void CreateFromAxes_SwappingTheArgumentsReversesTheNormal()
    {
        Vector3d x = VectorOps.Create(1, 2, 3);
        Vector3d y = VectorOps.Create(-3, 1, 1);

        Plane forward = PlaneOps.CreateFromAxes(Point3d.Origin, x, y);
        Plane swapped = PlaneOps.CreateFromAxes(Point3d.Origin, y, x);

        Assert.True(VectorOps.EpsilonEquals(forward.Normal, -swapped.Normal, 1e-12));
    }

    [Fact]
    public void CreateFromAxes_RejectsParallelOrDegenerateDirections()
    {
        Assert.Throws<ArgumentException>(() => PlaneOps.CreateFromAxes(
            Point3d.Origin, Vector3d.XAxis, Vector3d.XAxis * 3));

        Assert.False(PlaneOps.TryCreateFromAxes(
            Point3d.Origin, Vector3d.XAxis, -Vector3d.XAxis, out Plane? antiParallel));
        Assert.Null(antiParallel);

        Assert.False(PlaneOps.TryCreateFromAxes(
            Point3d.Origin, Vector3d.Zero, Vector3d.YAxis, out _));

        Assert.False(PlaneOps.TryCreateFromAxes(
            Point3d.Unset, Vector3d.XAxis, Vector3d.YAxis, out _));
    }

    [Fact]
    public void CreateFromNormal_MatchesTheRequestedNormal()
    {
        Vector3d normal = VectorOps.Create(1, 2, 3);
        Plane plane = PlaneOps.CreateFromNormal(PointOps.Create(4, 5, 6), normal);

        Assert.Equal(PointOps.Create(4, 5, 6), plane.Origin);
        Assert.True(VectorOps.EpsilonEquals(
            plane.Normal, VectorOps.Normalized(normal), 1e-12));
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(0, 0, -1)]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(1e-9, 1e-9, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(-3, 7, -0.5)]
    public void CreateFromNormal_StaysWellConditionedForEveryNormal(double x, double y, double z)
    {
        // The previous implementation compared the normal to world Z for exact equality when picking an
        // in-plane axis, so a normal a hair off Z produced a near-degenerate frame.
        Plane plane = PlaneOps.CreateFromNormal(Point3d.Origin, VectorOps.Create(x, y, z));

        Assert.True(PlaneOps.IsValid(plane));
        Assert.True(VectorOps.IsUnit(plane.XAxis, 1e-12));
        Assert.True(VectorOps.IsUnit(plane.YAxis, 1e-12));
        Assert.True(VectorOps.IsUnit(plane.ZAxis, 1e-12));
        Assert.True(VectorOps.IsPerpendicularTo(plane.XAxis, plane.YAxis, 1e-9));
        Assert.True(VectorOps.IsPerpendicularTo(plane.XAxis, plane.ZAxis, 1e-9));
        Assert.True(VectorOps.EpsilonEquals(
            VectorOps.Cross(plane.XAxis, plane.YAxis), plane.ZAxis, 1e-9));
    }

    [Fact]
    public void CreateFromNormal_RejectsADegenerateNormal()
    {
        Assert.Throws<ArgumentException>(
            () => PlaneOps.CreateFromNormal(Point3d.Origin, Vector3d.Zero));

        Assert.False(PlaneOps.TryCreateFromNormal(Point3d.Origin, Vector3d.Unset, out Plane? plane));
        Assert.Null(plane);
    }

    [Fact]
    public void CreateFromPoints_AnchorsAtTheFirstPointAndRunsXTowardsTheSecond()
    {
        Point3d a = PointOps.Create(1, 1, 0);
        Point3d b = PointOps.Create(4, 1, 0);
        Point3d c = PointOps.Create(1, 5, 0);

        Plane plane = PlaneOps.CreateFromPoints(a, b, c);

        Assert.Equal(a, plane.Origin);
        Assert.True(VectorOps.EpsilonEquals(plane.XAxis, Vector3d.XAxis, 1e-12));
        Assert.True(VectorOps.EpsilonEquals(plane.Normal, Vector3d.ZAxis, 1e-12));
        Assert.True(PlaneOps.Contains(plane, b, 1e-12));
        Assert.True(PlaneOps.Contains(plane, c, 1e-12));
    }

    [Fact]
    public void CreateFromPoints_ReversingTheWindingReversesTheNormal()
    {
        Point3d a = Point3d.Origin;
        Point3d b = PointOps.Create(1, 0, 0);
        Point3d c = PointOps.Create(0, 1, 0);

        Plane forward = PlaneOps.CreateFromPoints(a, b, c);
        Plane reversed = PlaneOps.CreateFromPoints(a, c, b);

        Assert.True(VectorOps.EpsilonEquals(forward.Normal, -reversed.Normal, 1e-12));
    }

    [Fact]
    public void CreateFromPoints_RejectsCollinearOrCoincidentPoints()
    {
        Assert.Throws<ArgumentException>(() => PlaneOps.CreateFromPoints(
            Point3d.Origin, PointOps.Create(1, 1, 1), PointOps.Create(2, 2, 2)));

        Assert.False(PlaneOps.TryCreateFromPoints(
            Point3d.Origin, Point3d.Origin, PointOps.Create(1, 0, 0), out Plane? coincident));

        Assert.Null(coincident);
    }

    [Fact]
    public void PointAt_WalksTheFrameAxes()
    {
        Assert.Equal(PointOps.Create(3, 4, 0), PlaneOps.PointAt(Plane.WorldXY, 3, 4));
        Assert.Equal(Point3d.Origin, PlaneOps.PointAt(Plane.WorldXY, 0, 0));

        // WorldYZ spans Y then Z, so its frame coordinates land there and not on X.
        Assert.Equal(PointOps.Create(0, 3, 4), PlaneOps.PointAt(Plane.WorldYZ, 3, 4));
    }

    [Fact]
    public void SignedDistanceTo_TellsTheSidesApart()
    {
        Assert.Equal(
            5.0,
            PlaneOps.SignedDistanceTo(Plane.WorldXY, PointOps.Create(9, -9, 5)),
            1e-12);

        Assert.Equal(
            -5.0,
            PlaneOps.SignedDistanceTo(Plane.WorldXY, PointOps.Create(9, -9, -5)),
            1e-12);

        Assert.Equal(
            0.0,
            PlaneOps.SignedDistanceTo(Plane.WorldXY, PointOps.Create(9, -9, 0)),
            1e-12);
    }

    [Fact]
    public void DistanceTo_IsTheUnsignedForm()
    {
        Point3d below = PointOps.Create(0, 0, -5);

        Assert.Equal(5.0, PlaneOps.DistanceTo(Plane.WorldXY, below), 1e-12);
        Assert.Equal(-5.0, PlaneOps.SignedDistanceTo(Plane.WorldXY, below), 1e-12);
    }

    [Fact]
    public void ClosestPoint_ProjectsAlongTheNormalAndLandsOnThePlane()
    {
        Assert.Equal(
            PointOps.Create(3, 4, 0),
            PlaneOps.ClosestPoint(Plane.WorldXY, PointOps.Create(3, 4, 17)));

        Point3d far = PointOps.Create(11, -7, 4);

        Assert.Equal(
            0.0,
            PlaneOps.DistanceTo(Tilted, PlaneOps.ClosestPoint(Tilted, far)),
            1e-12);
    }

    [Fact]
    public void ClosestParameter_RoundTripsThroughPointAt()
    {
        Point3d point = PointOps.Create(11, -7, 4);

        (double u, double v) = PlaneOps.ClosestParameter(Tilted, point);

        Assert.True(PointOps.EpsilonEquals(
            PlaneOps.PointAt(Tilted, u, v),
            PlaneOps.ClosestPoint(Tilted, point),
            1e-12));
    }

    [Fact]
    public void ClosestParameter_IsExactForAPointOnThePlane()
    {
        Point3d point = PlaneOps.PointAt(Tilted, 2.5, -1.25);

        (double u, double v) = PlaneOps.ClosestParameter(Tilted, point);

        Assert.Equal(2.5, u, 1e-12);
        Assert.Equal(-1.25, v, 1e-12);
    }

    [Fact]
    public void Flipped_ReversesTheNormalAndStaysRightHanded()
    {
        Plane flipped = PlaneOps.Flipped(Tilted);

        Assert.True(VectorOps.EpsilonEquals(flipped.Normal, -Tilted.Normal, 1e-12));
        Assert.Equal(Tilted.Origin, flipped.Origin);
        Assert.True(VectorOps.EpsilonEquals(
            VectorOps.Cross(flipped.XAxis, flipped.YAxis), flipped.ZAxis, 1e-12));
        Assert.True(PlaneOps.EpsilonEquals(PlaneOps.Flipped(flipped), Tilted, 1e-12));
    }

    [Fact]
    public void GetPlaneEquation_YieldsTheSignedDistanceWhenSubstituted()
    {
        (double a, double b, double c, double d) = PlaneOps.GetPlaneEquation(Tilted);
        Point3d point = PointOps.Create(4, -2, 9);

        // The normal is already unit length, so no division by its magnitude is needed.
        double substituted = (a * point.X) + (b * point.Y) + (c * point.Z) + d;

        Assert.Equal(PlaneOps.SignedDistanceTo(Tilted, point), substituted, 1e-12);
    }

    [Fact]
    public void Contains_UsesTheSuppliedTolerance()
    {
        Point3d justOff = PointOps.Create(0, 0, 0.001);

        Assert.True(PlaneOps.Contains(Plane.WorldXY, Point3d.Origin));
        Assert.True(PlaneOps.Contains(Plane.WorldXY, justOff, 0.01));
        Assert.False(PlaneOps.Contains(Plane.WorldXY, justOff, 0.0001));
    }

    [Fact]
    public void Transform_CarriesARigidFrameAcross()
    {
        TMatrix rigid = Transforms.Translate(5, 0, 0) * Transforms.Rotate(Vector3d.XAxis, Math.PI / 2);

        Plane moved = PlaneOps.Transform(Plane.WorldXY, rigid);

        Assert.True(PlaneOps.IsValid(moved));
        Assert.Equal(PointOps.Create(5, 0, 0), moved.Origin);
        Assert.True(VectorOps.EpsilonEquals(moved.Normal, -Vector3d.YAxis, 1e-12));
        Assert.True(VectorOps.IsPerpendicularTo(moved.XAxis, moved.YAxis, 1e-12));
    }

    [Fact]
    public void Transform_ReEstablishesTheFrameUnderANonUniformScale()
    {
        // A non-uniform scale does not preserve perpendicularity, so the frame is rebuilt rather than
        // left subtly skewed.
        Plane skewed = PlaneOps.CreateFromAxes(
            Point3d.Origin,
            VectorOps.Create(1, 1, 0),
            VectorOps.Create(-1, 1, 0));

        Plane scaled = PlaneOps.Transform(skewed, Transforms.Scale(5, 1, 1));

        Assert.True(PlaneOps.IsValid(scaled));
        Assert.True(VectorOps.IsUnit(scaled.XAxis, 1e-12));
        Assert.True(VectorOps.IsUnit(scaled.YAxis, 1e-12));
        Assert.True(VectorOps.IsPerpendicularTo(scaled.XAxis, scaled.YAxis, 1e-12));
        Assert.True(VectorOps.EpsilonEquals(
            VectorOps.Cross(scaled.XAxis, scaled.YAxis), scaled.ZAxis, 1e-12));
    }

    [Fact]
    public void Transform_YieldsUnsetWhenTheFrameCollapses()
    {
        // Flattening along Z leaves WorldZX with nothing to span.
        Plane collapsed = PlaneOps.Transform(Plane.WorldZX, Transforms.Scale(1, 1, 0));

        Assert.False(PlaneOps.IsValid(collapsed));
    }

    [Fact]
    public void EpsilonEquals_ComparesOriginAndAllThreeAxes()
    {
        Plane nudged = PlaneOps.CreateFromNormal(
            PointOps.Create(1, 2, 3.0000001), VectorOps.Create(1, 1, 1));

        Assert.True(PlaneOps.EpsilonEquals(Tilted, nudged, 1e-3));
        Assert.False(PlaneOps.EpsilonEquals(Tilted, nudged, 1e-9));
        Assert.False(PlaneOps.EpsilonEquals(Tilted, PlaneOps.Flipped(Tilted), 1e-9));
    }

    [Fact]
    public void Fit_RecoversAPlaneThroughExactlyCoplanarPoints()
    {
        Plane source = PlaneOps.CreateFromNormal(
            PointOps.Create(1, 2, 3), VectorOps.Create(1, 1, 1));

        Point3d[] samples =
        [
            PlaneOps.PointAt(source, 0, 0),
            PlaneOps.PointAt(source, 5, 0),
            PlaneOps.PointAt(source, 0, 5),
            PlaneOps.PointAt(source, -3, 2),
            PlaneOps.PointAt(source, 7, -4),
        ];

        Plane fitted = PlaneOps.CreateFromBestFit(samples, out double deviation);

        Assert.Equal(0.0, deviation, 1e-9);
        Assert.True(VectorOps.IsParallelTo(fitted.Normal, source.Normal, 1e-9));

        foreach (Point3d sample in samples)
        {
            Assert.True(PlaneOps.Contains(fitted, sample, 1e-9));
        }
    }

    [Fact]
    public void Fit_NeedsPointsWithSpreadInEveryAxisToExposeTheCovariance()
    {
        // The previous implementation accumulated the ZZ term as r.Y * r.Z instead of r.Z * r.Z. For
        // points lying in a world-aligned plane the error cancels, so this uses a tilted plane where the
        // residuals vary in Z and the wrong covariance produces the wrong normal.
        Plane source = PlaneOps.CreateFromNormal(
            Point3d.Origin, VectorOps.Create(2, -3, 5));

        Point3d[] samples =
        [
            PlaneOps.PointAt(source, 0, 0),
            PlaneOps.PointAt(source, 4, 1),
            PlaneOps.PointAt(source, -2, 6),
            PlaneOps.PointAt(source, 9, -5),
            PlaneOps.PointAt(source, -7, -3),
            PlaneOps.PointAt(source, 3, 8),
        ];

        Plane fitted = PlaneOps.CreateFromBestFit(samples, out double deviation);

        Assert.Equal(0.0, deviation, 1e-9);
        Assert.True(VectorOps.IsParallelTo(fitted.Normal, source.Normal, 1e-9));
    }

    [Fact]
    public void Fit_ReportsTheWorstDeviationForNonPlanarInput()
    {
        Point3d[] samples =
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(1, 1, 0),
            PointOps.Create(0, 1, 0),
            PointOps.Create(0.5, 0.5, 0.2),
        ];

        PlaneOps.CreateFromBestFit(samples, out double deviation);

        Assert.True(deviation > 0.0);
        Assert.True(deviation <= 0.2);
    }

    [Fact]
    public void Fit_RejectsTooFewOrCollinearPoints()
    {
        Assert.Throws<ArgumentException>(() => PlaneOps.CreateFromBestFit(
            [Point3d.Origin, PointOps.Create(1, 0, 0)], out _));

        Assert.False(PlaneOps.TryCreateFromBestFit(
            [Point3d.Origin, PointOps.Create(1, 1, 1), PointOps.Create(2, 2, 2)],
            out Plane? collinear,
            out double? deviation));

        Assert.Null(collinear);
        Assert.Null(deviation);
        Assert.False(PlaneOps.TryCreateFromBestFit(ReadOnlySpan<Point3d>.Empty, out _, out _));
    }
}
