using Phenome.Geometry;
using Vector2f = System.Numerics.Vector2;
using Vector3f = System.Numerics.Vector3;

namespace Phenome.Geometry.Tests;

/// <summary>Storage, access and attributes. Behaviour lives in MeshOpsTests.</summary>
public class MeshTests
{
    /// <summary>A triangle, a quad and a triangle sharing vertices — mixed corner counts on purpose.</summary>
    private static Mesh MixedFaces()
    {
        Mesh mesh = MeshOps.Create();

        mesh.AddVertices(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(1, 1, 0),
            PointOps.Create(0, 1, 0),
            PointOps.Create(2, 0, 0),
            PointOps.Create(2, 1, 0),
        ]);

        mesh.AddFace(0, 1, 2);
        mesh.AddFace(0, 2, 3, 4);
        mesh.AddFace(4, 5, 1);

        return mesh;
    }

    [Fact]
    public void NewMesh_IsEmpty()
    {
        Mesh mesh = MeshOps.Create();

        Assert.Equal(0, mesh.VertexCount);
        Assert.Equal(0, mesh.FaceCount);
        Assert.Equal(0, mesh.FaceCornerCount);
        Assert.True(mesh.Vertices.IsEmpty);
    }

    [Fact]
    public void AddVertex_ReturnsTheIndexItWasGiven()
    {
        Mesh mesh = MeshOps.Create();

        Assert.Equal(0, mesh.AddVertex(PointOps.Create(1, 2, 3)));
        Assert.Equal(1, mesh.AddVertex(PointOps.Create(4, 5, 6)));
        Assert.Equal(2, mesh.VertexCount);
        Assert.Equal(PointOps.Create(4, 5, 6), mesh.Vertices[1]);
    }

    [Fact]
    public void AddVertices_ReturnsTheIndexOfTheFirst()
    {
        Mesh mesh = MeshOps.Create();
        mesh.AddVertex(Point3d.Origin);

        int first = mesh.AddVertices(
            [PointOps.Create(1, 0, 0), PointOps.Create(2, 0, 0)]);

        Assert.Equal(1, first);
        Assert.Equal(3, mesh.VertexCount);
    }

    [Fact]
    public void Face_SlicesTheFlatCornerBufferCorrectly()
    {
        // The whole point of the compressed layout: face i is the slice between two offsets, so faces of
        // different corner counts sit in one buffer with no per-face allocation.
        Mesh mesh = MixedFaces();

        Assert.Equal(3, mesh.FaceCount);
        Assert.Equal(10, mesh.FaceCornerCount);

        Assert.Equal([0, 1, 2], mesh.Face(0).ToArray());
        Assert.Equal([0, 2, 3, 4], mesh.Face(1).ToArray());
        Assert.Equal([4, 5, 1], mesh.Face(2).ToArray());
    }

    [Fact]
    public void CornersInFace_MatchesTheSliceLength()
    {
        Mesh mesh = MixedFaces();

        Assert.Equal(3, mesh.CornersInFace(0));
        Assert.Equal(4, mesh.CornersInFace(1));
        Assert.Equal(3, mesh.CornersInFace(2));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Face_RejectsAnIndexOutsideTheMesh(int faceIndex)
    {
        Mesh mesh = MixedFaces();

        Assert.Throws<ArgumentOutOfRangeException>(() => mesh.Face(faceIndex).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => mesh.CornersInFace(faceIndex));
    }

    [Fact]
    public void AddFace_RejectsFewerThanThreeCorners()
    {
        Mesh mesh = MeshOps.Create();
        mesh.AddVertices([Point3d.Origin, PointOps.Create(1, 0, 0)]);

        Assert.Throws<ArgumentException>(() => mesh.AddFace([0, 1]));
        Assert.Throws<ArgumentException>(() => mesh.AddFace([]));
    }

    [Fact]
    public void AddFace_RejectsAnIndexWithNoVertexBehindIt()
    {
        // Appending is the only moment where this is checkable, and an out-of-range corner is cheap to
        // catch here and painful to diagnose once it reaches a renderer.
        Mesh mesh = MeshOps.Create();
        mesh.AddVertices([Point3d.Origin, PointOps.Create(1, 0, 0), PointOps.Create(1, 1, 0)]);

        Assert.Throws<ArgumentOutOfRangeException>(() => mesh.AddFace(0, 1, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => mesh.AddFace(0, -1, 2));
        Assert.Equal(0, mesh.FaceCount);
    }

    [Fact]
    public void VerticesForWriting_MutatesInPlaceWithoutCopying()
    {
        Mesh mesh = MixedFaces();

        Span<Point3d> vertices = mesh.VerticesForWriting();
        vertices[0] = PointOps.Create(9, 9, 9);

        Assert.Equal(PointOps.Create(9, 9, 9), mesh.Vertices[0]);
    }

    [Fact]
    public void Clear_EmptiesEverythingIncludingAttributes()
    {
        Mesh mesh = MixedFaces();
        mesh.SetVertexColors(Enumerable.Repeat(Color32.White, mesh.VertexCount).ToArray());

        mesh.Clear();

        Assert.Equal(0, mesh.VertexCount);
        Assert.Equal(0, mesh.FaceCount);
        Assert.Equal(0, mesh.FaceCornerCount);
        Assert.False(mesh.HasVertexColors);

        // The offset sentinel has to survive, or the next AddFace would produce a broken slice.
        mesh.AddVertices([Point3d.Origin, PointOps.Create(1, 0, 0), PointOps.Create(0, 1, 0)]);
        mesh.AddFace(0, 1, 2);
        Assert.Equal([0, 1, 2], mesh.Face(0).ToArray());
    }

    [Fact]
    public void Attributes_AreAbsentUntilSet()
    {
        Mesh mesh = MixedFaces();

        Assert.False(mesh.HasNormals);
        Assert.False(mesh.HasTextureCoordinates);
        Assert.False(mesh.HasVertexColors);
        Assert.False(mesh.HasFaceGroups);

        Assert.True(mesh.Normals.IsEmpty);
        Assert.True(mesh.TextureCoordinates.IsEmpty);
        Assert.True(mesh.VertexColors.IsEmpty);
        Assert.True(mesh.FaceGroups.IsEmpty);
    }

    [Fact]
    public void SetNormals_StoresOnePerVertex()
    {
        Mesh mesh = MixedFaces();
        Vector3f[] normals = Enumerable.Repeat(Vector3f.UnitZ, mesh.VertexCount).ToArray();

        mesh.SetNormals(normals);

        Assert.True(mesh.HasNormals);
        Assert.Equal(mesh.VertexCount, mesh.Normals.Length);
        Assert.Equal(Vector3f.UnitZ, mesh.Normals[0]);
    }

    [Fact]
    public void SetPerVertexAttributes_RejectAMismatchedCount()
    {
        Mesh mesh = MixedFaces();

        Assert.Throws<ArgumentException>(() => mesh.SetNormals(new Vector3f[2]));
        Assert.Throws<ArgumentException>(() => mesh.SetTextureCoordinates(new Vector2f[99]));
        Assert.Throws<ArgumentException>(() => mesh.SetVertexColors(new Color32[0]));
    }

    [Fact]
    public void SetFaceGroups_RequiresOnePerFace()
    {
        Mesh mesh = MixedFaces();

        mesh.SetFaceGroups([7, 7, 9]);

        Assert.True(mesh.HasFaceGroups);
        Assert.Equal([7, 7, 9], mesh.FaceGroups.ToArray());
        Assert.Throws<ArgumentException>(() => mesh.SetFaceGroups([1, 2]));
    }

    [Fact]
    public void ClearingAnAttribute_LeavesTheOthersAlone()
    {
        Mesh mesh = MixedFaces();
        mesh.SetNormals(new Vector3f[mesh.VertexCount]);
        mesh.SetVertexColors(new Color32[mesh.VertexCount]);

        mesh.ClearNormals();

        Assert.False(mesh.HasNormals);
        Assert.True(mesh.HasVertexColors);
    }

    [Fact]
    public void ToString_ReportsTheCounts()
    {
        Assert.Equal("Mesh(V 6; F 3)", MixedFaces().ToString());
    }
}
