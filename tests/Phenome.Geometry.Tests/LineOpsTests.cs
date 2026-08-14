using Phenome.Geometry;

namespace Phenome.Geometry.Tests;

public class LineOpsTests
{
    private static readonly Line AlongX = LineOps.Create(
        PointOps.Create(0, 0, 0),
        PointOps.Create(10, 0, 0));

    [Fact]
    public void LengthAndDirection_DescribeTheSegment()
    {
        Assert.Equal(10.0, LineOps.Length(AlongX), 1e-12);
        Assert.Equal(100.0, LineOps.LengthSquared(AlongX), 1e-12);
        Assert.Equal(VectorOps.Create(10, 0, 0), LineOps.Direction(AlongX));
        Assert.Equal(Vector3d.XAxis, LineOps.UnitDirection(AlongX));
        Assert.Equal(PointOps.Create(5, 0, 0), LineOps.Midpoint(AlongX));
    }

    [Fact]
    public void IsValid_RejectsUnsetEndpoints()
    {
        Assert.True(LineOps.IsValid(AlongX));
        Assert.False(LineOps.IsValid(Line.Unset));
        Assert.False(LineOps.IsValid(LineOps.Create(Point3d.Origin, Point3d.Unset)));
    }

    [Fact]
    public void IsDegenerate_DetectsZeroLength()
    {
        Point3d point = PointOps.Create(3, 4, 5);

        Assert.True(LineOps.IsDegenerate(LineOps.Create(point, point)));
        Assert.True(LineOps.IsDegenerate(Line.Unset));
        Assert.False(LineOps.IsDegenerate(AlongX));
    }

    [Fact]
    public void TryUnitDirection_FailsOnADegenerateSegment()
    {
        Line degenerate = LineOps.Create(Point3d.Origin, Point3d.Origin);

        Assert.False(LineOps.TryUnitDirection(degenerate, out Vector3d? direction));
        Assert.Null(direction);
        Assert.True(LineOps.TryUnitDirection(AlongX, out Vector3d? valid));
        Assert.Equal(Vector3d.XAxis, valid!.Value);
    }

    [Fact]
    public void Flipped_ReturnsANewSegmentAndLeavesTheOriginalAlone()
    {
        // The whole reason the type is a readonly struct: the previous mutating version silently did
        // nothing when called on a value returned by a property.
        Line flipped = LineOps.Flipped(AlongX);

        Assert.Equal(PointOps.Create(0, 0, 0), AlongX.From);
        Assert.Equal(PointOps.Create(10, 0, 0), AlongX.To);
        Assert.Equal(PointOps.Create(10, 0, 0), flipped.From);
        Assert.Equal(PointOps.Create(0, 0, 0), flipped.To);
        Assert.Equal(AlongX, LineOps.Flipped(flipped));
    }

    [Fact]
    public void PointAt_UsesNormalisedParameters()
    {
        Assert.Equal(PointOps.Create(0, 0, 0), LineOps.PointAt(AlongX, 0));
        Assert.Equal(PointOps.Create(10, 0, 0), LineOps.PointAt(AlongX, 1));
        Assert.Equal(PointOps.Create(2.5, 0, 0), LineOps.PointAt(AlongX, 0.25));
    }

    [Fact]
    public void PointAt_ExtrapolatesBeyondTheEndpoints()
    {
        Assert.Equal(PointOps.Create(-10, 0, 0), LineOps.PointAt(AlongX, -1));
        Assert.Equal(PointOps.Create(20, 0, 0), LineOps.PointAt(AlongX, 2));
    }

    [Fact]
    public void PointAtLength_MeasuresInModelUnits()
    {
        Assert.Equal(PointOps.Create(3, 0, 0), LineOps.PointAtLength(AlongX, 3));
        Assert.Equal(PointOps.Create(-3, 0, 0), LineOps.PointAtLength(AlongX, -3));
    }

    [Fact]
    public void PointAtLength_ThrowsOnADegenerateSegment()
    {
        Line degenerate = LineOps.Create(Point3d.Origin, Point3d.Origin);

        Assert.Throws<InvalidOperationException>(() => LineOps.PointAtLength(degenerate, 1));
    }

    [Fact]
    public void ClosestParameter_ProjectsOntoTheInfiniteLine()
    {
        Assert.Equal(
            0.5, LineOps.ClosestParameter(AlongX, PointOps.Create(5, 3, 0)), 1e-12);
        Assert.Equal(
            -0.5, LineOps.ClosestParameter(AlongX, PointOps.Create(-5, 0, 0)), 1e-12);
        Assert.Equal(
            1.5, LineOps.ClosestParameter(AlongX, PointOps.Create(15, 0, 0)), 1e-12);
    }

    [Fact]
    public void ClosestParameter_ClampsToTheSegmentWhenAsked()
    {
        Assert.Equal(
            0.0,
            LineOps.ClosestParameter(AlongX, PointOps.Create(-5, 0, 0), limitToSegment: true),
            1e-12);

        Assert.Equal(
            1.0,
            LineOps.ClosestParameter(AlongX, PointOps.Create(15, 0, 0), limitToSegment: true),
            1e-12);
    }

    [Fact]
    public void ClosestParameter_ReturnsZeroForADegenerateSegmentRatherThanNaN()
    {
        Line degenerate = LineOps.Create(
            PointOps.Create(1, 1, 1), PointOps.Create(1, 1, 1));

        double parameter = LineOps.ClosestParameter(degenerate, PointOps.Create(9, 9, 9));

        Assert.False(double.IsNaN(parameter));
        Assert.Equal(0.0, parameter);
        Assert.Equal(
            PointOps.Create(1, 1, 1),
            LineOps.ClosestPoint(degenerate, PointOps.Create(9, 9, 9)));
    }

    [Fact]
    public void ClosestPointAndDistance_AgreeWithEachOther()
    {
        Point3d point = PointOps.Create(4, 3, 0);

        Assert.Equal(PointOps.Create(4, 0, 0), LineOps.ClosestPoint(AlongX, point));
        Assert.Equal(3.0, LineOps.DistanceTo(AlongX, point), 1e-12);
    }

    [Fact]
    public void DistanceTo_DiffersForTheSegmentAndTheInfiniteLine()
    {
        Point3d beyondTheEnd = PointOps.Create(13, 4, 0);

        Assert.Equal(4.0, LineOps.DistanceTo(AlongX, beyondTheEnd), 1e-12);
        Assert.Equal(
            5.0, LineOps.DistanceTo(AlongX, beyondTheEnd, limitToSegment: true), 1e-12);
    }

    [Fact]
    public void Transform_MovesBothEndpointsAndLeavesTheOriginalAlone()
    {
        Line moved = LineOps.Transform(AlongX, Transforms.Translate(0, 5, 0));

        Assert.Equal(
            LineOps.Create(PointOps.Create(0, 5, 0), PointOps.Create(10, 5, 0)),
            moved);

        Assert.Equal(PointOps.Create(0, 0, 0), AlongX.From);
        Assert.Equal(LineOps.Length(AlongX), LineOps.Length(moved), 1e-12);
    }

    [Fact]
    public void CreateFromPointDirection_NormalisesTheDirection()
    {
        Line line = LineOps.CreateFromPointDirection(
            PointOps.Create(1, 1, 1), VectorOps.Create(0, 0, 5), 3);

        Assert.Equal(PointOps.Create(1, 1, 1), line.From);
        Assert.Equal(PointOps.Create(1, 1, 4), line.To);
        Assert.Equal(3.0, LineOps.Length(line), 1e-12);
    }

    [Fact]
    public void CreateFromPointDirection_AcceptsANegativeLength()
    {
        Line line = LineOps.CreateFromPointDirection(Point3d.Origin, Vector3d.XAxis, -4);

        Assert.Equal(PointOps.Create(-4, 0, 0), line.To);
    }

    [Fact]
    public void CreateFromPointDirection_RejectsADegenerateDirection()
    {
        // The previous constructor normalised without checking, producing a segment whose endpoint
        // was NaN.
        Assert.Throws<ArgumentException>(
            () => LineOps.CreateFromPointDirection(Point3d.Origin, Vector3d.Zero, 5));

        Assert.False(LineOps.TryCreateFromPointDirection(
            Point3d.Origin, Vector3d.Zero, 5, out Line? line));

        Assert.Null(line);
    }

    [Fact]
    public void TryClosestParameters_SolvesSkewLines()
    {
        Line a = LineOps.Create(PointOps.Create(0, 0, 0), PointOps.Create(2, 0, 0));
        Line b = LineOps.Create(PointOps.Create(1, 1, 0), PointOps.Create(1, 1, 1));

        Assert.True(LineOps.TryClosestParameters(
            a, b, out double? parameterOnA, out double? parameterOnB));

        Assert.Equal(0.5, parameterOnA!.Value, 1e-12);
        Assert.Equal(0.0, parameterOnB!.Value, 1e-12);
    }

    [Fact]
    public void TryClosestPoints_ReturnsThePairAtMinimumSeparation()
    {
        Line a = LineOps.Create(PointOps.Create(0, 0, 0), PointOps.Create(2, 0, 0));
        Line b = LineOps.Create(PointOps.Create(1, 1, 0), PointOps.Create(1, 1, 1));

        Assert.True(LineOps.TryClosestPoints(
            a, b, out Point3d? pointOnA, out Point3d? pointOnB));

        Assert.Equal(PointOps.Create(1, 0, 0), pointOnA!.Value);
        Assert.Equal(PointOps.Create(1, 1, 0), pointOnB!.Value);
        Assert.Equal(1.0, PointOps.DistanceTo(pointOnA.Value, pointOnB.Value), 1e-12);
    }

    [Fact]
    public void TryClosestPoints_FindsTheIntersectionOfCrossingLines()
    {
        Line a = LineOps.Create(PointOps.Create(-1, 0, 0), PointOps.Create(1, 0, 0));
        Line b = LineOps.Create(PointOps.Create(0, -1, 0), PointOps.Create(0, 1, 0));

        Assert.True(LineOps.TryClosestPoints(
            a, b, out Point3d? pointOnA, out Point3d? pointOnB));

        Assert.True(PointOps.EpsilonEquals(pointOnA!.Value, Point3d.Origin, 1e-12));
        Assert.True(PointOps.EpsilonEquals(pointOnB!.Value, Point3d.Origin, 1e-12));
    }

    [Fact]
    public void TryClosestParameters_FailsForParallelLines()
    {
        Line a = LineOps.Create(PointOps.Create(0, 0, 0), PointOps.Create(1, 0, 0));
        Line b = LineOps.Create(PointOps.Create(0, 1, 0), PointOps.Create(1, 1, 0));

        Assert.False(LineOps.TryClosestParameters(
            a, b, out double? parameterOnA, out double? parameterOnB));

        Assert.Null(parameterOnA);
        Assert.Null(parameterOnB);
    }

    [Fact]
    public void TryClosestParameters_FailsWhenEitherLineIsDegenerateOrUnset()
    {
        Line degenerate = LineOps.Create(
            PointOps.Create(0, 1, 0), PointOps.Create(0, 1, 0));

        Assert.False(LineOps.TryClosestParameters(AlongX, degenerate, out _, out _));
        Assert.False(LineOps.TryClosestParameters(AlongX, Line.Unset, out _, out _));
    }

    [Fact]
    public void TryClosestParameters_ClampsToBothSegmentsWhenAsked()
    {
        Line a = LineOps.Create(PointOps.Create(0, 0, 0), PointOps.Create(1, 0, 0));
        Line b = LineOps.Create(PointOps.Create(5, 1, 0), PointOps.Create(5, 1, 1));

        Assert.True(LineOps.TryClosestParameters(
            a, b, out double? parameterOnA, out double? parameterOnB, limitToSegments: true));

        Assert.InRange(parameterOnA!.Value, 0.0, 1.0);
        Assert.InRange(parameterOnB!.Value, 0.0, 1.0);
        Assert.Equal(1.0, parameterOnA.Value, 1e-12);
    }

    [Fact]
    public void EpsilonEquals_IsDirectionSensitive()
    {
        Line nearlyTheSame = LineOps.Create(
            PointOps.Create(0, 0, 0), PointOps.Create(10, 1e-12, 0));

        Assert.True(LineOps.EpsilonEquals(AlongX, nearlyTheSame, 1e-9));
        Assert.False(LineOps.EpsilonEquals(AlongX, LineOps.Flipped(AlongX), 1e-9));
    }
}
