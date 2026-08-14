using Phenome.Geometry;

namespace Phenome.Geometry.Tests;

/// <summary>Data, equality, operators and formatting. Behaviour lives in VectorOpsTests.</summary>
public class Vector3dTests
{
    [Fact]
    public void Axes_AreUnitVectors()
    {
        Assert.True(VectorOps.IsUnit(Vector3d.XAxis));
        Assert.True(VectorOps.IsUnit(Vector3d.YAxis));
        Assert.True(VectorOps.IsUnit(Vector3d.ZAxis));
        Assert.True(VectorOps.IsZero(Vector3d.Zero));
        Assert.False(VectorOps.IsUnit(Vector3d.Zero));
    }

    [Fact]
    public void Deconstruct_YieldsComponentsInOrder()
    {
        (double x, double y, double z) = VectorOps.Create(7, 8, 9);

        Assert.Equal(7, x);
        Assert.Equal(8, y);
        Assert.Equal(9, z);
    }

    [Fact]
    public void ArithmeticOperators_WorkComponentWise()
    {
        Vector3d a = VectorOps.Create(1, 2, 3);
        Vector3d b = VectorOps.Create(10, 20, 30);

        Assert.Equal(VectorOps.Create(11, 22, 33), a + b);
        Assert.Equal(VectorOps.Create(-9, -18, -27), a - b);
        Assert.Equal(VectorOps.Create(2, 4, 6), a * 2);
        Assert.Equal(VectorOps.Create(2, 4, 6), 2 * a);
        Assert.Equal(a, (a * 4) / 4);
        Assert.Equal(VectorOps.Create(-1, -2, -3), -a);
    }

    [Fact]
    public void MixedVectorAndPointArithmetic_IsForbiddenWithTheVectorOnTheLeft()
    {
        // `vector + point` and `vector - point` are declared and poisoned with [Obsolete(error: true)]:
        // with implicit conversions in both directions, an *absent* overload would not forbid the
        // expression - the compiler would convert the point and add two vectors, silently. Declared and
        // poisoned, the expression fails to compile with a message about geometry. A compile-time ban
        // cannot be exercised from a test that has to compile, so this asserts the declaration is there
        // and marked the way the ban requires.
        System.Reflection.MethodInfo? plus = typeof(Vector3d).GetMethod(
            "op_Addition", [typeof(Vector3d), typeof(Point3d)]);
        System.Reflection.MethodInfo? minus = typeof(Vector3d).GetMethod(
            "op_Subtraction", [typeof(Vector3d), typeof(Point3d)]);

        Assert.NotNull(plus);
        Assert.NotNull(minus);

        Assert.True(System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<ObsoleteAttribute>(plus)!.IsError);
        Assert.True(System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<ObsoleteAttribute>(minus)!.IsError);
    }

    [Fact]
    public void TupleConvertsImplicitly()
    {
        Vector3d vector = (1.0, 2.0, 3.0);

        Assert.Equal(VectorOps.Create(1, 2, 3), vector);
    }

    [Fact]
    public void Equals_TreatsNaNAsEqual_SoUnsetVectorsWorkAsDictionaryKeys()
    {
        Assert.True(Vector3d.Unset.Equals(Vector3d.Unset));
        Assert.False(Vector3d.Unset == Vector3d.Unset);

        var lookup = new HashSet<Vector3d> { Vector3d.Unset };

        Assert.Contains(Vector3d.Unset, lookup);
    }

    [Fact]
    public void ToString_IsInvariantAndNamesUnsetVectors()
    {
        Assert.Equal("Vector(1.5, 0, -2)", VectorOps.Create(1.5, 0, -2).ToString());
        Assert.Equal("Vector(unset)", Vector3d.Unset.ToString());
    }
}
