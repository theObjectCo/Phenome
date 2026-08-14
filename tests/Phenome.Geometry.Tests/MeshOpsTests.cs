using Phenome.Geometry;
using Vector3f = System.Numerics.Vector3;

namespace Phenome.Geometry.Tests;

public class MeshOpsTests
{
    /// <summary>A unit square in the XY plane, wound counter-clockwise, as one quad.</summary>
    private static Mesh UnitQuad() => MeshOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(1, 1, 0),
            PointOps.Create(0, 1, 0),
        ],
        [[0, 1, 2, 3]]);

    /// <summary>Four separate triangles, so faces can be removed without orphaning shared corners.</summary>
    private static Mesh FourTriangles()
    {
        Mesh mesh = MeshOps.Create();

        for (int i = 0; i < 4; i++)
        {
            int start = mesh.AddVertices(
            [
                PointOps.Create(i, 0, 0),
                PointOps.Create(i + 1, 0, 0),
                PointOps.Create(i, 1, 0),
            ]);

            mesh.AddFace(start, start + 1, start + 2);
        }

        return mesh;
    }

    [Fact]
    public void Create_FromVerticesAndFacesBuildsTheMesh()
    {
        Mesh mesh = UnitQuad();

        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(1, mesh.FaceCount);
        Assert.Equal([0, 1, 2, 3], mesh.Face(0).ToArray());
    }

    [Fact]
    public void IsValid_AcceptsAWellFormedMeshAndRejectsNaN()
    {
        Assert.True(MeshOps.IsValid(UnitQuad()));

        Mesh broken = UnitQuad();
        broken.VerticesForWriting()[2] = Point3d.Unset;

        Assert.False(MeshOps.IsValid(broken));
    }

    [Fact]
    public void Duplicate_CopiesGeometryAndAttributesIndependently()
    {
        Mesh original = UnitQuad();
        original.SetVertexColors(Enumerable.Repeat(Color32.White, 4).ToArray());
        original.SetFaceGroups([3]);

        Mesh copy = MeshOps.Duplicate(original);
        copy.VerticesForWriting()[0] = PointOps.Create(99, 99, 99);

        Assert.Equal(original.VertexCount, copy.VertexCount);
        Assert.Equal(original.FaceCount, copy.FaceCount);
        Assert.True(copy.HasVertexColors);
        Assert.Equal([3], copy.FaceGroups.ToArray());

        // Writing into the copy must not reach back into the original.
        Assert.Equal(Point3d.Origin, original.Vertices[0]);
    }

    [Fact]
    public void Transform_MovesTheVerticesInPlace()
    {
        Mesh mesh = UnitQuad();

        MeshOps.Transform(mesh, Transforms.Translate(0, 0, 5));

        Assert.Equal(PointOps.Create(0, 0, 5), mesh.Vertices[0]);
        Assert.Equal(PointOps.Create(1, 1, 5), mesh.Vertices[2]);
    }

    [Fact]
    public void Transform_LeavesADuplicateUntouched()
    {
        Mesh original = UnitQuad();
        Mesh moved = MeshOps.Duplicate(original);

        MeshOps.Transform(moved, Transforms.Scale(10));

        Assert.Equal(PointOps.Create(1, 1, 0), original.Vertices[2]);
        Assert.Equal(PointOps.Create(10, 10, 0), moved.Vertices[2]);
    }

    [Fact]
    public void FaceAreaVector_PointsAlongTheNormalWithTwiceTheArea()
    {
        // Newell's method: magnitude is twice the area, which is the weight a vertex normal wants.
        Vector3d areaVector = MeshOps.FaceAreaVector(UnitQuad(), 0);

        Assert.Equal(2.0, VectorOps.Length(areaVector), 1e-12);
        Assert.True(VectorOps.EpsilonEquals(
            VectorOps.Normalized(areaVector), Vector3d.ZAxis, 1e-12));
    }

    [Fact]
    public void FaceArea_MatchesTheObviousAnswer()
    {
        Assert.Equal(1.0, MeshOps.FaceArea(UnitQuad(), 0), 1e-12);
    }

    [Fact]
    public void FaceAreaVector_HandlesNGonsOfAnyCornerCount()
    {
        // A regular hexagon of circumradius 1 has area 3*sqrt(3)/2. Two edges crossed would not give this;
        // Newell sums over every edge, so corner count does not matter.
        Point3d[] hexagon = new Point3d[6];

        for (int i = 0; i < 6; i++)
        {
            double angle = i * Math.PI / 3.0;
            hexagon[i] = PointOps.Create(Math.Cos(angle), Math.Sin(angle), 0);
        }

        Mesh mesh = MeshOps.Create(hexagon, [[0, 1, 2, 3, 4, 5]]);

        Assert.Equal(3.0 * Math.Sqrt(3.0) / 2.0, MeshOps.FaceArea(mesh, 0), 1e-12);
    }

    [Fact]
    public void FaceNormal_ReversesWithTheWinding()
    {
        Mesh clockwise = MeshOps.Create(
            [
                PointOps.Create(0, 0, 0),
                PointOps.Create(0, 1, 0),
                PointOps.Create(1, 1, 0),
                PointOps.Create(1, 0, 0),
            ],
            [[0, 1, 2, 3]]);

        Assert.True(MeshOps.TryFaceNormal(UnitQuad(), 0, out Vector3d? up));
        Assert.True(MeshOps.TryFaceNormal(clockwise, 0, out Vector3d? down));

        Assert.True(VectorOps.EpsilonEquals(up!.Value, Vector3d.ZAxis, 1e-12));
        Assert.True(VectorOps.EpsilonEquals(down!.Value, -Vector3d.ZAxis, 1e-12));
    }

    [Fact]
    public void TryFaceNormal_FailsForACollapsedFace()
    {
        Mesh degenerate = MeshOps.Create(
            [
                PointOps.Create(0, 0, 0),
                PointOps.Create(1, 0, 0),
                PointOps.Create(2, 0, 0),
            ],
            [[0, 1, 2]]);

        Assert.False(MeshOps.TryFaceNormal(degenerate, 0, out Vector3d? normal));
        Assert.Null(normal);
    }

    [Fact]
    public void FaceNormals_MarksDegenerateFacesUnsetRatherThanGuessing()
    {
        Mesh mesh = MeshOps.Create(
            [
                PointOps.Create(0, 0, 0),
                PointOps.Create(1, 0, 0),
                PointOps.Create(1, 1, 0),
                PointOps.Create(2, 0, 0),
            ],
            [[0, 1, 2], [0, 1, 3]]);

        Vector3d[] normals = MeshOps.FaceNormals(mesh);

        Assert.True(VectorOps.IsValid(normals[0]));
        Assert.False(VectorOps.IsValid(normals[1]));
    }

    [Fact]
    public void FaceCenter_AveragesTheCorners()
    {
        Assert.Equal(PointOps.Create(0.5, 0.5, 0), MeshOps.FaceCenter(UnitQuad(), 0));
        Assert.Equal(
            [PointOps.Create(0.5, 0.5, 0)],
            MeshOps.FaceCenters(UnitQuad()));
    }

    [Fact]
    public void ComputeVertexNormals_StoresAUnitNormalPerVertex()
    {
        Mesh mesh = UnitQuad();

        OperationResult result = MeshOps.ComputeVertexNormals(mesh);

        Assert.True(result.IsSuccess);
        Assert.True(mesh.HasNormals);
        Assert.Equal(4, mesh.Normals.Length);

        foreach (Vector3f normal in mesh.Normals)
        {
            Assert.Equal(Vector3f.UnitZ, normal);
        }
    }

    [Fact]
    public void ComputeVertexNormals_ReportsPartialWhenAVertexHasNoFace()
    {
        Mesh mesh = UnitQuad();
        mesh.AddVertex(PointOps.Create(5, 5, 5));

        OperationResult result = MeshOps.ComputeVertexNormals(mesh);

        // Deliberately not silent: an orphan vertex means the mesh has a problem worth seeing.
        Assert.True(result.IsPartial);
        Assert.True(result.HasOutput);
        Assert.NotNull(result.Message);
        Assert.Contains("1 of 5", result.Message);
        Assert.Equal(Vector3f.Zero, mesh.Normals[4]);
    }

    [Fact]
    public void ComputeVertexNormals_WeightsByFaceArea()
    {
        // Two triangles meeting at a vertex, one ten times the other and facing a different way. The bigger
        // face should dominate, which is what makes smooth shading look right on an irregular mesh.
        Mesh mesh = MeshOps.Create(
            [
                PointOps.Create(0, 0, 0),
                PointOps.Create(1, 0, 0),
                PointOps.Create(0, 1, 0),
                PointOps.Create(0, 0, 10),
                PointOps.Create(0, 10, 0),
            ],
            [[0, 1, 2], [0, 3, 4]]);

        MeshOps.ComputeVertexNormals(mesh);

        // Face 1 lies in the YZ plane and is far larger, so the shared vertex leans towards its normal.
        Assert.True(Math.Abs(mesh.Normals[0].X) > Math.Abs(mesh.Normals[0].Z));
    }

    [Fact]
    public void Join_OffsetsTheCornerIndicesOfEveryMeshAfterTheFirst()
    {
        Mesh moved = UnitQuad();
        MeshOps.Transform(moved, Transforms.Translate(10, 0, 0));

        Assert.True(MeshOps.Join([UnitQuad(), moved], out Mesh joined).IsSuccess);

        Assert.Equal(8, joined.VertexCount);
        Assert.Equal(2, joined.FaceCount);
        Assert.Equal([4, 5, 6, 7], joined.Face(1).ToArray());
        Assert.Equal(PointOps.Create(10, 0, 0), joined.Vertices[4]);
    }

    [Fact]
    public void Join_MergesEveryInputMesh()
    {
        Assert.True(MeshOps.Join([UnitQuad(), UnitQuad(), UnitQuad()], out Mesh joined).IsSuccess);

        Assert.Equal(12, joined.VertexCount);
        Assert.Equal(3, joined.FaceCount);
        Assert.Equal([8, 9, 10, 11], joined.Face(2).ToArray());
    }

    [Fact]
    public void Join_OfNothingIsAnEmptyMesh()
    {
        Assert.True(MeshOps.Join([], out Mesh joined).IsSuccess);

        Assert.Equal(0, joined.VertexCount);
        Assert.Equal(0, joined.FaceCount);
        Assert.False(joined.HasNormals);
    }

    [Fact]
    public void Join_KeepsAnAttributeEveryMeshCarries()
    {
        // The defect this replaced Append over. Joining accumulated pairwise into an empty mesh, which has no
        // attributes of its own, so the first input's were compared against nothing and dropped -- and the
        // report only named what the target had lost, so an empty target reported success. Every join
        // silently discarded every attribute.
        Mesh first = UnitQuad();
        Mesh second = UnitQuad();

        first.SetVertexColors([.. Enumerable.Repeat(Color32.White, 4)]);
        second.SetVertexColors([.. Enumerable.Repeat(Color32.Black, 4)]);

        Assert.True(MeshOps.Join([first, second], out Mesh joined).IsSuccess);

        Assert.True(joined.HasVertexColors);
        Assert.Equal(8, joined.VertexColors.Length);
        Assert.Equal(Color32.White, joined.VertexColors[0]);
        Assert.Equal(Color32.Black, joined.VertexColors[4]);
    }

    [Fact]
    public void Join_KeepsNormalsAndFaceGroupsTheirOwnLengths()
    {
        // Face groups are one per face; the other three are one per vertex. Offsetting them all by the vertex
        // count would put the groups of everything after the first mesh in the wrong place.
        Mesh first = UnitQuad();
        Mesh second = UnitQuad();

        Assert.True(MeshOps.ComputeVertexNormals(first).IsSuccess);
        Assert.True(MeshOps.ComputeVertexNormals(second).IsSuccess);

        first.SetFaceGroups([7]);
        second.SetFaceGroups([9]);

        Assert.True(MeshOps.Join([first, second], out Mesh joined).IsSuccess);

        Assert.Equal(8, joined.Normals.Length);
        Assert.Equal([7, 9], joined.FaceGroups.ToArray());
    }

    [Fact]
    public void Join_DropsAnAttributeOnlySomeMeshesCarryAndSaysSo()
    {
        // A parallel list with holes in it is worse than no list, so the attribute goes -- but not quietly.
        Mesh coloured = UnitQuad();
        coloured.SetVertexColors([.. Enumerable.Repeat(Color32.White, 4)]);

        OperationResult result = MeshOps.Join([coloured, UnitQuad()], out Mesh joined);

        Assert.True(result.IsPartial);
        Assert.False(joined.HasVertexColors);
        Assert.NotNull(result.Message);
        Assert.Contains("vertex colours", result.Message);
    }

    [Fact]
    public void RemoveFaces_CompactsTheCornerBufferInOnePass()
    {
        Mesh mesh = FourTriangles();

        int removed = MeshOps.RemoveFaces(mesh, [1, 2]);

        Assert.Equal(2, removed);
        Assert.Equal(2, mesh.FaceCount);
        Assert.Equal(6, mesh.FaceCornerCount);

        // The survivors keep their corners, and their vertices are left in place.
        Assert.Equal([0, 1, 2], mesh.Face(0).ToArray());
        Assert.Equal([9, 10, 11], mesh.Face(1).ToArray());
        Assert.Equal(12, mesh.VertexCount);
    }

    [Fact]
    public void RemoveFaces_IgnoresRepeatedAndOutOfRangeIndices()
    {
        // So a caller can hand over the output of a filter without deduplicating it first.
        Mesh mesh = FourTriangles();

        int removed = MeshOps.RemoveFaces(mesh, [1, 1, 1, -5, 99]);

        Assert.Equal(1, removed);
        Assert.Equal(3, mesh.FaceCount);
    }

    [Fact]
    public void RemoveFaces_RemovingNothingChangesNothing()
    {
        Mesh mesh = FourTriangles();

        Assert.Equal(0, MeshOps.RemoveFaces(mesh, []));
        Assert.Equal(0, MeshOps.RemoveFaces(mesh, [42]));
        Assert.Equal(4, mesh.FaceCount);
        Assert.Equal(12, mesh.FaceCornerCount);
    }

    [Fact]
    public void RemoveFaces_KeepsFaceGroupsAlignedWithTheSurvivors()
    {
        Mesh mesh = FourTriangles();
        mesh.SetFaceGroups([10, 20, 30, 40]);

        MeshOps.RemoveFaces(mesh, [0, 2]);

        Assert.Equal([20, 40], mesh.FaceGroups.ToArray());
    }

    [Fact]
    public void RemoveFaces_LeavesAUsableMeshBehind()
    {
        Mesh mesh = FourTriangles();
        MeshOps.RemoveFaces(mesh, [0, 1, 2]);

        Assert.True(MeshOps.IsValid(mesh));
        Assert.Equal(1, mesh.FaceCount);
        Assert.Equal(1.0, MeshOps.FaceArea(mesh, 0) * 2.0, 1e-12);
    }

    [Fact]
    public void RemoveFaces_CanEmptyTheMeshOfFaces()
    {
        Mesh mesh = FourTriangles();

        Assert.Equal(4, MeshOps.RemoveFaces(mesh, [0, 1, 2, 3]));
        Assert.Equal(0, mesh.FaceCount);
        Assert.Equal(0, mesh.FaceCornerCount);
        Assert.Equal(12, mesh.VertexCount);
    }
}
