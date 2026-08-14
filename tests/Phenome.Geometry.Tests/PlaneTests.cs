using Phenome.Geometry;

namespace Phenome.Geometry.Tests;

/// <summary>Data, equality and formatting. Behaviour lives in PlaneOpsTests.</summary>
public class PlaneTests
{
    [Fact]
    public void WorldPlanes_AreOrthonormalAndRightHanded()
    {
        foreach (Plane plane in new[] { Plane.WorldXY, Plane.WorldYZ, Plane.WorldZX })
        {
            Assert.True(PlaneOps.IsValid(plane));
            Assert.True(VectorOps.IsUnit(plane.XAxis, 1e-12));
            Assert.True(VectorOps.IsUnit(plane.YAxis, 1e-12));
            Assert.True(VectorOps.IsUnit(plane.ZAxis, 1e-12));
            Assert.True(VectorOps.IsPerpendicularTo(plane.XAxis, plane.YAxis, 1e-12));
            Assert.True(VectorOps.EpsilonEquals(
                VectorOps.Cross(plane.XAxis, plane.YAxis), plane.ZAxis, 1e-12));
        }
    }

    [Fact]
    public void WorldXY_HasTheExpectedAxes()
    {
        Assert.Equal(Point3d.Origin, Plane.WorldXY.Origin);
        Assert.Equal(Vector3d.XAxis, Plane.WorldXY.XAxis);
        Assert.Equal(Vector3d.YAxis, Plane.WorldXY.YAxis);
        Assert.Equal(Vector3d.ZAxis, Plane.WorldXY.ZAxis);
        Assert.Equal(Vector3d.ZAxis, Plane.WorldXY.Normal);
    }

    [Fact]
    public void Deconstruct_YieldsOriginAndAxesInOrder()
    {
        (Point3d origin, Vector3d x, Vector3d y, Vector3d z) = Plane.WorldXY;

        Assert.Equal(Point3d.Origin, origin);
        Assert.Equal(Vector3d.XAxis, x);
        Assert.Equal(Vector3d.YAxis, y);
        Assert.Equal(Vector3d.ZAxis, z);
    }

    [Fact]
    public void Unset_IsNotValid()
    {
        Assert.False(PlaneOps.IsValid(Plane.Unset));
        Assert.Equal("Plane(unset)", Plane.Unset.ToString());
    }

    [Fact]
    public void Equals_TreatsNaNAsEqual_SoUnsetPlanesWorkAsDictionaryKeys()
    {
        Assert.True(Plane.Unset.Equals(Plane.Unset));
        Assert.False(Plane.Unset == Plane.Unset);

        var lookup = new HashSet<Plane> { Plane.Unset, Plane.WorldXY };

        Assert.Contains(Plane.Unset, lookup);
        Assert.Contains(Plane.WorldXY, lookup);
    }

    [Fact]
    public void ToString_NamesTheOriginAndNormal()
    {
        Assert.Equal("Plane(O Point(0, 0, 0); N Vector(0, 0, 1))", Plane.WorldXY.ToString());
    }
}
