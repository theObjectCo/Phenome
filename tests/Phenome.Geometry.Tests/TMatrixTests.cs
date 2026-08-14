using Phenome.Geometry;

namespace Phenome.Geometry.Tests;

/// <summary>Data, indexing, equality and formatting. Behaviour lives in TransformsTests.</summary>
public class TMatrixTests
{
    [Fact]
    public void Indexer_ReadsRowsAndColumnsOneBased()
    {
        TMatrix translation = Transforms.Translate(5, 6, 7);

        Assert.Equal(1.0, translation[1, 1]);
        Assert.Equal(5.0, translation[1, 4]);
        Assert.Equal(6.0, translation[2, 4]);
        Assert.Equal(7.0, translation[3, 4]);
        Assert.Equal(1.0, translation[4, 4]);
        Assert.Equal(0.0, translation[4, 1]);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 5)]
    public void Indexer_RejectsOutOfRangeIndices(int row, int column)
    {
        TMatrix identity = TMatrix.Identity;

        Assert.Throws<ArgumentOutOfRangeException>(() => identity[row, column]);
    }

    [Fact]
    public void Multiplication_AppliesTheRightHandMatrixFirst()
    {
        TMatrix translate = Transforms.Translate(10, 0, 0);
        TMatrix scale = Transforms.Scale(2);
        Point3d point = PointOps.Create(1, 0, 0);

        // Scale first, then translate: 1 -> 2 -> 12.
        Assert.Equal(
            PointOps.Create(12, 0, 0),
            PointOps.Transform(point, translate * scale));

        // Translate first, then scale: 1 -> 11 -> 22.
        Assert.Equal(
            PointOps.Create(22, 0, 0),
            PointOps.Transform(point, scale * translate));
    }

    [Fact]
    public void Multiplication_MatchesApplyingEachTransformInTurn()
    {
        TMatrix first = Transforms.Rotate(Vector3d.ZAxis, 0.4);
        TMatrix second = Transforms.Translate(3, -1, 2);
        Point3d point = PointOps.Create(7, 8, 9);

        Point3d composed = PointOps.Transform(point, second * first);
        Point3d stepwise = PointOps.Transform(PointOps.Transform(point, first), second);

        Assert.True(PointOps.EpsilonEquals(composed, stepwise, 1e-12));
    }

    [Fact]
    public void Equality_MatchesOnEveryEntry()
    {
        TMatrix a = Transforms.Translate(1, 2, 3);
        TMatrix b = Transforms.Translate(1, 2, 3);

        Assert.True(a == b);
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a != Transforms.Translate(1, 2, 4));
    }

    [Fact]
    public void ToString_LaysTheMatrixOutRowByRow()
    {
        string text = Transforms.Translate(1, 2, 3).ToString();

        Assert.Contains("[1 0 0 1]", text);
        Assert.Contains("[0 1 0 2]", text);
        Assert.Contains("[0 0 1 3]", text);
        Assert.Contains("[0 0 0 1]", text);
    }
}
