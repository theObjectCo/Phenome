using Vector2f = System.Numerics.Vector2;
using Vector3f = System.Numerics.Vector3;

namespace Phenome.Geometry.Tests;

/// <summary>
/// Covers the shading side: splitting vertices at creases, and mirroring parts into place.
/// </summary>
public class MeshShadingTests
{
    private const double FortyDegrees = 0.7;

    [Fact]
    public void SplitAtCreases_GivesABoxThreeVerticesPerCornerSoItsEdgesStaySharp()
    {
        // The whole point: eight welded vertices cannot express twelve hard edges, because a vertex carries
        // one normal. Averaging the three faces at a corner is what makes a box render like a cushion.
        Mesh box = MeshBuilders.CreateBox(1, 1, 1);

        OperationResult result = MeshOps.SplitAtCreases(box, FortyDegrees, out Mesh? split);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(24, split!.VertexCount);
        Assert.Equal(6, split.FaceCount);

        // Every normal is now an axis direction, not a corner-averaged diagonal.
        foreach (Vector3f normal in split.Normals)
        {
            double largest = Math.Max(Math.Abs(normal.X), Math.Max(Math.Abs(normal.Y), Math.Abs(normal.Z)));
            Assert.Equal(1.0, largest, 5);
        }
    }

    [Fact]
    public void SplitAtCreases_MatchesTheFaceNormalOnEveryCornerOfABox()
    {
        Mesh box = MeshBuilders.CreateBox(2, 3, 4);
        Assert.True(MeshOps.SplitAtCreases(box, FortyDegrees, out Mesh? split).IsSuccess);

        for (int face = 0; face < split!.FaceCount; face++)
        {
            Assert.True(MeshOps.TryFaceNormal(split, face, out Vector3d? faceNormal));

            foreach (int corner in split.Face(face))
            {
                Vector3f vertexNormal = split.Normals[corner];

                Assert.True(VectorOps.EpsilonEquals(
                    VectorOps.Create(vertexNormal.X, vertexNormal.Y, vertexNormal.Z),
                    faceNormal!.Value,
                    1e-6));
            }
        }
    }

    [Fact]
    public void SplitAtCreases_KeepsACylinderSideSmoothAllTheWayRoundAndBreaksOffItsCaps()
    {
        // The case that says the grouping is transitive rather than pairwise: the first and last side faces
        // point opposite ways, yet the run between them must stay one smooth group.
        Polyline profile = PolylineOps.Create(
            [PointOps.Create(2, 0, 0), PointOps.Create(2, 0, 5)]);

        Assert.True(MeshBuilders.CreateRevolution(
            profile,
            LineOps.Create(Point3d.Origin, PointOps.Create(0, 0, 1)),
            Interval.FullTurn,
            segments: 32,
            capped: true,
            out Mesh? cylinder).IsSuccess);

        Assert.True(MeshOps.SplitAtCreases(cylinder!, FortyDegrees, out Mesh? split).IsSuccess);

        // Each of the 64 side vertices stays shared between its two side faces, and each end ring gains one
        // copy for the cap: 64 for the sides plus 64 for the two caps.
        Assert.Equal(64 + 64, split!.VertexCount);

        // Every side normal is horizontal; every cap normal is vertical. Nothing in between, which is what a
        // welded average would have produced at the rim.
        int horizontal = 0;
        int vertical = 0;

        foreach (Vector3f normal in split.Normals)
        {
            if (Math.Abs(normal.Z) < 1e-5)
            {
                horizontal++;
            }
            else if (Math.Abs(Math.Abs(normal.Z) - 1) < 1e-5)
            {
                vertical++;
            }
        }

        Assert.Equal(64, horizontal);
        Assert.Equal(64, vertical);
    }

    [Fact]
    public void SplitAtCreases_WithPiKeepsEverythingWeldedLikeTheAveragingVersion()
    {
        Mesh box = MeshBuilders.CreateBox(1, 1, 1);

        Assert.True(MeshOps.SplitAtCreases(box, Math.PI, out Mesh? welded).IsSuccess);
        Assert.Equal(8, welded!.VertexCount);

        Mesh reference = MeshBuilders.CreateBox(1, 1, 1);
        Assert.True(MeshOps.ComputeVertexNormals(reference).IsSuccess);

        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(reference.Normals[i].X, welded.Normals[i].X, 5);
            Assert.Equal(reference.Normals[i].Y, welded.Normals[i].Y, 5);
            Assert.Equal(reference.Normals[i].Z, welded.Normals[i].Z, 5);
        }
    }

    [Fact]
    public void SplitAtCreases_WithZeroSplitsEveryFaceApartForFlatShading()
    {
        Mesh box = MeshBuilders.CreateBox(1, 1, 1);

        Assert.True(MeshOps.SplitAtCreases(box, 0, out Mesh? split).IsSuccess);

        // Every face gets its own four corners and shares none.
        Assert.Equal(6 * 4, split!.VertexCount);
    }

    [Fact]
    public void SplitAtCreases_TreatsAFaceGroupBoundaryAsHardWhateverTheAngle()
    {
        // Two coplanar quads: nothing about the geometry says split, but different materials do, and a
        // material boundary needs its own normals and its own vertices for the draw call anyway.
        Mesh strip = MeshOps.Create();
        strip.AddVertices(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(2, 0, 0),
            PointOps.Create(0, 1, 0),
            PointOps.Create(1, 1, 0),
            PointOps.Create(2, 1, 0),
        ]);
        strip.AddFace(0, 1, 4, 3);
        strip.AddFace(1, 2, 5, 4);

        Assert.True(MeshOps.SplitAtCreases(strip, Math.PI, out Mesh? welded).IsSuccess);
        Assert.Equal(6, welded!.VertexCount);

        strip.SetFaceGroups([7, 9]);

        Assert.True(MeshOps.SplitAtCreases(strip, Math.PI, out Mesh? split).IsSuccess);

        // The two vertices along the shared edge each become two.
        Assert.Equal(8, split!.VertexCount);
        Assert.Equal([7, 9], split.FaceGroups.ToArray());
    }

    [Fact]
    public void SplitAtCreases_CarriesTextureCoordinatesAndColoursOntoTheCopies()
    {
        Mesh box = MeshBuilders.CreateBox(1, 1, 1);
        box.SetVertexColors(Enumerable.Repeat(Color32.White, box.VertexCount).ToArray());
        box.SetTextureCoordinates(Enumerable.Repeat(new Vector2f(0.25f, 0.75f), box.VertexCount).ToArray());

        Assert.True(MeshOps.SplitAtCreases(box, FortyDegrees, out Mesh? split).IsSuccess);

        Assert.Equal(24, split!.VertexCount);
        Assert.Equal(24, split.VertexColors.Length);
        Assert.Equal(24, split.TextureCoordinates.Length);

        foreach (Vector2f uv in split.TextureCoordinates)
        {
            Assert.Equal(0.25f, uv.X);
            Assert.Equal(0.75f, uv.Y);
        }
    }

    [Fact]
    public void SplitAtCreases_LeavesTheGeometryExactlyWhereItWas()
    {
        Mesh box = MeshBuilders.CreateBox(3, 5, 7);
        Assert.True(MeshOps.SplitAtCreases(box, FortyDegrees, out Mesh? split).IsSuccess);

        double before = 0;
        double after = 0;

        for (int i = 0; i < box.FaceCount; i++)
        {
            before += MeshOps.FaceArea(box, i);
        }

        for (int i = 0; i < split!.FaceCount; i++)
        {
            after += MeshOps.FaceArea(split, i);
        }

        Assert.Equal(before, after, 9);
    }

    [Fact]
    public void SplitAtCreases_RejectsANegativeCreaseAngle()
    {
        OperationResult result = MeshOps.SplitAtCreases(
            MeshBuilders.CreateBox(1, 1, 1), -0.5, out Mesh? split);

        Assert.True(result.IsFailed);
        Assert.Null(split);
    }

    [Fact]
    public void Mirror_ReflectsAcrossAPlaneThroughTheOrigin()
    {
        TMatrix mirror = Transforms.Mirror(Plane.WorldZX);

        Point3d moved = PointOps.Transform(PointOps.Create(1, 4, 2), mirror);

        Assert.True(PointOps.EpsilonEquals(moved, PointOps.Create(1, -4, 2), 1e-12));
    }

    [Fact]
    public void Mirror_ReflectsAcrossAnOffsetPlane()
    {
        // The case that matters for furniture: a part mirrored about the middle of the carcass, not about
        // the world origin.
        TMatrix mirror = Transforms.Mirror(PointOps.Create(0, 0, 0) + VectorOps.Create(10, 0, 0), Vector3d.XAxis);

        Assert.True(PointOps.EpsilonEquals(
            PointOps.Transform(PointOps.Create(2, 3, 4), mirror),
            PointOps.Create(18, 3, 4),
            1e-12));

        // A point on the mirror does not move.
        Assert.True(PointOps.EpsilonEquals(
            PointOps.Transform(PointOps.Create(10, 5, 6), mirror),
            PointOps.Create(10, 5, 6),
            1e-12));
    }

    [Fact]
    public void Mirror_IsItsOwnInverse()
    {
        TMatrix mirror = Transforms.Mirror(
            PointOps.Create(1, 2, 3), VectorOps.Create(1, 1, 1));

        Point3d original = PointOps.Create(7, -2, 5);
        Point3d twice = PointOps.Transform(PointOps.Transform(original, mirror), mirror);

        Assert.True(PointOps.EpsilonEquals(twice, original, 1e-9));
    }

    [Fact]
    public void Mirror_ReversesHandednessSoAMeshNeedsFlipping()
    {
        // Documented and worth pinning: the reflected mesh occupies the right space but winds the other way,
        // so its normals point inwards until Flip puts them back.
        Mesh box = MeshBuilders.CreateBox(1, 2, 3);
        TMatrix mirror = Transforms.Mirror(Plane.WorldYZ);

        Assert.True(Transforms.Determinant(mirror) < 0);

        MeshOps.Transform(box, mirror);
        Assert.True(MeshOps.TryFaceNormal(box, 1, out Vector3d? beforeFlip));

        // Face 1 is the box's top, so after a reflection in X it should still face up — and it does, because
        // that face is perpendicular to the mirror. The one to check is a face parallel to it.
        Assert.True(MeshOps.TryFaceNormal(box, 3, out Vector3d? sideBefore));
        MeshOps.Flip(box);
        Assert.True(MeshOps.TryFaceNormal(box, 3, out Vector3d? sideAfter));

        Assert.True(VectorOps.EpsilonEquals(sideBefore!.Value, -sideAfter!.Value, 1e-12));
        Assert.NotNull(beforeFlip);
    }

    [Fact]
    public void Mirror_RejectsADegenerateNormalOrPlane()
    {
        Assert.Throws<ArgumentException>(() => Transforms.Mirror(Plane.Unset));
        Assert.Throws<ArgumentException>(() => Transforms.Mirror(Point3d.Origin, Vector3d.Zero));
    }
}
