namespace Phenome.Geometry.Tests;

public class CircleOpsTests
{
    private static readonly Circle UnitCircle = CircleOps.Create(Plane.WorldXY, 1);

    [Fact]
    public void Create_RejectsARadiusThatIsNotFiniteAndPositive()
    {
        // Silently taking the absolute value would hide a sign error upstream, and a zero radius is not a
        // circle at all.
        Assert.Throws<ArgumentOutOfRangeException>(() => CircleOps.Create(Plane.WorldXY, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CircleOps.Create(Plane.WorldXY, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CircleOps.Create(Plane.WorldXY, double.NaN));
    }

    [Fact]
    public void TryCreate_ReportsFailureInsteadOfThrowing()
    {
        Assert.False(CircleOps.TryCreate(Plane.WorldXY, -1, out Circle? bad));
        Assert.Null(bad);

        Assert.False(CircleOps.TryCreate(Plane.Unset, 1, out Circle? unset));
        Assert.Null(unset);

        Assert.True(CircleOps.TryCreate(Plane.WorldXY, 2, out Circle? good));
        Assert.Equal(2, good.Value.Radius);
    }

    [Fact]
    public void TryCreate_CreateFromCenterAndNormalFailsOnADegenerateNormal()
    {
        Assert.False(CircleOps.TryCreate(Point3d.Origin, Vector3d.Zero, 1, out Circle? none));
        Assert.Null(none);
    }

    [Fact]
    public void PointAt_MeasuresAnglesFromTheXAxisTowardsTheYAxis()
    {
        Assert.True(PointOps.EpsilonEquals(
            CircleOps.PointAt(UnitCircle, 0),
            PointOps.Create(1, 0, 0),
            1e-12));

        Assert.True(PointOps.EpsilonEquals(
            CircleOps.PointAt(UnitCircle, Math.PI / 2),
            PointOps.Create(0, 1, 0),
            1e-12));
    }

    [Fact]
    public void PointAt_DoesNotWrapSoAnglesAFullTurnApartAgree()
    {
        Assert.True(PointOps.EpsilonEquals(
            CircleOps.PointAt(UnitCircle, 0.3),
            CircleOps.PointAt(UnitCircle, 0.3 + Math.Tau),
            1e-12));
    }

    [Fact]
    public void TangentAt_PointsTheWayTheAngleIncreases()
    {
        Vector3d atStart = CircleOps.TangentAt(UnitCircle, 0);

        Assert.True(VectorOps.EpsilonEquals(atStart, Vector3d.YAxis, 1e-12));
        Assert.Equal(1, VectorOps.Length(atStart), 12);
    }

    [Fact]
    public void CircumferenceAndArea_MatchTheRadius()
    {
        Circle circle = CircleOps.Create(Plane.WorldXY, 3);

        Assert.Equal(Math.Tau * 3, CircleOps.Circumference(circle), 12);
        Assert.Equal(Math.PI * 9, CircleOps.Area(circle), 12);
        Assert.Equal(6, CircleOps.Diameter(circle));
    }

    [Fact]
    public void CreateFromPoints_PutsAngleZeroOnTheFirstPointAndSweepsTowardsTheSecond()
    {
        // Arc construction relies on this: the frame has to be predictable, or there is no way to say which
        // of the two arcs between the ends was meant.
        Point3d a = PointOps.Create(1, 0, 0);
        Point3d b = PointOps.Create(0, 1, 0);
        Point3d c = PointOps.Create(-1, 0, 0);

        Circle circle = CircleOps.CreateFromPoints(a, b, c);

        Assert.Equal(1, circle.Radius, 12);
        Assert.True(PointOps.EpsilonEquals(CircleOps.Center(circle), Point3d.Origin, 1e-12));
        Assert.True(PointOps.EpsilonEquals(CircleOps.PointAt(circle, 0), a, 1e-12));
        Assert.Equal(Math.PI / 2, CircleOps.ClosestParameter(circle, b), 12);
        Assert.Equal(Math.PI, CircleOps.ClosestParameter(circle, c), 12);
    }

    [Fact]
    public void CreateFromPoints_WorksOnAPlaneThatIsNotAxisAligned()
    {
        Point3d a = PointOps.Create(1, 1, 1);
        Point3d b = PointOps.Create(4, 2, -1);
        Point3d c = PointOps.Create(0, 5, 2);

        Circle circle = CircleOps.CreateFromPoints(a, b, c);
        double radius = circle.Radius;

        // Every point must be exactly a radius from the centre, which is the whole definition.
        Assert.Equal(radius, PointOps.DistanceTo(CircleOps.Center(circle), a), 9);
        Assert.Equal(radius, PointOps.DistanceTo(CircleOps.Center(circle), b), 9);
        Assert.Equal(radius, PointOps.DistanceTo(CircleOps.Center(circle), c), 9);
    }

    [Fact]
    public void TryCreateFromPoints_RejectsCollinearAndCoincidentPoints()
    {
        Assert.False(CircleOps.TryCreateFromPoints(
            PointOps.Create(0, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(2, 0, 0),
            out Circle? collinear));

        Assert.Null(collinear);

        Assert.False(CircleOps.TryCreateFromPoints(
            PointOps.Create(1, 1, 1),
            PointOps.Create(1, 1, 1),
            PointOps.Create(2, 0, 0),
            out Circle? coincident));

        Assert.Null(coincident);
    }

    [Fact]
    public void TryCreateFromPoints_AcceptsATinyTriangleAndRejectsAHugeFlatOne()
    {
        // The degeneracy test is relative, so absolute size must not decide the answer either way.
        Assert.True(CircleOps.TryCreateFromPoints(
            PointOps.Create(0, 0, 0),
            PointOps.Create(1e-6, 0, 0),
            PointOps.Create(0, 1e-6, 0),
            out Circle? tiny));

        Assert.NotNull(tiny);

        Assert.False(CircleOps.TryCreateFromPoints(
            PointOps.Create(0, 0, 0),
            PointOps.Create(1e6, 0, 0),
            PointOps.Create(2e6, 0, 0),
            out Circle? huge));

        Assert.Null(huge);
    }

    [Fact]
    public void ClosestParameter_WrapsIntoASingleTurn()
    {
        double angle = CircleOps.ClosestParameter(
            UnitCircle,
            PointOps.Create(0, -5, 0));

        Assert.Equal(3 * Math.PI / 2, angle, 12);
    }

    [Fact]
    public void ClosestPoint_ProjectsOntoTheCircleRegardlessOfHeight()
    {
        // A point off the plane still has a unique nearest point on the circle; only points on the axis do
        // not.
        Point3d closest = CircleOps.ClosestPoint(UnitCircle, PointOps.Create(5, 0, 9));

        Assert.True(PointOps.EpsilonEquals(closest, PointOps.Create(1, 0, 0), 1e-12));
    }

    [Fact]
    public void ClosestParameter_ReturnsZeroOnTheAxisWhereNoPointIsNearest()
    {
        Assert.Equal(0, CircleOps.ClosestParameter(UnitCircle, PointOps.Create(0, 0, 4)));
    }

    [Fact]
    public void DistanceTo_MeasuresToTheCircleNotToTheDisc()
    {
        // The centre is a full radius from the circle, even though it is inside it.
        Assert.Equal(1, CircleOps.DistanceTo(UnitCircle, Point3d.Origin), 12);
        Assert.Equal(2, CircleOps.DistanceTo(UnitCircle, PointOps.Create(3, 0, 0)), 12);
    }

    [Fact]
    public void Reversed_KeepsTheStartPointAndFlipsTheNormal()
    {
        Circle reversed = CircleOps.Reversed(UnitCircle);

        Assert.True(PointOps.EpsilonEquals(
            CircleOps.PointAt(reversed, 0),
            CircleOps.PointAt(UnitCircle, 0),
            1e-12));

        Assert.True(VectorOps.EpsilonEquals(
            CircleOps.Normal(reversed),
            -CircleOps.Normal(UnitCircle),
            1e-12));

        // Same start, opposite normal, so a quarter turn now lands on the other side.
        Assert.True(PointOps.EpsilonEquals(
            CircleOps.PointAt(reversed, Math.PI / 2),
            PointOps.Create(0, -1, 0),
            1e-12));
    }

    [Fact]
    public void ToPolyline_ClosesExactlyByRepeatingTheFirstPoint()
    {
        // Recomputing the last point from an angle of tau would leave it a rounding error away from the
        // first, and the polyline would not read as closed.
        Polyline polyline = CircleOps.ToPolyline(UnitCircle, 6);

        Assert.Equal(7, polyline.PointCount);
        Assert.True(polyline.Points[0].Equals(polyline.Points[6]));
        Assert.True(PolylineOps.IsClosed(polyline));
    }

    [Fact]
    public void ToPolyline_PutsEveryCornerOnTheCircle()
    {
        Circle circle = CircleOps.Create(
            PlaneOps.CreateFromNormal(PointOps.Create(3, -2, 1), VectorOps.Create(1, 2, 3)),
            5);

        Polyline polyline = CircleOps.ToPolyline(circle, 12);

        foreach (Point3d point in polyline.Points)
        {
            Assert.Equal(5, PointOps.DistanceTo(CircleOps.Center(circle), point), 9);
        }
    }

    [Fact]
    public void ToPolyline_RunsCounterClockwiseAboutThePlaneNormal()
    {
        Polyline polyline = CircleOps.ToPolyline(UnitCircle, 8);

        Assert.True(PolylineOps.SignedArea(polyline, Plane.WorldXY) > 0);
    }

    [Fact]
    public void ToPolyline_RejectsFewerThanThreeSegments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CircleOps.ToPolyline(UnitCircle, 2));
    }

    [Fact]
    public void SegmentCountForTolerance_KeepsTheInscribedPolygonWithinTheDeviationAsked()
    {
        double radius = 20;
        double allowed = 0.05;

        int count = CircleOps.SegmentCountForTolerance(radius, allowed);
        double actual = radius * (1 - Math.Cos(Math.PI / count));

        Assert.True(actual <= allowed, $"{count} segments deviate by {actual}, more than {allowed}");

        // And it should be the fewest that do, so one segment less must break the tolerance.
        double looser = radius * (1 - Math.Cos(Math.PI / (count - 1)));
        Assert.True(looser > allowed);
    }

    [Fact]
    public void SegmentCountForTolerance_NeverGoesBelowThreeSegments()
    {
        Assert.Equal(3, CircleOps.SegmentCountForTolerance(1, 5));
        Assert.Equal(3, CircleOps.SegmentCountForTolerance(1, 1));
    }

    [Fact]
    public void SegmentCountForTolerance_GrowsAsTheSquareRootOfTheTolerance()
    {
        // Worth pinning down, because it is the reason a count picked by tolerance beats a round number:
        // halving the error costs about 40% more segments rather than double.
        int coarse = CircleOps.SegmentCountForTolerance(100, 0.1);
        int fine = CircleOps.SegmentCountForTolerance(100, 0.025);

        Assert.Equal(2.0, (double)fine / coarse, 1);
    }

    [Fact]
    public void Transform_CarriesTheCircleThroughARigidMotion()
    {
        TMatrix motion = Transforms.Rotate(Vector3d.XAxis, Math.PI / 3) *
            Transforms.Translate(VectorOps.Create(1, 2, 3));

        Circle moved = CircleOps.Transform(UnitCircle, motion);

        Assert.Equal(1, moved.Radius, 12);
        Assert.True(PointOps.EpsilonEquals(
            CircleOps.PointAt(moved, 0.7),
            PointOps.Transform(CircleOps.PointAt(UnitCircle, 0.7), motion),
            1e-9));
    }

    [Fact]
    public void Transform_ScalesTheRadiusUnderAUniformScale()
    {
        Circle scaled = CircleOps.Transform(UnitCircle, Transforms.Scale(Point3d.Origin, 4));

        Assert.Equal(4, scaled.Radius, 12);
    }

    [Fact]
    public void TryTransform_RefusesANonUniformScaleBecauseTheResultIsAnEllipse()
    {
        // There is no ellipse type, so reporting failure is the only honest answer. Returning a circle of
        // some averaged radius would no longer pass through the transformed points.
        TMatrix squash = Transforms.Scale(2, 1, 1);

        Assert.False(CircleOps.TryTransform(UnitCircle, squash, out Circle? none));
        Assert.Null(none);
        Assert.False(CircleOps.IsValid(CircleOps.Transform(UnitCircle, squash)));
    }

    [Fact]
    public void TryCreateInCorner_TouchesBothLegsAtTheTangentDistance()
    {
        Point3d previous = PointOps.Create(10, 0, 0);
        Point3d corner = Point3d.Origin;
        Point3d next = PointOps.Create(0, 10, 0);

        Assert.True(CircleOps.TryCreateInCorner(previous, corner, next, 2, out Circle? fillet));

        // A right-angled corner puts the centre at (r, r) and the tangent points a radius along each leg.
        Assert.True(PointOps.EpsilonEquals(
            CircleOps.Center(fillet.Value),
            PointOps.Create(2, 2, 0),
            1e-12));

        Assert.Equal(2, fillet.Value.Radius, 12);

        // Both tangent points lie on the fillet circle, a radius back along each leg from the corner.
        Assert.Equal(
            0,
            CircleOps.DistanceTo(fillet.Value, PointOps.Create(2, 0, 0)),
            12);

        Assert.Equal(
            0,
            CircleOps.DistanceTo(fillet.Value, PointOps.Create(0, 2, 0)),
            12);
    }

    [Fact]
    public void TryCreateInCorner_RefusesWhenTheLegsAreTooShortForTheRadius()
    {
        // This is the case worth catching: a fillet larger than its own leg runs past the end of it and
        // produces a self-crossing outline further downstream.
        Point3d previous = PointOps.Create(1, 0, 0);
        Point3d corner = Point3d.Origin;
        Point3d next = PointOps.Create(0, 10, 0);

        Assert.False(CircleOps.TryCreateInCorner(previous, corner, next, 5, out Circle? none));
        Assert.Null(none);
    }

    [Fact]
    public void TryCreateInCorner_RefusesAStraightOrFoldedCorner()
    {
        Point3d corner = Point3d.Origin;

        Assert.False(CircleOps.TryCreateInCorner(
            PointOps.Create(-1, 0, 0), corner, PointOps.Create(1, 0, 0), 0.1, out _));

        Assert.False(CircleOps.TryCreateInCorner(
            PointOps.Create(1, 0, 0), corner, PointOps.Create(1, 0, 0), 0.1, out _));
    }

    [Fact]
    public void ToString_NamesTheCentreAndRadiusAndSaysWhenItIsUnset()
    {
        Assert.Equal("Circle(unset)", Circle.Unset.ToString());
        Assert.Contains("R 1", UnitCircle.ToString());
    }

    [Fact]
    public void Equality_TreatsCirclesWithDifferentFramesAsDifferent()
    {
        // The same points, but angle zero sits somewhere else, so evaluation differs and they are not the
        // same circle as far as this library is concerned.
        Circle rotated = CircleOps.Create(
            PlaneOps.CreateFromAxes(Point3d.Origin, Vector3d.YAxis, -Vector3d.XAxis),
            1);

        Assert.NotEqual(UnitCircle, rotated);
    }
}
