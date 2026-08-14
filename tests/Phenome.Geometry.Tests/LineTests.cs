using Phenome.Geometry;

namespace Phenome.Geometry.Tests;

/// <summary>Data, equality and formatting. Behaviour lives in LineOpsTests.</summary>
public class LineTests
{
    private static readonly Line AlongX = LineOps.Create(
        PointOps.Create(0, 0, 0),
        PointOps.Create(10, 0, 0));

    [Fact]
    public void Create_KeepsTheEndpointsInOrder()
    {
        Assert.Equal(PointOps.Create(0, 0, 0), AlongX.From);
        Assert.Equal(PointOps.Create(10, 0, 0), AlongX.To);
    }

    [Fact]
    public void Deconstruct_YieldsEndpointsInOrder()
    {
        (Point3d from, Point3d to) = AlongX;

        Assert.Equal(PointOps.Create(0, 0, 0), from);
        Assert.Equal(PointOps.Create(10, 0, 0), to);
    }

    [Fact]
    public void Equals_TreatsNaNAsEqual_SoUnsetSegmentsWorkAsDictionaryKeys()
    {
        Assert.True(Line.Unset.Equals(Line.Unset));
        Assert.False(Line.Unset == Line.Unset);
        Assert.Equal(Line.Unset.GetHashCode(), Line.Unset.GetHashCode());

        var lookup = new HashSet<Line> { Line.Unset, AlongX };

        Assert.Contains(Line.Unset, lookup);
        Assert.Contains(AlongX, lookup);
        Assert.DoesNotContain(LineOps.Flipped(AlongX), lookup);
    }

    [Fact]
    public void ToString_ShowsBothEndpoints()
    {
        Assert.Equal("Line(Point(0, 0, 0) -> Point(10, 0, 0))", AlongX.ToString());
    }
}
