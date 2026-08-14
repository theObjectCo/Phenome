using Phenome.Geometry;

namespace Phenome.Geometry.Tests;

public class MeshBuildersTests
{
    [Fact]
    public void Box_HasEightVerticesAndSixQuadFaces()
    {
        Mesh box = MeshBuilders.CreateBox(2, 3, 4);

        Assert.Equal(8, box.VertexCount);
        Assert.Equal(6, box.FaceCount);
        Assert.Equal(24, box.FaceCornerCount);

        for (int i = 0; i < box.FaceCount; i++)
        {
            // Six quads, not twelve triangles: a flat side stays one face.
            Assert.Equal(4, box.CornersInFace(i));
        }

        Assert.True(MeshOps.IsValid(box));
    }

    [Fact]
    public void Box_SitsInThePositiveOctantWithTheGivenExtents()
    {
        Mesh box = MeshBuilders.CreateBox(2, 3, 4);

        Assert.Equal(Point3d.Origin, box.Vertices[0]);
        Assert.Equal(PointOps.Create(2, 3, 4), box.Vertices[6]);

        foreach (Point3d vertex in box.Vertices)
        {
            Assert.InRange(vertex.X, 0, 2);
            Assert.InRange(vertex.Y, 0, 3);
            Assert.InRange(vertex.Z, 0, 4);
        }
    }

    [Fact]
    public void Box_HasEveryFaceWoundSoItsNormalPointsOutwards()
    {
        Mesh box = MeshBuilders.CreateBox(2, 3, 4);
        Point3d centre = PointOps.Create(1, 1.5, 2);

        for (int i = 0; i < box.FaceCount; i++)
        {
            Assert.True(MeshOps.TryFaceNormal(box, i, out Vector3d? normal));

            // A normal points outwards when it agrees with the direction from the box centre to the face.
            Vector3d outward = MeshOps.FaceCenter(box, i) - centre;

            Assert.True(VectorOps.Dot(normal!.Value, outward) > 0);
        }
    }

    [Fact]
    public void Box_HasTheSixExpectedAxisAlignedNormals()
    {
        Mesh box = MeshBuilders.CreateBox(1, 1, 1);
        Vector3d[] normals = MeshOps.FaceNormals(box);

        Vector3d[] expected =
        [
            -Vector3d.ZAxis, Vector3d.ZAxis,
            -Vector3d.YAxis, Vector3d.XAxis,
            Vector3d.YAxis, -Vector3d.XAxis,
        ];

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(VectorOps.EpsilonEquals(normals[i], expected[i], 1e-12));
        }
    }

    [Fact]
    public void Box_TotalSurfaceAreaMatchesTheFormula()
    {
        Mesh box = MeshBuilders.CreateBox(2, 3, 4);
        double area = 0;

        for (int i = 0; i < box.FaceCount; i++)
        {
            area += MeshOps.FaceArea(box, i);
        }

        Assert.Equal(2 * ((2 * 3) + (3 * 4) + (2 * 4)), area, 1e-12);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 1, 0)]
    public void Box_RejectsNonPositiveExtents(double width, double depth, double height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshBuilders.CreateBox(width, depth, height));
    }

    [Fact]
    public void Grid_HasOneMoreVertexThanCellInEachDirection()
    {
        Mesh grid = MeshBuilders.CreateGrid(3, 2, 1, 1);

        Assert.Equal(4 * 3, grid.VertexCount);
        Assert.Equal(3 * 2, grid.FaceCount);
        Assert.True(MeshOps.IsValid(grid));
    }

    [Fact]
    public void Grid_LiesFlatWithEveryNormalPointingUp()
    {
        Mesh grid = MeshBuilders.CreateGrid(4, 4, 0.5, 0.25);

        foreach (Vector3d normal in MeshOps.FaceNormals(grid))
        {
            Assert.True(VectorOps.EpsilonEquals(normal, Vector3d.ZAxis, 1e-12));
        }

        Assert.Equal(PointOps.Create(2, 1, 0), grid.Vertices[^1]);
    }

    [Fact]
    public void Grid_CellsTileTheWholeAreaWithoutGaps()
    {
        Mesh grid = MeshBuilders.CreateGrid(5, 7, 2, 3);
        double area = 0;

        for (int i = 0; i < grid.FaceCount; i++)
        {
            area += MeshOps.FaceArea(grid, i);
        }

        Assert.Equal(5 * 2 * 7 * 3, area, 1e-9);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, -3)]
    public void Grid_RejectsNonPositiveCounts(int columns, int rows)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshBuilders.CreateGrid(columns, rows, 1, 1));
    }

    [Fact]
    public void Grid_HandlesALargeMeshInOneFlatBuffer()
    {
        // A hundred thousand quads, to confirm the compressed layout holds up and that the counts line up
        // exactly: one corner buffer, one offset buffer, no per-face object.
        Mesh grid = MeshBuilders.CreateGrid(400, 250, 1, 1);

        Assert.Equal(401 * 251, grid.VertexCount);
        Assert.Equal(100_000, grid.FaceCount);
        Assert.Equal(400_000, grid.FaceCornerCount);
        Assert.Equal([0, 1, 402, 401], grid.Face(0).ToArray());
    }
}
