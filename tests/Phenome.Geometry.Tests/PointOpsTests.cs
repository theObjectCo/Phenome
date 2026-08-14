using Phenome.Geometry;

namespace Phenome.Geometry.Tests;

public class PointOpsTests
{
    [Fact]
    public void IsValid_RejectsNaNAndInfinity()
    {
        Assert.False(PointOps.IsValid(Point3d.Unset));
        Assert.False(PointOps.IsValid(PointOps.Create(1, double.NaN, 3)));
        Assert.False(PointOps.IsValid(PointOps.Create(double.PositiveInfinity, 0, 0)));
        Assert.False(PointOps.IsValid(PointOps.Create(0, double.NegativeInfinity, 0)));
        Assert.True(PointOps.IsValid(PointOps.Create(1, 2, 3)));
    }

    [Fact]
    public void DistanceTo_MeasuresEuclideanDistance()
    {
        Point3d a = PointOps.Create(1, 2, 3);
        Point3d b = PointOps.Create(4, 6, 3);

        Assert.Equal(5.0, PointOps.DistanceTo(a, b), 1e-12);
        Assert.Equal(25.0, PointOps.DistanceSquaredTo(a, b), 1e-12);
    }

    [Fact]
    public void DistanceTo_IsSymmetricAndZeroForSelf()
    {
        Point3d a = PointOps.Create(-3, 7, 0.5);
        Point3d b = PointOps.Create(11, -2, 4);

        Assert.Equal(PointOps.DistanceTo(a, b), PointOps.DistanceTo(b, a), 1e-12);
        Assert.Equal(0.0, PointOps.DistanceTo(a, a), 1e-12);
    }

    [Fact]
    public void EpsilonEquals_UsesTheSuppliedTolerance()
    {
        Point3d a = PointOps.Create(0, 0, 0);
        Point3d b = PointOps.Create(0.001, 0, 0);

        Assert.True(PointOps.EpsilonEquals(a, b, 0.01));
        Assert.False(PointOps.EpsilonEquals(a, b, 0.0001));
        Assert.False(PointOps.EpsilonEquals(a, b));
    }

    [Fact]
    public void Lerp_HitsEndpointsAndInterpolatesBetween()
    {
        Point3d from = PointOps.Create(0, 0, 0);
        Point3d to = PointOps.Create(10, 20, -30);

        Assert.Equal(from, PointOps.Lerp(from, to, 0));
        Assert.Equal(to, PointOps.Lerp(from, to, 1));
        Assert.Equal(PointOps.Create(5, 10, -15), PointOps.Lerp(from, to, 0.5));
    }

    [Fact]
    public void Lerp_ExtrapolatesOutsideUnitInterval()
    {
        Point3d from = PointOps.Create(0, 0, 0);
        Point3d to = PointOps.Create(2, 0, 0);

        Assert.Equal(PointOps.Create(-2, 0, 0), PointOps.Lerp(from, to, -1));
        Assert.Equal(PointOps.Create(4, 0, 0), PointOps.Lerp(from, to, 2));
    }

    [Fact]
    public void Centroid_AveragesPoints()
    {
        Point3d[] points =
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(3, 0, 0),
            PointOps.Create(0, 3, 0),
        ];

        // Exercises both overloads, and confirms an array binds without ambiguity.
        Assert.Equal(PointOps.Create(1, 1, 0), PointOps.Centroid(points));
        Assert.Equal(
            PointOps.Create(1, 1, 0),
            PointOps.Centroid((IEnumerable<Point3d>)points));
    }

    [Fact]
    public void Centroid_OfASinglePointIsThatPoint()
    {
        Point3d point = PointOps.Create(7, -2, 4);

        Assert.Equal(point, PointOps.Centroid([point]));
    }

    [Fact]
    public void Centroid_RejectsEmptyInput()
    {
        Assert.Throws<ArgumentException>(() => PointOps.Centroid(ReadOnlySpan<Point3d>.Empty));
        Assert.Throws<ArgumentException>(
            () => PointOps.Centroid(Array.Empty<Point3d>().AsEnumerable()));

        // Null is not tested for: the parameter is not nullable, so the signature is the contract and the
        // compiler enforces it at the call site rather than this doing it again at run time.
    }

    [Fact]
    public void CreateFromCoordinates_ReadsTheFirstThreeValues()
    {
        Assert.Equal(PointOps.Create(1, 2, 3), PointOps.CreateFromCoordinates([1, 2, 3]));
        Assert.Equal(PointOps.Create(1, 2, 3), PointOps.CreateFromCoordinates([1, 2, 3, 4, 5]));
    }

    [Fact]
    public void CreateFromCoordinates_ReadsFromAnOffsetWhenTheSpanIsSliced()
    {
        double[] buffer = [0, 0, 0, 1, 2, 3];

        Assert.Equal(
            PointOps.Create(1, 2, 3),
            PointOps.CreateFromCoordinates(buffer.AsSpan(3)));
    }

    [Fact]
    public void CreateFromCoordinates_RejectsATooShortBuffer()
    {
        // Taking a span rather than an array collapses the null case into the empty case, so there is
        // one failure mode instead of two.
        Assert.Throws<ArgumentException>(() => PointOps.CreateFromCoordinates([1, 2]));
        Assert.Throws<ArgumentException>(
            () => PointOps.CreateFromCoordinates(ReadOnlySpan<double>.Empty));
    }

    [Fact]
    public void TryClosestIndex_FindsTheNearestCandidate()
    {
        Point3d[] points =
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(10, 0, 0),
            PointOps.Create(3, 0, 0),
        ];

        Assert.True(PointOps.TryClosestIndex(
            points, PointOps.Create(4, 0, 0), out int? index));

        Assert.Equal(2, index);
    }

    [Fact]
    public void TryClosestIndex_FailsOnAnEmptySet()
    {
        Assert.False(PointOps.TryClosestIndex(
            ReadOnlySpan<Point3d>.Empty, Point3d.Origin, out int? index));

        Assert.Null(index);
    }

    [Fact]
    public void Transform_MovesThePointIncludingTranslation()
    {
        TMatrix translation = Transforms.Translate(10, 20, 30);

        Assert.Equal(
            PointOps.Create(11, 22, 33),
            PointOps.Transform(PointOps.Create(1, 2, 3), translation));
    }

    [Fact]
    public void Transform_DividesByWWhenTheMatrixIsNotAffine()
    {
        // Bottom row (0, 0, 1, 0) makes w equal to z.
        TMatrix perspective = Transforms.Create(
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 1, 0);

        Assert.Equal(
            PointOps.Create(1, 2, 1),
            PointOps.Transform(PointOps.Create(2, 4, 2), perspective));
    }

    [Fact]
    public void Transform_ReturnsUnsetWhenWCollapsesToZero()
    {
        TMatrix perspective = Transforms.Create(
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 1, 0);

        Point3d result = PointOps.Transform(PointOps.Create(2, 4, 0), perspective);

        Assert.False(PointOps.IsValid(result));
        Assert.True(result.Equals(Point3d.Unset));
    }
}
