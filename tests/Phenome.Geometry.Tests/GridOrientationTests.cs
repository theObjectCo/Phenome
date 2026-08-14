namespace Phenome.Geometry.Tests;

/// <summary>
/// Checks which way a grid faces, and that computing its vertex normals agrees.
/// </summary>
/// <remarks>
/// Written because the browser playground drew a grid lit from underneath. A surface shaded from the wrong side
/// is the visible symptom of two separate things that can be wrong — the winding of the faces, or the sign of
/// the normals averaged from them — and they are worth separating here rather than in a renderer.
/// </remarks>
public class GridOrientationTests
{
    [Fact]
    public void EveryFaceOfAGridIsWoundSoItsNormalPointsUp()
    {
        Mesh grid = MeshBuilders.CreateGrid(3, 4, 1, 1);

        for (int face = 0; face < grid.FaceCount; face++)
        {
            Assert.True(MeshOps.TryFaceNormal(grid, face, out Vector3d? normal));
            Assert.True(
                normal!.Value.Z > 0,
                $"Face {face} has normal {normal.Value}, which does not point up.");
        }
    }

    [Fact]
    public void TheComputedVertexNormalsOfAFlatGridPointTheSameWayItsFacesDo()
    {
        Mesh grid = MeshBuilders.CreateGrid(3, 4, 1, 1);

        Assert.True(MeshOps.ComputeVertexNormals(grid).IsSuccess);
        Assert.True(grid.HasNormals);

        for (int vertex = 0; vertex < grid.VertexCount; vertex++)
        {
            System.Numerics.Vector3 normal = grid.Normals[vertex];

            Assert.True(
                normal.Z > 0,
                $"Vertex {vertex} has normal {normal}, which does not point up.");
        }
    }

    [Fact]
    public void ABoxHasEveryVertexNormalPointingAwayFromItsCentre()
    {
        // The same question for a closed mesh, where averaging has more than one face to combine.
        Mesh box = MeshBuilders.CreateBox(2, 2, 2);

        Assert.True(MeshOps.ComputeVertexNormals(box).IsSuccess);

        Point3d centre = BoundingBoxOps.Center(BoundingBoxOps.Bound(box));

        for (int vertex = 0; vertex < box.VertexCount; vertex++)
        {
            System.Numerics.Vector3 stored = box.Normals[vertex];
            Vector3d normal = VectorOps.Create(stored.X, stored.Y, stored.Z);
            Vector3d outward = box.Vertices[vertex] - centre;

            Assert.True(
                VectorOps.Dot(normal, outward) > 0,
                $"Vertex {vertex} has normal {normal}, which points back towards the centre.");
        }
    }
}
