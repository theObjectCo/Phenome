using System.Runtime.InteropServices;
using Phenome.Geometry;
using Vector2f = System.Numerics.Vector2;
using Vector3f = System.Numerics.Vector3;

namespace Phenome.Geometry.Tests;

public class RenderBuffersTests
{
    private static Mesh UnitQuad() => MeshOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(1, 1, 0),
            PointOps.Create(0, 1, 0),
        ],
        [[0, 1, 2, 3]]);

    [Fact]
    public void TriangleCount_IsTwoLessThanTheCornerCountPerFace()
    {
        // Triangle -> 1, quad -> 2, hexagon -> 4.
        Mesh mesh = MeshOps.Create(
            [
                PointOps.Create(0, 0, 0),
                PointOps.Create(1, 0, 0),
                PointOps.Create(2, 0, 0),
                PointOps.Create(2, 1, 0),
                PointOps.Create(1, 1, 0),
                PointOps.Create(0, 1, 0),
            ],
            [[0, 1, 5], [1, 2, 3, 4], [0, 1, 2, 3, 4, 5]]);

        Assert.Equal(1 + 2 + 4, RenderBuffers.TriangleCount(mesh));
        Assert.Equal((1 + 2 + 4) * 3, RenderBuffers.TriangleIndexCount(mesh));
    }

    [Fact]
    public void CreatePositions_WritesThreeFloatsPerVertex()
    {
        float[] positions = RenderBuffers.CreatePositions(MeshBuilders.CreateBox(2, 3, 4));

        Assert.Equal(8 * 3, positions.Length);
        Assert.Equal(0f, positions[0]);
        Assert.Equal(2f, positions[3]);
    }

    [Fact]
    public void WritePositions_SubtractsALocalOriginToKeepTheNumbersSmall()
    {
        // At 500 metres a single-precision value steps in units of 2^-5 mm, about 31 microns, so a detail
        // finer than that vanishes entirely when the coordinate is written absolutely. Measuring from a
        // nearby origin keeps the same detail exact.
        Mesh mesh = MeshOps.Create([PointOps.Create(500_000.001, 0, 0)], []);

        float[] absolute = new float[3];
        float[] relative = new float[3];

        RenderBuffers.WritePositions(mesh, absolute);
        RenderBuffers.WritePositions(mesh, PointOps.Create(500_000, 0, 0), relative);

        Assert.Equal(0.001f, relative[0], 1e-9f);
        Assert.Equal(0f, absolute[0] - 500_000f);
    }

    [Fact]
    public void WritePositions_RejectsATooSmallBuffer()
    {
        Mesh box = MeshBuilders.CreateBox(1, 1, 1);

        Assert.Throws<ArgumentException>(() => RenderBuffers.WritePositions(box, new float[23]));
    }

    [Fact]
    public void CreateTriangleIndices_SplitsAQuadAcrossADiagonal()
    {
        Assert.True(RenderBuffers.CreateTriangleIndices(UnitQuad(), out int[]? indices).IsSuccess);

        // A unit square's diagonals are the same length, so the tie goes to the first.
        Assert.Equal([0, 1, 2, 0, 2, 3], indices!);
    }

    [Fact]
    public void CreateTriangleIndices_PreservesWindingSoNormalsSurvive()
    {
        Mesh quad = UnitQuad();
        Assert.True(MeshOps.TryFaceNormal(quad, 0, out Vector3d? faceNormal));

        Assert.True(RenderBuffers.CreateTriangleIndices(quad, out int[]? indices).IsSuccess);
        ReadOnlySpan<Point3d> vertices = quad.Vertices;

        for (int i = 0; i < indices!.Length; i += 3)
        {
            Vector3d edgeA = vertices[indices[i + 1]] - vertices[indices[i]];
            Vector3d edgeB = vertices[indices[i + 2]] - vertices[indices[i + 1]];
            Vector3d triangleNormal = VectorOps.Normalized(VectorOps.Cross(edgeA, edgeB));

            Assert.True(VectorOps.EpsilonEquals(triangleNormal, faceNormal!.Value, 1e-12));
        }
    }

    [Fact]
    public void CreateTriangleIndices_CoversABoxCompletely()
    {
        Mesh box = MeshBuilders.CreateBox(1, 1, 1);
        Assert.True(RenderBuffers.CreateTriangleIndices(box, out int[]? indices).IsSuccess);

        Assert.Equal(12 * 3, indices!.Length);

        foreach (int index in indices)
        {
            Assert.InRange(index, 0, box.VertexCount - 1);
        }
    }

    [Fact]
    public void WriteTriangleIndices_RejectsATooSmallBuffer()
    {
        Mesh box = MeshBuilders.CreateBox(1, 1, 1);

        Assert.Throws<ArgumentException>(() => RenderBuffers.WriteTriangleIndices(box, new int[35]));
    }

    [Fact]
    public void NormalBytes_AreAViewOntoTheMeshWithNoConversion()
    {
        // The payoff for storing display attributes at single precision: the upload is a memory copy, not
        // a per-element loop.
        Mesh box = MeshBuilders.CreateBox(1, 1, 1);
        MeshOps.ComputeVertexNormals(box);

        ReadOnlySpan<byte> bytes = RenderBuffers.NormalBytes(box);

        Assert.Equal(box.VertexCount * 3 * sizeof(float), bytes.Length);
        Assert.True(bytes.SequenceEqual(MemoryMarshal.AsBytes(box.Normals)));
    }

    [Fact]
    public void AttributeBytes_AreEmptyWhenTheMeshHasNoSuchAttribute()
    {
        Mesh box = MeshBuilders.CreateBox(1, 1, 1);

        Assert.True(RenderBuffers.NormalBytes(box).IsEmpty);
        Assert.True(RenderBuffers.TextureCoordinateBytes(box).IsEmpty);
        Assert.True(RenderBuffers.VertexColorBytes(box).IsEmpty);
    }

    [Fact]
    public void TextureCoordinateBytes_AreTwoFloatsPerVertex()
    {
        Mesh box = MeshBuilders.CreateBox(1, 1, 1);
        box.SetTextureCoordinates(Enumerable.Repeat(new Vector2f(0.25f, 0.5f), 8).ToArray());

        Assert.Equal(8 * 2 * sizeof(float), RenderBuffers.TextureCoordinateBytes(box).Length);
    }

    [Fact]
    public void VertexColorBytes_AreFourBytesPerVertexInRgbaOrder()
    {
        Mesh box = MeshBuilders.CreateBox(1, 1, 1);
        Color32 orange = ColorOps.Create(255, 128, 0, 255);
        box.SetVertexColors(Enumerable.Repeat(orange, 8).ToArray());

        ReadOnlySpan<byte> bytes = RenderBuffers.VertexColorBytes(box);

        Assert.Equal(8 * 4, bytes.Length);
        Assert.Equal(255, bytes[0]);
        Assert.Equal(128, bytes[1]);
        Assert.Equal(0, bytes[2]);
        Assert.Equal(255, bytes[3]);
    }

    [Fact]
    public void ABoxSurvivesTheWholeRoundTripToBuffers()
    {
        // The vertical slice end to end: build, compute normals, colour, then produce every buffer a
        // renderer needs and check the sizes agree with each other.
        Mesh box = MeshBuilders.CreateBox(10, 20, 30);
        MeshOps.ComputeVertexNormals(box);
        box.SetVertexColors(Enumerable.Repeat(Color32.White, box.VertexCount).ToArray());

        float[] positions = RenderBuffers.CreatePositions(box);
        Assert.True(RenderBuffers.CreateTriangleIndices(box, out int[]? indices).IsSuccess);

        Assert.Equal(box.VertexCount * 3, positions.Length);
        Assert.Equal(RenderBuffers.TriangleCount(box) * 3, indices!.Length);
        Assert.Equal(positions.Length * sizeof(float), RenderBuffers.NormalBytes(box).Length);
        Assert.Equal(box.VertexCount * 4, RenderBuffers.VertexColorBytes(box).Length);

        foreach (Vector3f normal in box.Normals)
        {
            Assert.Equal(1.0f, normal.Length(), 1e-6f);
        }
    }
}
