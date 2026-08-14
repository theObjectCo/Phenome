using Phenome.Geometry;

namespace Phenome.Geometry.Tests;

public class PolylineOpsTests
{
    /// <summary>An L along X then Y, total length 3, so arc length and index parameters differ.</summary>
    private static Polyline Bent() => PolylineOps.Create(
    [
        PointOps.Create(0, 0, 0),
        PointOps.Create(2, 0, 0),
        PointOps.Create(2, 1, 0),
    ]);

    /// <summary>A unit square closed by repeating the first point, wound counter-clockwise.</summary>
    private static Polyline ClosedSquare() => PolylineOps.Create(
    [
        PointOps.Create(0, 0, 0),
        PointOps.Create(1, 0, 0),
        PointOps.Create(1, 1, 0),
        PointOps.Create(0, 1, 0),
        PointOps.Create(0, 0, 0),
    ]);

    [Fact]
    public void Create_KeepsThePointsInOrder()
    {
        Polyline polyline = Bent();

        Assert.Equal(3, polyline.PointCount);
        Assert.Equal(PointOps.Create(2, 0, 0), polyline.Points[1]);
    }

    [Fact]
    public void IsValid_NeedsTwoFinitePoints()
    {
        Assert.True(PolylineOps.IsValid(Bent()));
        Assert.False(PolylineOps.IsValid(PolylineOps.Create()));
        Assert.False(PolylineOps.IsValid(PolylineOps.Create([Point3d.Origin])));
        Assert.False(PolylineOps.IsValid(
            PolylineOps.Create([Point3d.Origin, Point3d.Unset])));
    }

    [Fact]
    public void SegmentCount_IsOneFewerThanThePoints()
    {
        Assert.Equal(2, PolylineOps.SegmentCount(Bent()));
        Assert.Equal(4, PolylineOps.SegmentCount(ClosedSquare()));
        Assert.Equal(0, PolylineOps.SegmentCount(PolylineOps.Create()));
    }

    [Fact]
    public void Segment_HandsBackTheSpanBetweenTwoPoints()
    {
        Line first = PolylineOps.Segment(Bent(), 0);

        Assert.Equal(Point3d.Origin, first.From);
        Assert.Equal(PointOps.Create(2, 0, 0), first.To);
        Assert.Equal(2, PolylineOps.Segments(Bent()).Length);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Segment_RejectsAnIndexOutsideThePolyline(int segmentIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PolylineOps.Segment(Bent(), segmentIndex));
    }

    [Fact]
    public void Length_AddsUpTheSegments()
    {
        Assert.Equal(3.0, PolylineOps.Length(Bent()), 1e-12);
        Assert.Equal(4.0, PolylineOps.Length(ClosedSquare()), 1e-12);
        Assert.Equal(0.0, PolylineOps.Length(PolylineOps.Create()), 1e-12);
    }

    [Fact]
    public void IsClosed_TestsTheRepeatedPointWithATolerance()
    {
        // The previous library compared the endpoints for exact float equality, so a loop that had been
        // through a transform stopped counting as closed.
        Assert.True(PolylineOps.IsClosed(ClosedSquare()));
        Assert.False(PolylineOps.IsClosed(Bent()));

        Polyline nearlyClosed = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(1, 1, 0),
            PointOps.Create(0.0001, 0, 0),
        ]);

        Assert.True(PolylineOps.IsClosed(nearlyClosed, 0.001));
        Assert.False(PolylineOps.IsClosed(nearlyClosed, 0.00001));
    }

    [Fact]
    public void Closed_RepeatsTheFirstPointAndIsIdempotent()
    {
        Polyline open = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(1, 1, 0),
        ]);

        Polyline closed = PolylineOps.Closed(open);

        Assert.Equal(4, closed.PointCount);
        Assert.Equal(closed.Points[0], closed.Points[^1]);
        Assert.True(PolylineOps.IsClosed(closed));

        // Closing something already closed must not add another point.
        Assert.Equal(4, PolylineOps.Closed(closed).PointCount);
        Assert.Equal(3, open.PointCount);
    }

    [Fact]
    public void Closed_RejectsSomethingWithNothingToEnclose()
    {
        Assert.Throws<ArgumentException>(() => PolylineOps.Closed(
            PolylineOps.Create([Point3d.Origin, PointOps.Create(1, 0, 0)])));
    }

    [Fact]
    public void Reversed_FlipsTheOrderAndLeavesTheOriginalAlone()
    {
        Polyline original = Bent();
        Polyline reversed = PolylineOps.Reversed(original);

        Assert.Equal(PointOps.Create(2, 1, 0), reversed.Points[0]);
        Assert.Equal(Point3d.Origin, reversed.Points[^1]);
        Assert.Equal(Point3d.Origin, original.Points[0]);
        Assert.True(PolylineOps.EpsilonEquals(
            original, PolylineOps.Reversed(reversed)));
    }

    [Fact]
    public void Transform_MovesThePointsInPlace()
    {
        Polyline polyline = Bent();

        PolylineOps.Transform(polyline, Transforms.Translate(0, 0, 5));

        Assert.Equal(PointOps.Create(0, 0, 5), polyline.Points[0]);
        Assert.Equal(PointOps.Create(2, 1, 5), polyline.Points[2]);
    }

    [Fact]
    public void PointAt_UsesIndexBasedParameters()
    {
        Polyline polyline = Bent();

        Assert.Equal(Point3d.Origin, PolylineOps.PointAt(polyline, 0));
        Assert.Equal(PointOps.Create(1, 0, 0), PolylineOps.PointAt(polyline, 0.5));
        Assert.Equal(PointOps.Create(2, 0, 0), PolylineOps.PointAt(polyline, 1));
        Assert.Equal(PointOps.Create(2, 0.5, 0), PolylineOps.PointAt(polyline, 1.5));
        Assert.Equal(PointOps.Create(2, 1, 0), PolylineOps.PointAt(polyline, 2));
    }

    [Fact]
    public void PointAt_ClampsRatherThanExtrapolating()
    {
        // A polyline has no natural extension past its ends, so inventing one would be inventing geometry.
        Polyline polyline = Bent();

        Assert.Equal(Point3d.Origin, PolylineOps.PointAt(polyline, -10));
        Assert.Equal(PointOps.Create(2, 1, 0), PolylineOps.PointAt(polyline, 99));
    }

    [Fact]
    public void PointAtLength_MeasuresAlongTheWholeRunNotPerSegment()
    {
        // Total length 3, and the bend sits at 2, so 2.5 is halfway up the second leg. An index parameter
        // of 2.5 would mean something else entirely, which is why both exist.
        Polyline polyline = Bent();

        Assert.Equal(PointOps.Create(1, 0, 0), PolylineOps.PointAtLength(polyline, 1));
        Assert.Equal(PointOps.Create(2, 0, 0), PolylineOps.PointAtLength(polyline, 2));
        Assert.Equal(PointOps.Create(2, 0.5, 0), PolylineOps.PointAtLength(polyline, 2.5));
    }

    [Fact]
    public void PointAtLength_ClampsAtBothEnds()
    {
        Polyline polyline = Bent();

        Assert.Equal(Point3d.Origin, PolylineOps.PointAtLength(polyline, -5));
        Assert.Equal(PointOps.Create(2, 1, 0), PolylineOps.PointAtLength(polyline, 500));
    }

    [Fact]
    public void PointAtLength_SurvivesAZeroLengthSegmentInTheMiddle()
    {
        Polyline withRepeat = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(2, 0, 0),
        ]);

        Point3d at1 = PolylineOps.PointAtLength(withRepeat, 1);

        Assert.True(PointOps.IsValid(at1));
        Assert.Equal(PointOps.Create(1, 0, 0), at1);
    }

    [Fact]
    public void ClosestParameter_AndClosestPoint_AgreeWithEachOther()
    {
        Polyline polyline = Bent();
        Point3d target = PointOps.Create(1, 3, 0);

        double parameter = PolylineOps.ClosestParameter(polyline, target);
        Point3d closest = PolylineOps.ClosestPoint(polyline, target);

        Assert.Equal(closest, PolylineOps.PointAt(polyline, parameter));
        Assert.Equal(PolylineOps.DistanceTo(polyline, target),
            PointOps.DistanceTo(closest, target), 1e-12);
    }

    [Fact]
    public void ClosestPoint_StaysOnThePolylineAndPicksTheNearestSegment()
    {
        Polyline polyline = Bent();

        // Just above the first leg.
        Assert.Equal(
            PointOps.Create(1, 0, 0),
            PolylineOps.ClosestPoint(polyline, PointOps.Create(1, 0.25, 0)));

        // Off the end of the second leg, so it lands on the corner point.
        Assert.Equal(
            PointOps.Create(2, 1, 0),
            PolylineOps.ClosestPoint(polyline, PointOps.Create(5, 5, 0)));
    }

    [Fact]
    public void SignedArea_IsPositiveCounterClockwiseAndNegativeClockwise()
    {
        Polyline counterClockwise = ClosedSquare();
        Polyline clockwise = PolylineOps.Reversed(counterClockwise);

        Assert.Equal(1.0, PolylineOps.SignedArea(counterClockwise, Plane.WorldXY), 1e-12);
        Assert.Equal(-1.0, PolylineOps.SignedArea(clockwise, Plane.WorldXY), 1e-12);
    }

    [Fact]
    public void SignedArea_ClosesAnOpenPolylineImplicitly()
    {
        // The same square without the repeated point encloses the same area.
        Polyline open = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(1, 1, 0),
            PointOps.Create(0, 1, 0),
        ]);

        Assert.Equal(1.0, PolylineOps.SignedArea(open, Plane.WorldXY), 1e-12);
    }

    [Fact]
    public void IsClockwise_FlipsWithTheWindingAndWithThePlane()
    {
        // The previous library answered this by summing unsigned angles, which cannot express a direction,
        // so the result was decided by rounding.
        Polyline square = ClosedSquare();

        Assert.False(PolylineOps.IsClockwise(square, Plane.WorldXY));
        Assert.True(PolylineOps.IsClockwise(PolylineOps.Reversed(square), Plane.WorldXY));

        // Viewed from the other side, the same loop runs the other way.
        Assert.True(PolylineOps.IsClockwise(square, PlaneOps.Flipped(Plane.WorldXY)));
    }

    [Fact]
    public void TryDivideByCount_SpacesThePointsByEqualArcLength()
    {
        Assert.True(PolylineOps.TryDivideByCount(Bent(), 3, out Point3d[]? points));

        Assert.NotNull(points);
        Assert.Equal(4, points.Length);
        Assert.Equal(Point3d.Origin, points[0]);
        Assert.Equal(PointOps.Create(1, 0, 0), points[1]);
        Assert.Equal(PointOps.Create(2, 0, 0), points[2]);
        Assert.Equal(PointOps.Create(2, 1, 0), points[3]);
    }

    [Fact]
    public void TryDivideByCount_FailsWhenThereIsNoLengthToDivide()
    {
        Polyline collapsed = PolylineOps.Create([Point3d.Origin, Point3d.Origin]);

        Assert.False(PolylineOps.TryDivideByCount(collapsed, 4, out Point3d[]? points));
        Assert.Null(points);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => PolylineOps.TryDivideByCount(Bent(), 0, out _));
    }

    [Fact]
    public void EpsilonEquals_IsOrderSensitive()
    {
        Polyline polyline = Bent();

        Assert.True(PolylineOps.EpsilonEquals(polyline, Bent()));
        Assert.False(PolylineOps.EpsilonEquals(polyline, PolylineOps.Reversed(polyline)));
        Assert.False(PolylineOps.EpsilonEquals(polyline, ClosedSquare()));
    }

    [Fact]
    public void ToString_ReportsThePointCount()
    {
        Assert.Equal("Polyline(P 3)", Bent().ToString());
    }
}
