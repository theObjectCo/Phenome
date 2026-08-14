namespace Phenome.Geometry.Tests;

public class IntervalOpsTests
{
    [Fact]
    public void Create_KeepsTheOrderGivenSoThatADecreasingIntervalSurvives()
    {
        // A decreasing interval is the only way to express a clockwise sweep, so sorting here would throw
        // away information the caller meant to supply.
        Interval decreasing = IntervalOps.Create(5, 1);

        Assert.Equal(5, decreasing.T0);
        Assert.Equal(1, decreasing.T1);
        Assert.True(IntervalOps.IsDecreasing(decreasing));
    }

    [Fact]
    public void CreateFromSorted_PutsTheBoundsInIncreasingOrder()
    {
        Assert.Equal(IntervalOps.Create(1, 5), IntervalOps.CreateFromSorted(5, 1));
        Assert.Equal(IntervalOps.Create(1, 5), IntervalOps.CreateFromSorted(1, 5));
    }

    [Fact]
    public void CreateFromCenter_IgnoresTheSignOfTheRadius()
    {
        Interval fromNegative = IntervalOps.CreateFromCenter(10, -2);

        Assert.Equal(8, fromNegative.T0);
        Assert.Equal(12, fromNegative.T1);
    }

    [Fact]
    public void Length_IsSignedSoThatDirectionSurvives()
    {
        Assert.Equal(4, IntervalOps.Length(IntervalOps.Create(1, 5)));
        Assert.Equal(-4, IntervalOps.Length(IntervalOps.Create(5, 1)));
    }

    [Fact]
    public void MinAndMax_DoNotAssumeTheIntervalIncreases()
    {
        Interval decreasing = IntervalOps.Create(5, 1);

        Assert.Equal(1, IntervalOps.Min(decreasing));
        Assert.Equal(5, IntervalOps.Max(decreasing));
    }

    [Fact]
    public void Mid_AveragesTheBoundsRatherThanOffsettingFromOne()
    {
        // Averaging keeps the midpoint accurate for bounds far from the origin, where adding half a length
        // to T0 would round away the offset entirely.
        Interval far = IntervalOps.Create(1e16, 1e16 + 4);

        Assert.Equal(1e16 + 2, IntervalOps.Mid(far));
    }

    [Fact]
    public void IsValid_RejectsInfiniteAndUnsetBounds()
    {
        Assert.True(IntervalOps.IsValid(Interval.Unit));
        Assert.False(IntervalOps.IsValid(Interval.Unset));
        Assert.False(IntervalOps.IsValid(IntervalOps.Create(0, double.PositiveInfinity)));
    }

    [Fact]
    public void IsSingleton_HoldsForAnIntervalCoveringOneValue()
    {
        Interval singleton = IntervalOps.Create(3);

        Assert.True(IntervalOps.IsSingleton(singleton));
        Assert.True(IntervalOps.IsValid(singleton));
        Assert.False(IntervalOps.IsIncreasing(singleton));
        Assert.False(IntervalOps.IsDecreasing(singleton));
    }

    [Fact]
    public void ParameterAt_MapsTheUnitRangeOntoTheBoundsAndExtrapolatesBeyondIt()
    {
        Interval interval = IntervalOps.Create(10, 20);

        Assert.Equal(10, IntervalOps.ParameterAt(interval, 0));
        Assert.Equal(15, IntervalOps.ParameterAt(interval, 0.5));
        Assert.Equal(20, IntervalOps.ParameterAt(interval, 1));
        Assert.Equal(25, IntervalOps.ParameterAt(interval, 1.5));
    }

    [Fact]
    public void ParameterAt_FollowsADecreasingIntervalBackwards()
    {
        Interval decreasing = IntervalOps.Create(20, 10);

        Assert.Equal(20, IntervalOps.ParameterAt(decreasing, 0));
        Assert.Equal(10, IntervalOps.ParameterAt(decreasing, 1));
    }

    [Fact]
    public void NormalizedParameterAt_IsTheInverseOfParameterAt()
    {
        Interval interval = IntervalOps.Create(-3, 7);

        foreach (double t in new[] { -0.5, 0, 0.25, 1, 2 })
        {
            double roundTripped = IntervalOps.NormalizedParameterAt(
                interval,
                IntervalOps.ParameterAt(interval, t));

            Assert.Equal(t, roundTripped, 12);
        }
    }

    [Fact]
    public void NormalizedParameterAt_HasNoAnswerOnASingleton()
    {
        // Every value would map to the same place, so no parameter can be recovered. Returning NaN says so
        // rather than dividing by zero somewhere further downstream.
        Assert.True(double.IsNaN(
            IntervalOps.NormalizedParameterAt(IntervalOps.Create(4), 4)));
    }

    [Fact]
    public void Includes_IgnoresDirectionAndCanExcludeTheBounds()
    {
        Interval decreasing = IntervalOps.Create(5, 1);

        Assert.True(IntervalOps.Includes(decreasing, 3));
        Assert.True(IntervalOps.Includes(decreasing, 1));
        Assert.False(IntervalOps.Includes(decreasing, 1, includeBounds: false));
        Assert.False(IntervalOps.Includes(decreasing, 6));
    }

    [Fact]
    public void Clamped_MovesAValueToTheNearestBound()
    {
        Interval interval = IntervalOps.Create(1, 5);

        Assert.Equal(1, IntervalOps.Clamped(interval, -10));
        Assert.Equal(5, IntervalOps.Clamped(interval, 10));
        Assert.Equal(3, IntervalOps.Clamped(interval, 3));
    }

    [Fact]
    public void Grown_KeepsTheDirectionItAlreadyHad()
    {
        Interval decreasing = IntervalOps.Create(5, 2);
        Interval grown = IntervalOps.Grown(decreasing, 7);

        Assert.Equal(7, grown.T0);
        Assert.Equal(2, grown.T1);
        Assert.True(IntervalOps.IsDecreasing(grown));
    }

    [Fact]
    public void Grown_StartsFromTheValueWhenTheIntervalWasUnset()
    {
        Interval grown = IntervalOps.Grown(Interval.Unset, 3);

        Assert.True(IntervalOps.IsSingleton(grown));
        Assert.Equal(3, grown.T0);
    }

    [Fact]
    public void Bound_CoversEveryValueAndReportsUnsetForNone()
    {
        Assert.Equal(
            IntervalOps.Create(-2, 9),
            IntervalOps.Bound([3, -2, 9, 0]));

        Assert.False(IntervalOps.IsValid(IntervalOps.Bound([])));
    }

    [Fact]
    public void Union_IncreasesEvenWhenTheInputsDoNot()
    {
        Interval union = IntervalOps.Union(
            IntervalOps.Create(5, 1),
            IntervalOps.Create(9, 7));

        Assert.Equal(IntervalOps.Create(1, 9), union);
    }

    [Fact]
    public void Overlaps_CountsTouchingAtABound()
    {
        Assert.True(IntervalOps.Overlaps(
            IntervalOps.Create(0, 5),
            IntervalOps.Create(5, 9)));

        Assert.False(IntervalOps.Overlaps(
            IntervalOps.Create(0, 5),
            IntervalOps.Create(6, 9)));
    }

    [Fact]
    public void TryIntersection_GivesTheSharedRangeOrNull()
    {
        Assert.True(IntervalOps.TryIntersection(
            IntervalOps.Create(0, 5),
            IntervalOps.Create(3, 9),
            out Interval? shared));

        Assert.Equal(IntervalOps.Create(3, 5), shared.Value);

        Assert.False(IntervalOps.TryIntersection(
            IntervalOps.Create(0, 5),
            IntervalOps.Create(6, 9),
            out Interval? none));

        Assert.Null(none);
    }

    [Fact]
    public void TryIntersection_IgnoresTheDirectionOfItsInputs()
    {
        Assert.True(IntervalOps.TryIntersection(
            IntervalOps.Create(5, 0),
            IntervalOps.Create(9, 3),
            out Interval? shared));

        Assert.Equal(IntervalOps.Create(3, 5), shared.Value);
    }

    [Fact]
    public void Equality_DistinguishesAnIntervalFromItsReversal()
    {
        // The two cover the same values but sweep opposite ways, and an arc built on each is a different
        // arc, so they must not compare equal.
        Interval forward = IntervalOps.Create(0, 1);
        Interval backward = IntervalOps.Create(1, 0);

        Assert.NotEqual(forward, backward);
        Assert.True(forward != backward);
    }

    [Fact]
    public void Equality_TreatsUnsetAsEqualToItselfSoItWorksAsADictionaryKey()
    {
        Assert.True(Interval.Unset.Equals(Interval.Unset));
        Assert.False(Interval.Unset == Interval.Unset);

        Dictionary<Interval, string> byInterval = new() { [Interval.Unset] = "unset" };
        Assert.Equal("unset", byInterval[Interval.Unset]);
    }

    [Fact]
    public void ToString_PrintsBothBoundsInStoredOrder()
    {
        Assert.Equal("Interval(5 .. 1)", IntervalOps.Create(5, 1).ToString());
    }
}
