namespace Phenome.Geometry.Tests;

public class ArcOpsTests
{
    private static readonly Arc QuarterTurn = ArcOps.Create(
        Plane.WorldXY,
        1,
        IntervalOps.Create(0, Math.PI / 2));

    [Fact]
    public void Create_RejectsADomainThatSweepsNothing()
    {
        // An arc of zero sweep is a point, and every parameterisation of it divides by zero somewhere.
        Assert.Throws<ArgumentException>(() =>
            ArcOps.Create(Plane.WorldXY, 1, IntervalOps.Create(1, 1)));

        Assert.Throws<ArgumentException>(() =>
            ArcOps.Create(Plane.WorldXY, 1, Interval.Unset));
    }

    [Fact]
    public void Create_RejectsARadiusThatIsNotFiniteAndPositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ArcOps.Create(Plane.WorldXY, 0, Interval.Unit));
    }

    [Fact]
    public void Create_KeepsADomainWiderThanAFullTurnRatherThanReducingIt()
    {
        // Reducing would lose the difference between an arc and the same arc plus a lap, which matters to a
        // sweep or a revolve built on it.
        Arc andAHalf = ArcOps.Create(
            Plane.WorldXY, 1, IntervalOps.Create(0, Math.Tau * 1.5));

        Assert.Equal(Math.Tau * 1.5, ArcOps.SweepAngle(andAHalf), 12);
        Assert.Equal(Math.Tau * 1.5, ArcOps.Length(andAHalf), 12);
    }

    [Fact]
    public void StartAndEndPoints_SitAtTheEndsOfTheDomain()
    {
        Assert.True(PointOps.EpsilonEquals(
            ArcOps.StartPoint(QuarterTurn), PointOps.Create(1, 0, 0), 1e-12));

        Assert.True(PointOps.EpsilonEquals(
            ArcOps.EndPoint(QuarterTurn), PointOps.Create(0, 1, 0), 1e-12));

        Assert.True(PointOps.EpsilonEquals(
            ArcOps.MidPoint(QuarterTurn),
            PointOps.Create(Math.Sqrt(0.5), Math.Sqrt(0.5), 0),
            1e-12));
    }

    [Fact]
    public void SweepAngle_IsNegativeForAClockwiseArc()
    {
        Arc clockwise = ArcOps.Create(
            Plane.WorldXY, 1, IntervalOps.Create(Math.PI / 2, 0));

        Assert.Equal(-Math.PI / 2, ArcOps.SweepAngle(clockwise), 12);
        Assert.True(ArcOps.IsClockwise(clockwise));

        // Length is a distance and stays positive whichever way the arc runs.
        Assert.Equal(Math.PI / 2, ArcOps.Length(clockwise), 12);
    }

    [Fact]
    public void PointAtNormalized_RunsFromStartToEndWhicheverWayTheArcSweeps()
    {
        Arc clockwise = ArcOps.Reversed(QuarterTurn);

        Assert.True(PointOps.EpsilonEquals(
            ArcOps.PointAtNormalized(clockwise, 0),
            ArcOps.EndPoint(QuarterTurn),
            1e-12));

        Assert.True(PointOps.EpsilonEquals(
            ArcOps.PointAtNormalized(clockwise, 1),
            ArcOps.StartPoint(QuarterTurn),
            1e-12));
    }

    [Fact]
    public void PointAtLength_ClampsAtBothEndsRatherThanContinuingRound()
    {
        Assert.True(PointOps.EpsilonEquals(
            ArcOps.PointAtLength(QuarterTurn, -5),
            ArcOps.StartPoint(QuarterTurn),
            1e-12));

        Assert.True(PointOps.EpsilonEquals(
            ArcOps.PointAtLength(QuarterTurn, 500),
            ArcOps.EndPoint(QuarterTurn),
            1e-12));

        // A quarter of a unit circle is pi/2 long, so half of that is the midpoint.
        Assert.True(PointOps.EpsilonEquals(
            ArcOps.PointAtLength(QuarterTurn, Math.PI / 4),
            ArcOps.MidPoint(QuarterTurn),
            1e-12));
    }

    [Fact]
    public void TangentAt_PointsIntoTheArcFromItsStartEvenWhenClockwise()
    {
        // The useful definition follows the sweep, not the underlying circle: the tangent at the start of an
        // arc should point along the arc.
        Vector3d forward = ArcOps.StartTangent(QuarterTurn);
        Assert.True(VectorOps.EpsilonEquals(forward, Vector3d.YAxis, 1e-12));

        Arc clockwise = ArcOps.Reversed(QuarterTurn);
        Vector3d backward = ArcOps.StartTangent(clockwise);
        Assert.True(VectorOps.EpsilonEquals(backward, Vector3d.XAxis, 1e-12));
    }

    [Fact]
    public void Reversed_SwapsTheEndsAndLeavesTheGeometryAlone()
    {
        Arc reversed = ArcOps.Reversed(QuarterTurn);

        Assert.True(PointOps.EpsilonEquals(
            ArcOps.StartPoint(reversed), ArcOps.EndPoint(QuarterTurn), 1e-12));

        Assert.Equal(ArcOps.Length(QuarterTurn), ArcOps.Length(reversed), 12);
        Assert.Equal(QuarterTurn, ArcOps.Reversed(reversed));
    }

    [Fact]
    public void Sagitta_MeasuresTheBulgePastTheChord()
    {
        // A half turn bulges by a full radius; a full turn's chord is degenerate and the bulge is a
        // diameter, which is why this is computed from the half angle rather than the chord.
        Arc halfTurn = ArcOps.Create(Plane.WorldXY, 3, IntervalOps.Create(0, Math.PI));
        Assert.Equal(3, ArcOps.Sagitta(halfTurn), 12);

        Arc fullTurn = ArcOps.Create(Plane.WorldXY, 3, Interval.FullTurn);
        Assert.Equal(6, ArcOps.Sagitta(fullTurn), 12);
    }

    [Fact]
    public void ChordLength_MatchesTheStraightDistanceBetweenTheEnds()
    {
        Assert.Equal(Math.Sqrt(2), ArcOps.ChordLength(QuarterTurn), 12);
    }

    [Fact]
    public void CreateFromPoints_RunsFromTheFirstPointToTheLastByWayOfTheMiddle()
    {
        Point3d start = PointOps.Create(1, 0, 0);
        Point3d interior = PointOps.Create(0, 1, 0);
        Point3d end = PointOps.Create(-1, 0, 0);

        Arc arc = ArcOps.CreateFromPoints(start, interior, end);

        Assert.True(PointOps.EpsilonEquals(ArcOps.StartPoint(arc), start, 1e-9));
        Assert.True(PointOps.EpsilonEquals(ArcOps.EndPoint(arc), end, 1e-9));
        Assert.True(PointOps.EpsilonEquals(ArcOps.MidPoint(arc), interior, 1e-9));
        Assert.Equal(Math.PI, ArcOps.Length(arc), 9);
    }

    [Fact]
    public void CreateFromPoints_LetsTheMiddlePointChooseWhichOfTheTwoArcsIsMeant()
    {
        // The ends alone are ambiguous. Moving the middle point to the far side must give the long way
        // round, not the short one.
        Point3d start = PointOps.Create(1, 0, 0);
        Point3d end = PointOps.Create(0, 1, 0);

        Arc shortWay = ArcOps.CreateFromPoints(
            start, PointOps.Create(Math.Sqrt(0.5), Math.Sqrt(0.5), 0), end);

        Arc longWay = ArcOps.CreateFromPoints(
            start, PointOps.Create(-Math.Sqrt(0.5), -Math.Sqrt(0.5), 0), end);

        Assert.Equal(Math.PI / 2, Math.Abs(ArcOps.SweepAngle(shortWay)), 9);
        Assert.Equal(3 * Math.PI / 2, Math.Abs(ArcOps.SweepAngle(longWay)), 9);
    }

    [Fact]
    public void TryCreateFromPoints_RefusesWhenTheEndsCoincideBecauseTheSweepIsUndefined()
    {
        // Three points cannot say whether a start meeting its own end means a full circle or nothing at all.
        Point3d start = PointOps.Create(1, 0, 0);

        Assert.False(ArcOps.TryCreateFromPoints(
            start, PointOps.Create(0, 1, 0), start, out Arc? none));

        Assert.Null(none);
    }

    [Fact]
    public void TryCreateFromPoints_RefusesCollinearPoints()
    {
        Assert.False(ArcOps.TryCreateFromPoints(
            PointOps.Create(0, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(2, 0, 0),
            out Arc? none));

        Assert.Null(none);
    }

    [Fact]
    public void ClosestPoint_ClampsToTheNearerEndForAPointOffTheArc()
    {
        // The projected angle falls outside the domain in one of two directions, and the nearer end has to
        // win rather than whichever the wrapped angle happens to sit next to.
        Point3d pastTheStart = PointOps.Create(2, -2, 0);
        Point3d pastTheEnd = PointOps.Create(-2, 2, 0);

        Assert.True(PointOps.EpsilonEquals(
            ArcOps.ClosestPoint(QuarterTurn, pastTheStart),
            ArcOps.StartPoint(QuarterTurn),
            1e-9));

        Assert.True(PointOps.EpsilonEquals(
            ArcOps.ClosestPoint(QuarterTurn, pastTheEnd),
            ArcOps.EndPoint(QuarterTurn),
            1e-9));
    }

    [Fact]
    public void ClosestPoint_LandsOnTheArcForAPointBesideIt()
    {
        Point3d outside = PointOps.Create(3, 3, 5);

        Assert.True(PointOps.EpsilonEquals(
            ArcOps.ClosestPoint(QuarterTurn, outside),
            ArcOps.MidPoint(QuarterTurn),
            1e-9));
    }

    [Fact]
    public void DistanceTo_MeasuresToTheArcAndNotToItsCircle()
    {
        // The point sits on the circle, but on the part the arc does not cover, so the distance is to the
        // arc's nearer end rather than zero.
        Point3d onTheCircleButOffTheArc = PointOps.Create(-1, 0, 0);

        Assert.Equal(
            Math.Sqrt(2),
            ArcOps.DistanceTo(QuarterTurn, onTheCircleButOffTheArc),
            9);
    }

    [Fact]
    public void ToPolyline_HitsBothEndsExactlyAndStaysOpen()
    {
        Polyline polyline = ArcOps.ToPolyline(QuarterTurn, 4);

        Assert.Equal(5, polyline.PointCount);
        Assert.False(PolylineOps.IsClosed(polyline));

        Assert.True(PointOps.EpsilonEquals(
            polyline.Points[0], ArcOps.StartPoint(QuarterTurn), 1e-12));

        Assert.True(PointOps.EpsilonEquals(
            polyline.Points[4], ArcOps.EndPoint(QuarterTurn), 1e-12));
    }

    [Fact]
    public void ToPolyline_PutsEveryCornerOnTheArc()
    {
        Arc arc = ArcOps.Create(
            PlaneOps.CreateFromNormal(PointOps.Create(2, 3, -1), VectorOps.Create(1, -2, 4)),
            7,
            IntervalOps.Create(0.4, 2.9));

        Polyline polyline = ArcOps.ToPolyline(arc, 9);

        foreach (Point3d point in polyline.Points)
        {
            Assert.Equal(7, PointOps.DistanceTo(ArcOps.Center(arc), point), 9);
        }
    }

    [Fact]
    public void ToPolyline_RejectsAskingForNoSegments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ArcOps.ToPolyline(QuarterTurn, 0));
    }

    [Fact]
    public void SegmentCountForTolerance_StaysWithinTheDeviationAsked()
    {
        Arc arc = ArcOps.Create(Plane.WorldXY, 50, IntervalOps.Create(0, Math.PI / 2));
        double allowed = 0.02;

        int count = ArcOps.SegmentCountForTolerance(arc, allowed);
        Polyline polyline = ArcOps.ToPolyline(arc, count);

        // Measure the real thing rather than trusting the formula: every segment midpoint must be within
        // the tolerance of the arc.
        for (int i = 0; i < count; i++)
        {
            Point3d chordMidpoint = LineOps.Midpoint(PolylineOps.Segment(polyline, i));
            Assert.True(ArcOps.DistanceTo(arc, chordMidpoint) <= allowed);
        }
    }

    [Fact]
    public void SegmentCountForTolerance_ScalesWithTheSweepNotWithAFullTurn()
    {
        // A quarter turn should cost about a quarter of the segments a whole circle needs at the same
        // tolerance. Getting this wrong is how tessellation ends up four times heavier than it has to be.
        Arc quarter = ArcOps.Create(Plane.WorldXY, 10, IntervalOps.Create(0, Math.PI / 2));
        Arc full = ArcOps.Create(Plane.WorldXY, 10, Interval.FullTurn);

        int quarterCount = ArcOps.SegmentCountForTolerance(quarter, 0.01);
        int fullCount = ArcOps.SegmentCountForTolerance(full, 0.01);

        // Rounding up to whole segments means the ratio cannot land on exactly four, so the assertion is a
        // range rather than an equality — the point is that it is near four and nowhere near one.
        double ratio = (double)fullCount / quarterCount;
        Assert.InRange(ratio, 3.5, 4.5);
    }

    [Fact]
    public void Transform_CarriesTheArcThroughARigidMotion()
    {
        TMatrix motion = Transforms.Rotate(PointOps.Create(1, 1, 1), Vector3d.YAxis, 0.9) *
            Transforms.Translate(VectorOps.Create(-4, 2, 6));

        Arc moved = ArcOps.Transform(QuarterTurn, motion);

        Assert.True(ArcOps.IsValid(moved));
        Assert.Equal(ArcOps.Length(QuarterTurn), ArcOps.Length(moved), 9);

        Assert.True(PointOps.EpsilonEquals(
            ArcOps.StartPoint(moved),
            PointOps.Transform(ArcOps.StartPoint(QuarterTurn), motion),
            1e-9));

        Assert.True(PointOps.EpsilonEquals(
            ArcOps.MidPoint(moved),
            PointOps.Transform(ArcOps.MidPoint(QuarterTurn), motion),
            1e-9));

        Assert.True(PointOps.EpsilonEquals(
            ArcOps.EndPoint(moved),
            PointOps.Transform(ArcOps.EndPoint(QuarterTurn), motion),
            1e-9));
    }

    [Fact]
    public void Transform_KeepsTheArcOnTheSamePointsUnderAMirror()
    {
        // A mirror flips the frame's handedness, so the domain has to come out negated or the arc would
        // cover the complement of itself — the long way round instead of the short way.
        TMatrix mirror = Transforms.Scale(1, -1, 1);

        Arc mirrored = ArcOps.Transform(QuarterTurn, mirror);

        Assert.True(ArcOps.IsValid(mirrored));
        Assert.Equal(ArcOps.Length(QuarterTurn), ArcOps.Length(mirrored), 9);

        Assert.True(PointOps.EpsilonEquals(
            ArcOps.MidPoint(mirrored),
            PointOps.Transform(ArcOps.MidPoint(QuarterTurn), mirror),
            1e-9));
    }

    [Fact]
    public void Transform_ScalesTheArcUniformly()
    {
        Arc scaled = ArcOps.Transform(QuarterTurn, Transforms.Scale(Point3d.Origin, 3));

        Assert.Equal(3, scaled.Radius, 12);
        Assert.Equal(ArcOps.Length(QuarterTurn) * 3, ArcOps.Length(scaled), 9);
    }

    [Fact]
    public void TryTransform_RefusesANonUniformScaleBecauseTheResultIsElliptical()
    {
        Assert.False(ArcOps.TryTransform(QuarterTurn, Transforms.Scale(2, 1, 1), out Arc? none));
        Assert.Null(none);
        Assert.False(ArcOps.IsValid(ArcOps.Transform(QuarterTurn, Transforms.Scale(2, 1, 1))));
    }

    [Fact]
    public void ToCircle_KeepsTheFrameSoAngleZeroDoesNotMove()
    {
        Circle circle = ArcOps.ToCircle(QuarterTurn);

        Assert.Equal(QuarterTurn.Radius, circle.Radius);
        Assert.Equal(QuarterTurn.Plane, circle.Plane);
    }

    [Fact]
    public void ToString_NamesTheCentreRadiusAndDomainAndSaysWhenItIsUnset()
    {
        Assert.Equal("Arc(unset)", Arc.Unset.ToString());
        Assert.Contains("R 1", QuarterTurn.ToString());
    }
}
