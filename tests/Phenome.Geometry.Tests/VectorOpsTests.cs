using Phenome.Geometry;

namespace Phenome.Geometry.Tests;

public class VectorOpsTests
{
    [Fact]
    public void Length_MeasuresMagnitude()
    {
        Vector3d vector = VectorOps.Create(3, 4, 0);

        Assert.Equal(5.0, VectorOps.Length(vector), 1e-12);
        Assert.Equal(25.0, VectorOps.LengthSquared(vector), 1e-12);
    }

    [Fact]
    public void IsValid_RejectsNaNAndInfinity()
    {
        Assert.False(VectorOps.IsValid(Vector3d.Unset));
        Assert.False(VectorOps.IsValid(VectorOps.Create(1, double.NaN, 0)));
        Assert.False(VectorOps.IsValid(VectorOps.Create(double.PositiveInfinity, 0, 0)));
        Assert.True(VectorOps.IsValid(VectorOps.Create(1, 2, 3)));
    }

    [Fact]
    public void TryNormalize_ProducesAUnitVector()
    {
        Assert.True(VectorOps.TryNormalize(VectorOps.Create(0, 0, 7), out Vector3d? unit));

        Assert.NotNull(unit);
        Assert.Equal(Vector3d.ZAxis, unit.Value);
        Assert.True(VectorOps.IsUnit(unit.Value));
    }

    [Fact]
    public void TryNormalize_FailsOnZeroLengthAndYieldsNull()
    {
        // The previous implementation divided by zero here and handed back a NaN vector, which then
        // travelled silently into whatever consumed it. Null cannot be mistaken for a direction.
        Assert.False(VectorOps.TryNormalize(Vector3d.Zero, out Vector3d? unit));

        Assert.Null(unit);
    }

    [Fact]
    public void IgnoringTheTryResult_FailsAtTheCallSiteRatherThanLater()
    {
        VectorOps.TryNormalize(Vector3d.Zero, out Vector3d? unit);

        Assert.Throws<InvalidOperationException>(() => unit!.Value);
    }

    [Fact]
    public void TryNormalize_FailsOnInvalidInput()
    {
        Assert.False(VectorOps.TryNormalize(Vector3d.Unset, out _));
        Assert.False(VectorOps.TryNormalize(
            VectorOps.Create(double.PositiveInfinity, 0, 0), out _));
    }

    [Fact]
    public void Normalized_ThrowsOnDegenerateInput()
    {
        Assert.Throws<InvalidOperationException>(() => VectorOps.Normalized(Vector3d.Zero));
        Assert.Throws<InvalidOperationException>(() => VectorOps.Normalized(Vector3d.Unset));
    }

    [Fact]
    public void Reversed_NegatesEveryComponent()
    {
        Vector3d vector = VectorOps.Create(1, -2, 3);

        Assert.Equal(VectorOps.Create(-1, 2, -3), VectorOps.Reversed(vector));
        Assert.Equal(VectorOps.Reversed(vector), -vector);
        Assert.Equal(vector, VectorOps.Reversed(VectorOps.Reversed(vector)));
    }

    [Fact]
    public void DotProduct_MatchesKnownValues()
    {
        Assert.Equal(0.0, VectorOps.Dot(Vector3d.XAxis, Vector3d.YAxis), 1e-12);
        Assert.Equal(1.0, VectorOps.Dot(Vector3d.XAxis, Vector3d.XAxis), 1e-12);
        Assert.Equal(-1.0, VectorOps.Dot(Vector3d.XAxis, -Vector3d.XAxis), 1e-12);
        Assert.Equal(
            32.0,
            VectorOps.Dot(VectorOps.Create(1, 2, 3), VectorOps.Create(4, 5, 6)),
            1e-12);
    }

    [Fact]
    public void CrossProduct_FollowsTheRightHandRule()
    {
        Assert.Equal(Vector3d.ZAxis, VectorOps.Cross(Vector3d.XAxis, Vector3d.YAxis));
        Assert.Equal(-Vector3d.ZAxis, VectorOps.Cross(Vector3d.YAxis, Vector3d.XAxis));
        Assert.Equal(Vector3d.XAxis, VectorOps.Cross(Vector3d.YAxis, Vector3d.ZAxis));
    }

    [Fact]
    public void CrossProduct_OfParallelVectorsIsZero()
    {
        Vector3d cross = VectorOps.Cross(
            VectorOps.Create(2, 4, 6),
            VectorOps.Create(1, 2, 3));

        Assert.True(VectorOps.IsZero(cross, 1e-12));
    }

    [Theory]
    [InlineData(1, 0, 0, 1, 0, 0, 0.0)]
    [InlineData(1, 0, 0, 0, 1, 0, Math.PI / 2)]
    [InlineData(1, 0, 0, -1, 0, 0, Math.PI)]
    [InlineData(1, 0, 0, 1, 1, 0, Math.PI / 4)]
    public void AngleBetween_MatchesKnownAngles(
        double ax, double ay, double az,
        double bx, double by, double bz,
        double expected)
    {
        double angle = VectorOps.AngleBetween(
            VectorOps.Create(ax, ay, az),
            VectorOps.Create(bx, by, bz));

        Assert.Equal(expected, angle, 1e-12);
    }

    [Fact]
    public void AngleBetween_IsUnsignedSoItCannotDistinguishDirection()
    {
        // Documents the limitation that made the previous ClosestParameter on circles and arcs wrong:
        // an unsigned angle is the same in both directions, so it can never cover a full turn.
        double forward = VectorOps.AngleBetween(Vector3d.XAxis, Vector3d.YAxis);
        double backward = VectorOps.AngleBetween(Vector3d.YAxis, Vector3d.XAxis);

        Assert.Equal(forward, backward, 1e-12);
        Assert.InRange(forward, 0.0, Math.PI);
    }

    [Fact]
    public void AngleBetween_StaysFiniteForNearlyParallelVectors()
    {
        // acos(dot / (|a| |b|)) returns NaN once rounding pushes its argument past 1; the atan2 form
        // used here cannot.
        double nearlyAligned = VectorOps.AngleBetween(
            Vector3d.XAxis, VectorOps.Create(1, 1e-9, 0));

        double nearlyOpposed = VectorOps.AngleBetween(
            Vector3d.XAxis, VectorOps.Create(-1, 1e-9, 0));

        Assert.False(double.IsNaN(nearlyAligned));
        Assert.False(double.IsNaN(nearlyOpposed));
        Assert.Equal(1e-9, nearlyAligned, 1e-14);
        Assert.Equal(Math.PI - 1e-9, nearlyOpposed, 1e-14);
    }

    [Fact]
    public void AngleBetween_RejectsDegenerateInput()
    {
        Assert.Throws<ArgumentException>(
            () => VectorOps.AngleBetween(Vector3d.Zero, Vector3d.XAxis));

        Assert.Throws<ArgumentException>(
            () => VectorOps.AngleBetween(Vector3d.XAxis, Vector3d.Unset));

        Assert.False(VectorOps.TryAngleBetween(Vector3d.Zero, Vector3d.XAxis, out _));
    }

    [Fact]
    public void SignedAngle_ChangesSignWithTheOrderOfTheOperands()
    {
        double forward = VectorOps.SignedAngle(Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis);
        double backward = VectorOps.SignedAngle(Vector3d.YAxis, Vector3d.XAxis, Vector3d.ZAxis);

        Assert.Equal(Math.PI / 2, forward, 1e-12);
        Assert.Equal(-Math.PI / 2, backward, 1e-12);
    }

    [Fact]
    public void SignedAngle_ChangesSignWithTheAxis()
    {
        double aboutZ = VectorOps.SignedAngle(Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis);
        double aboutMinusZ = VectorOps.SignedAngle(Vector3d.XAxis, Vector3d.YAxis, -Vector3d.ZAxis);

        Assert.Equal(-aboutZ, aboutMinusZ, 1e-12);
    }

    [Fact]
    public void SignedAngle_ProjectsOntoThePlanePerpendicularToTheAxis()
    {
        // Neither input lies in the XY plane; both should be projected into it before measuring.
        double angle = VectorOps.SignedAngle(
            VectorOps.Create(1, 0, 5),
            VectorOps.Create(0, 1, -3),
            Vector3d.ZAxis);

        Assert.Equal(Math.PI / 2, angle, 1e-12);
    }

    [Fact]
    public void SignedAngle_CoversAFullTurnUnlikeTheUnsignedForm()
    {
        Vector3d threeQuarters = VectorOps.Create(0, -1, 0);

        double signed = VectorOps.SignedAngle(Vector3d.XAxis, threeQuarters, Vector3d.ZAxis);
        double unsigned = VectorOps.AngleBetween(Vector3d.XAxis, threeQuarters);

        Assert.Equal(-Math.PI / 2, signed, 1e-12);
        Assert.Equal(Math.PI / 2, unsigned, 1e-12);
    }

    [Fact]
    public void SignedAngle_FailsWhenAVectorVanishesUnderProjection()
    {
        Assert.False(VectorOps.TrySignedAngle(
            Vector3d.ZAxis, Vector3d.XAxis, Vector3d.ZAxis, out _));

        Assert.False(VectorOps.TrySignedAngle(
            Vector3d.XAxis, Vector3d.YAxis, Vector3d.Zero, out _));

        Assert.Throws<ArgumentException>(
            () => VectorOps.SignedAngle(Vector3d.ZAxis, Vector3d.XAxis, Vector3d.ZAxis));
    }

    [Fact]
    public void IsParallelTo_AcceptsBothDirections()
    {
        Vector3d vector = VectorOps.Create(1, 2, 3);

        Assert.True(VectorOps.IsParallelTo(vector, vector * 3));
        Assert.True(VectorOps.IsParallelTo(vector, vector * -3));
        Assert.False(VectorOps.IsParallelTo(vector, VectorOps.Create(3, 2, 1)));
        Assert.False(VectorOps.IsParallelTo(vector, Vector3d.Zero));
    }

    [Fact]
    public void IsPerpendicularTo_DetectsRightAngles()
    {
        Assert.True(VectorOps.IsPerpendicularTo(Vector3d.XAxis, Vector3d.YAxis));
        Assert.True(VectorOps.IsPerpendicularTo(
            Vector3d.XAxis, VectorOps.Create(0, 5, -5)));

        Assert.False(VectorOps.IsPerpendicularTo(Vector3d.XAxis, Vector3d.XAxis));
        Assert.False(VectorOps.IsPerpendicularTo(Vector3d.XAxis, Vector3d.Zero));
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(-1, 0, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(1e-6, 1, 0)]
    [InlineData(0.3, -0.7, 12)]
    public void PerpendicularTo_ReturnsAUnitVectorAtRightAngles(double x, double y, double z)
    {
        Vector3d vector = VectorOps.Create(x, y, z);
        Vector3d perpendicular = VectorOps.PerpendicularTo(vector);

        Assert.True(VectorOps.IsUnit(perpendicular, 1e-12));
        Assert.True(VectorOps.IsPerpendicularTo(perpendicular, vector, 1e-9));
    }

    [Fact]
    public void PerpendicularTo_RejectsDegenerateInput()
    {
        Assert.False(VectorOps.TryPerpendicularTo(Vector3d.Zero, out Vector3d? perpendicular));
        Assert.Null(perpendicular);
        Assert.Throws<InvalidOperationException>(() => VectorOps.PerpendicularTo(Vector3d.Zero));
    }

    [Fact]
    public void EpsilonEquals_UsesTheSuppliedTolerance()
    {
        Vector3d a = VectorOps.Create(1, 0, 0);
        Vector3d b = VectorOps.Create(1.001, 0, 0);

        Assert.True(VectorOps.EpsilonEquals(a, b, 0.01));
        Assert.False(VectorOps.EpsilonEquals(a, b, 0.0001));
    }

    [Fact]
    public void CreateFromComponents_ReadsTheFirstThreeValues()
    {
        Assert.Equal(VectorOps.Create(1, 2, 3), VectorOps.CreateFromComponents([1, 2, 3]));
        Assert.Equal(VectorOps.Create(1, 2, 3), VectorOps.CreateFromComponents([1, 2, 3, 4]));
    }

    [Fact]
    public void CreateFromComponents_RejectsATooShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => VectorOps.CreateFromComponents([1, 2]));
        Assert.Throws<ArgumentException>(
            () => VectorOps.CreateFromComponents(ReadOnlySpan<double>.Empty));
    }

    [Fact]
    public void Sum_AddsEveryComponent()
    {
        Vector3d[] vectors =
        [
            VectorOps.Create(1, 0, 0),
            VectorOps.Create(0, 2, 0),
            VectorOps.Create(0, 0, 3),
        ];

        Assert.Equal(VectorOps.Create(1, 2, 3), VectorOps.Sum(vectors));
    }

    [Fact]
    public void Sum_OfNothingIsZero()
    {
        Assert.Equal(Vector3d.Zero, VectorOps.Sum(ReadOnlySpan<Vector3d>.Empty));
    }

    [Fact]
    public void TryAverageDirection_ReturnsTheUnitOfTheSum()
    {
        Vector3d[] vectors = [Vector3d.XAxis, Vector3d.YAxis];

        Assert.True(VectorOps.TryAverageDirection(vectors, out Vector3d? average));
        Assert.NotNull(average);
        Assert.True(VectorOps.IsUnit(average.Value, 1e-12));
        Assert.True(VectorOps.EpsilonEquals(
            average.Value,
            VectorOps.Normalized(VectorOps.Create(1, 1, 0)),
            1e-12));
    }

    [Fact]
    public void TryAverageDirection_WeightsByMagnitude()
    {
        // Summing before normalising means a longer vector pulls harder. Pass unit vectors when every
        // contribution should count equally.
        Vector3d[] vectors = [Vector3d.XAxis * 100, Vector3d.YAxis];

        Assert.True(VectorOps.TryAverageDirection(vectors, out Vector3d? average));
        Assert.NotNull(average);
        Assert.True(average.Value.X > 0.99);
    }

    [Fact]
    public void TryAverageDirection_FailsWhenTheVectorsCancelOut()
    {
        Vector3d[] opposed = [Vector3d.XAxis, -Vector3d.XAxis];

        Assert.False(VectorOps.TryAverageDirection(opposed, out Vector3d? average));
        Assert.Null(average);
        Assert.False(VectorOps.TryAverageDirection(ReadOnlySpan<Vector3d>.Empty, out _));
    }

    [Fact]
    public void Transform_IgnoresTranslation()
    {
        // A direction has no position, so moving the world must leave it untouched.
        TMatrix translation = Transforms.Translate(10, 20, 30);

        Assert.Equal(Vector3d.XAxis, VectorOps.Transform(Vector3d.XAxis, translation));
        Assert.Equal(
            VectorOps.Create(1, 2, 3),
            VectorOps.Transform(VectorOps.Create(1, 2, 3), translation));
    }
}
