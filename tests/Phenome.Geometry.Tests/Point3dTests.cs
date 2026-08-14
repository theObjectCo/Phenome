using System.Globalization;
using Phenome.Geometry;

namespace Phenome.Geometry.Tests;

/// <summary>Data, equality, operators and formatting. Behaviour lives in PointOpsTests.</summary>
public class Point3dTests
{
    [Fact]
    public void Origin_IsAllZero()
    {
        Assert.Equal(PointOps.Create(0, 0, 0), Point3d.Origin);
        Assert.True(PointOps.IsValid(Point3d.Origin));
    }

    [Fact]
    public void Deconstruct_YieldsCoordinatesInOrder()
    {
        (double x, double y, double z) = PointOps.Create(7, 8, 9);

        Assert.Equal(7, x);
        Assert.Equal(8, y);
        Assert.Equal(9, z);
    }

    [Fact]
    public void SubtractingTwoPoints_YieldsAVector()
    {
        object difference = PointOps.Create(4, 6, 8) - PointOps.Create(1, 2, 3);

        Assert.IsType<Vector3d>(difference);
        Assert.Equal(VectorOps.Create(3, 4, 5), (Vector3d)difference);
    }

    [Fact]
    public void SubtractingAVectorFromAPoint_YieldsAPoint()
    {
        // The previous implementation declared this operator as returning a vector, which is wrong:
        // translating a position by the reverse of a direction leaves a position.
        object translated = PointOps.Create(4, 6, 8) - VectorOps.Create(1, 2, 3);

        Assert.IsType<Point3d>(translated);
        Assert.Equal(PointOps.Create(3, 4, 5), (Point3d)translated);
    }

    [Fact]
    public void MixedArithmetic_TakesItsResultTypeFromTheLeftOperand()
    {
        Point3d point = PointOps.Create(1, 2, 3);
        Vector3d vector = VectorOps.Create(10, 20, 30);

        // A point on the left keeps the expression in point space.
        Assert.IsType<Point3d>((object)(point + vector));
        Assert.IsType<Point3d>((object)(point - vector));
        Assert.IsType<Point3d>((object)(point + point));

        // A vector on the left stays in vector space. `vector + point` and `vector - point` do not appear
        // here because they no longer compile: the overloads exist and are poisoned with [Obsolete(error)],
        // so the compiler forbids them in geometric vocabulary instead of resolving them through an
        // implicit conversion.
        Assert.IsType<Vector3d>((object)(vector + vector));
        Assert.IsType<Vector3d>((object)(vector - vector));

        // The one exception: the difference of two positions is a displacement.
        Assert.IsType<Vector3d>((object)(point - point));
    }

    [Fact]
    public void ScalingIsCommutativeAndDivisionIsItsInverse()
    {
        Point3d point = PointOps.Create(1, -2, 3);

        Assert.Equal(PointOps.Create(2, -4, 6), point * 2);
        Assert.Equal(PointOps.Create(2, -4, 6), 2 * point);
        Assert.Equal(point, (point * 4) / 4);
        Assert.Equal(PointOps.Create(-1, 2, -3), -point);
    }

    [Fact]
    public void Equality_FollowsIeeeRulesForNaN()
    {
        // Two unset points are not "==" to each other, because NaN never equals itself.
        Assert.False(Point3d.Unset == Point3d.Unset);
        Assert.True(Point3d.Unset != Point3d.Unset);
    }

    [Fact]
    public void Equals_TreatsNaNAsEqual_SoUnsetPointsWorkAsDictionaryKeys()
    {
        Assert.True(Point3d.Unset.Equals(Point3d.Unset));
        Assert.Equal(Point3d.Unset.GetHashCode(), Point3d.Unset.GetHashCode());

        var lookup = new Dictionary<Point3d, string>
        {
            [Point3d.Unset] = "unset",
            [PointOps.Create(1, 2, 3)] = "somewhere",
        };

        Assert.True(lookup.ContainsKey(Point3d.Unset));
        Assert.Equal("unset", lookup[Point3d.Unset]);
        Assert.Equal("somewhere", lookup[PointOps.Create(1, 2, 3)]);
    }

    [Fact]
    public void EqualPoints_ShareAHashCode()
    {
        Point3d a = PointOps.Create(1.5, -2.5, 3.5);
        Point3d b = PointOps.Create(1.5, -2.5, 3.5);

        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a == b);
    }

    [Fact]
    public void TupleConvertsImplicitly()
    {
        Point3d point = (1.0, 2.0, 3.0);

        Assert.Equal(PointOps.Create(1, 2, 3), point);
    }

    [Fact]
    public void ToString_UsesInvariantCultureRegardlessOfCurrentCulture()
    {
        // The previous implementation formatted with the current culture, so on a machine set to a
        // comma-decimal locale it emitted "Point(1,5, 0, 0)" — three coordinates, four separators.
        CultureInfo previous = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
            Assert.Equal("Point(1.5, -2.25, 0)", PointOps.Create(1.5, -2.25, 0).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ToString_HonoursExplicitFormatAndProvider()
    {
        Point3d point = PointOps.Create(1.5, 0, 0);

        Assert.Equal("Point(1.50, 0.00, 0.00)", point.ToString("0.00", CultureInfo.InvariantCulture));
        Assert.Equal("Point(unset)", Point3d.Unset.ToString());
    }
}
