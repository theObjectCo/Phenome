using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Vector2f = System.Numerics.Vector2;
using Vector3f = System.Numerics.Vector3;
namespace Phenome.Geometry.Modules;

/// <summary>
/// Everything you can do with a <see cref="Mesh"/>.
/// </summary>
/// <remarks>
/// Operations that only read the mesh return a new value. Operations that would otherwise copy megabytes
/// of vertices — transforming, compacting — mutate the mesh in place and say so in their name or
/// documentation; pair them with <see cref="Duplicate"/> when the input must survive.
/// </remarks>
public static class MeshOps
{
    /// <summary>An empty mesh.</summary>
    public static Mesh Create() => new();

    /// <summary>A mesh with the given vertices and faces.</summary>
    /// <param name="vertices">The vertices.</param>
    /// <param name="faces">
    /// The faces, each an array of at least three indices into <paramref name="vertices"/>.
    /// </param>
    /// <exception cref="ArgumentException">A face holds fewer than three indices.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A corner index does not refer to a vertex.</exception>
    public static Mesh Create(ReadOnlySpan<Point3d> vertices, IReadOnlyList<int[]> faces)
    {
        Mesh mesh = new();
        int corners = 0;

        for (int i = 0; i < faces.Count; i++)
        {
            corners += faces[i].Length;
        }

        mesh.Reserve(vertices.Length, faces.Count, corners);
        mesh.AddVertices(vertices);

        for (int i = 0; i < faces.Count; i++)
        {
            mesh.AddFace(faces[i]);
        }

        return mesh;
    }

    /// <summary>An independent copy of the mesh, attributes included.</summary>
    public static Mesh Duplicate(Mesh mesh)
    {
        Mesh copy = new();
        copy.Reserve(mesh.VertexCount, mesh.FaceCount, mesh.FaceCornerCount);
        copy.AddVertices(mesh.Vertices);

        for (int i = 0; i < mesh.FaceCount; i++)
        {
            copy.AddFace(mesh.Face(i));
        }

        if (mesh.HasNormals)
        {
            copy.SetNormals(mesh.Normals);
        }

        if (mesh.HasTextureCoordinates)
        {
            copy.SetTextureCoordinates(mesh.TextureCoordinates);
        }

        if (mesh.HasVertexColors)
        {
            copy.SetVertexColors(mesh.VertexColors);
        }

        if (mesh.HasFaceGroups)
        {
            copy.SetFaceGroups(mesh.FaceGroups);
        }

        return copy;
    }

    /// <summary>
    /// <see langword="true"/> when every vertex is finite and every face has at least three corners, all
    /// of which refer to existing vertices.
    /// </summary>
    /// <remarks>
    /// Corner ranges are checked when a face is added, so this can only fail for them if vertices were
    /// removed afterwards. The vertex check is the one that catches a NaN arriving from a degenerate
    /// construction upstream.
    /// </remarks>
    public static bool IsValid(Mesh mesh)
    {
        foreach (Point3d vertex in mesh.Vertices)
        {
            if (!PointOps.IsValid(vertex))
            {
                return false;
            }
        }

        for (int i = 0; i < mesh.FaceCount; i++)
        {
            ReadOnlySpan<int> corners = mesh.Face(i);

            if (corners.Length < 3)
            {
                return false;
            }

            foreach (int corner in corners)
            {
                if (corner < 0 || corner >= mesh.VertexCount)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Moves every vertex of the mesh by a transformation matrix, in place.</summary>
    /// <remarks>
    /// In place rather than returning a copy, because a copy of a million vertices is 24 MB. Wrap the input
    /// in <see cref="Duplicate"/> when it has to survive.
    /// <para>
    /// Normals are not touched. Under a rigid transform they would need rotating; under a non-uniform scale
    /// they would need the inverse transpose. Recompute them with
    /// <see cref="ComputeVertexNormals"/> afterwards rather than guessing which case applies.
    /// </para>
    /// </remarks>
    public static void Transform(Mesh mesh, in TMatrix matrix)
    {
        Span<Point3d> vertices = mesh.VerticesForWriting();

        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = PointOps.Transform(vertices[i], matrix);
        }
    }

    /// <summary>
    /// The area vector of one face: a vector normal to the face whose magnitude is twice its area.
    /// </summary>
    /// <remarks>
    /// Computed by Newell's method, which sums a term per edge rather than crossing two edges. That makes
    /// it correct for faces of any corner count and well behaved for faces that are not quite planar,
    /// where picking two edges would give an answer that depends on which two.
    /// <para>
    /// Unnormalised on purpose: the magnitude carries the area, which is exactly the weight a vertex normal
    /// wants when averaging the faces around it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="faceIndex"/> is outside the mesh.
    /// </exception>
    public static Vector3d FaceAreaVector(Mesh mesh, int faceIndex)
    {
        ReadOnlySpan<int> corners = mesh.Face(faceIndex);
        ReadOnlySpan<Point3d> vertices = mesh.Vertices;

        double x = 0;
        double y = 0;
        double z = 0;

        for (int i = 0; i < corners.Length; i++)
        {
            Point3d current = vertices[corners[i]];
            Point3d next = vertices[corners[(i + 1) % corners.Length]];

            x += (current.Y - next.Y) * (current.Z + next.Z);
            y += (current.Z - next.Z) * (current.X + next.X);
            z += (current.X - next.X) * (current.Y + next.Y);
        }

        return new Vector3d(x, y, z);
    }

    /// <summary>The area of one face.</summary>
    /// <remarks>Exact for planar faces; an approximation for faces that are not.</remarks>
    public static double FaceArea(Mesh mesh, int faceIndex) =>
        VectorOps.Length(FaceAreaVector(mesh, faceIndex)) * 0.5;

    /// <summary>The unit normal of one face.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="faceIndex">Which face.</param>
    /// <param name="normal">The unit normal, or <see langword="null"/> when the face has no area.</param>
    /// <returns>
    /// <see langword="false"/> when the face is degenerate — collapsed to a line or a point — and so has
    /// no direction.
    /// </returns>
    public static bool TryFaceNormal(
        Mesh mesh,
        int faceIndex,
        [NotNullWhen(true)] out Vector3d? normal) =>
        VectorOps.TryNormalize(FaceAreaVector(mesh, faceIndex), out normal);

    /// <summary>The centre of one face, as the mean of its corners.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="faceIndex"/> is outside the mesh.
    /// </exception>
    public static Point3d FaceCenter(Mesh mesh, int faceIndex)
    {
        ReadOnlySpan<int> corners = mesh.Face(faceIndex);
        ReadOnlySpan<Point3d> vertices = mesh.Vertices;

        double x = 0;
        double y = 0;
        double z = 0;

        foreach (int corner in corners)
        {
            x += vertices[corner].X;
            y += vertices[corner].Y;
            z += vertices[corner].Z;
        }

        double denominator = 1.0 / corners.Length;
        return new Point3d(x * denominator, y * denominator, z * denominator);
    }

    /// <summary>The centre of every face, indexed by face.</summary>
    public static Point3d[] FaceCenters(Mesh mesh)
    {
        Point3d[] centers = new Point3d[mesh.FaceCount];

        for (int i = 0; i < centers.Length; i++)
        {
            centers[i] = FaceCenter(mesh, i);
        }

        return centers;
    }

    /// <summary>The unit normal of every face, indexed by face.</summary>
    /// <remarks>
    /// A degenerate face contributes <see cref="Vector3d.Unset"/> rather than silently borrowing a
    /// neighbour's direction, so the caller can see which faces are broken.
    /// </remarks>
    public static Vector3d[] FaceNormals(Mesh mesh)
    {
        Vector3d[] normals = new Vector3d[mesh.FaceCount];

        for (int i = 0; i < normals.Length; i++)
        {
            normals[i] = TryFaceNormal(mesh, i, out Vector3d? normal) ? normal.Value : Vector3d.Unset;
        }

        return normals;
    }

    /// <summary>
    /// Computes per-vertex normals from the faces around each vertex and stores them on the mesh.
    /// </summary>
    /// <remarks>
    /// Each face contributes its area vector, so larger faces weigh more — which is what makes a smooth
    /// shading normal look right on an irregular mesh. A vertex whose faces cancel out, or which no face
    /// uses, gets a zero normal; that is visible in the output rather than hidden, and it means the mesh
    /// has a problem worth looking at.
    /// </remarks>
    /// <returns>
    /// <see cref="ResultStatus.Success"/> when every vertex got a direction, or
    /// <see cref="ResultStatus.Partial"/> naming how many did not.
    /// </returns>
    public static OperationResult ComputeVertexNormals(Mesh mesh)
    {
        Vector3d[] accumulated = new Vector3d[mesh.VertexCount];

        for (int i = 0; i < mesh.FaceCount; i++)
        {
            Vector3d areaVector = FaceAreaVector(mesh, i);

            foreach (int corner in mesh.Face(i))
            {
                accumulated[corner] += areaVector;
            }
        }

        Vector3f[] normals = new Vector3f[mesh.VertexCount];
        int without = 0;

        for (int i = 0; i < accumulated.Length; i++)
        {
            if (VectorOps.TryNormalize(accumulated[i], out Vector3d? unit))
            {
                normals[i] = new Vector3f((float)unit.Value.X, (float)unit.Value.Y, (float)unit.Value.Z);
            }
            else
            {
                without++;
            }
        }

        mesh.SetNormals(normals);

        return without == 0
            ? OperationResult.Success
            : OperationResult.Partial(
                $"{without} of {mesh.VertexCount} vertices have no normal, because no face uses them or " +
                "their faces cancel out.");
    }

    /// <summary>
    /// A copy of the mesh with vertices split wherever faces meet at a sharp angle, and normals computed
    /// per split.
    /// </summary>
    /// <remarks>
    /// A vertex can only carry one normal, so a hard edge cannot be expressed by choosing normals — the
    /// vertex has to become several. This is what stops a box rendering like a cushion:
    /// <see cref="ComputeVertexNormals"/> averages the three faces at a corner into one direction, and the
    /// edges go soft. Splitting first gives each face its own corner and each corner the normal of the
    /// surface it belongs to.
    /// <para>
    /// Faces meeting at a vertex are grouped by whether their normals are within
    /// <paramref name="creaseAngleRadians"/> of each other, transitively — so a tessellated cylinder stays
    /// one smooth group all the way round even though its first and last faces point opposite ways, while
    /// the cap breaks off. A face group boundary is always a hard boundary regardless of angle, because a
    /// change of material is a change of surface and wants its own normals anyway.
    /// </para>
    /// <para>
    /// The grouping compares normals among the faces at a vertex rather than walking round the edge fan.
    /// That is the same answer for anything with ordinary topology and a good deal less machinery; where it
    /// differs is two smooth patches touching at a single point and nowhere else, which get merged rather
    /// than kept apart.
    /// </para>
    /// <para>
    /// Vertex count grows. A box goes from eight vertices to twenty-four, which is the honest cost of having
    /// twelve visible edges.
    /// </para>
    /// </remarks>
    /// <param name="mesh">The mesh to split.</param>
    /// <param name="creaseAngleRadians">
    /// How far apart two face normals may be and still share a normal. Zero splits every face apart, giving
    /// flat shading; pi keeps everything welded, matching <see cref="ComputeVertexNormals"/>. Around 0.7 —
    /// forty degrees — leaves a chamfer smooth and a corner sharp.
    /// </param>
    /// <param name="split">The split copy, or <see langword="null"/> when the call failed outright.</param>
    /// <returns>
    /// <see cref="ResultStatus.Partial"/> when some split still ended up with no direction, which means
    /// those faces have no area.
    /// </returns>
    public static OperationResult SplitAtCreases(
        Mesh mesh,
        double creaseAngleRadians,
        out Mesh? split)
    {
        split = null;

        if (!double.IsFinite(creaseAngleRadians) || creaseAngleRadians < 0)
        {
            return OperationResult.Failed(
                $"The crease angle must be finite and not negative, but was {creaseAngleRadians}.");
        }

        int vertexCount = mesh.VertexCount;
        int faceCount = mesh.FaceCount;

        // Which faces touch each vertex, as one flat buffer with offsets rather than a list per vertex: at a
        // million faces that is the difference between three arrays and four million small objects.
        int[] facesStart = new int[vertexCount + 1];

        for (int face = 0; face < faceCount; face++)
        {
            foreach (int corner in mesh.Face(face))
            {
                facesStart[corner + 1]++;
            }
        }

        for (int i = 0; i < vertexCount; i++)
        {
            facesStart[i + 1] += facesStart[i];
        }

        int[] facesAtVertex = new int[facesStart[vertexCount]];
        int[] cursor = new int[vertexCount];

        for (int face = 0; face < faceCount; face++)
        {
            foreach (int corner in mesh.Face(face))
            {
                facesAtVertex[facesStart[corner] + cursor[corner]++] = face;
            }
        }

        Vector3d[] areaVectors = new Vector3d[faceCount];
        Vector3d[] unitNormals = new Vector3d[faceCount];

        for (int face = 0; face < faceCount; face++)
        {
            areaVectors[face] = FaceAreaVector(mesh, face);
            unitNormals[face] = VectorOps.TryNormalize(areaVectors[face], out Vector3d? unit)
                ? unit.Value
                : Vector3d.Zero;
        }

        double cosineLimit = creaseAngleRadians >= Math.PI ? -1.0 : Math.Cos(creaseAngleRadians);
        ReadOnlySpan<int> faceGroups = mesh.HasFaceGroups ? mesh.FaceGroups : default;
        bool hasGroups = mesh.HasFaceGroups;

        split = new Mesh();
        split.Reserve(facesAtVertex.Length, mesh.FaceCornerStorage.Count, faceCount);

        // Which new vertex each (vertex, face) pairing ended up at, in the same layout as facesAtVertex.
        int[] newVertexAt = new int[facesAtVertex.Length];

        List<Vector3f> newNormals = new(facesAtVertex.Length);
        List<Vector2f>? newTextures = mesh.HasTextureCoordinates ? new List<Vector2f>() : null;
        List<Color32>? newColors = mesh.HasVertexColors ? new List<Color32>() : null;

        ReadOnlySpan<Point3d> vertices = mesh.Vertices;
        int[] groupOf = new int[8];
        int withoutNormal = 0;

        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            int from = facesStart[vertex];
            int incident = facesStart[vertex + 1] - from;

            if (incident == 0)
            {
                continue;
            }

            if (groupOf.Length < incident)
            {
                groupOf = new int[incident];
            }

            // Each face starts in its own group; two are merged when they could share a normal. Merging is
            // transitive by construction, which is what carries a smooth run all the way round a cylinder.
            for (int i = 0; i < incident; i++)
            {
                groupOf[i] = i;
            }

            for (int i = 0; i < incident; i++)
            {
                for (int j = i + 1; j < incident; j++)
                {
                    int faceI = facesAtVertex[from + i];
                    int faceJ = facesAtVertex[from + j];

                    if (hasGroups && faceGroups[faceI] != faceGroups[faceJ])
                    {
                        continue;
                    }

                    if (VectorOps.Dot(unitNormals[faceI], unitNormals[faceJ]) < cosineLimit)
                    {
                        continue;
                    }

                    Merge(groupOf, i, j);
                }
            }

            for (int i = 0; i < incident; i++)
            {
                if (Root(groupOf, i) != i)
                {
                    continue;
                }

                // One new vertex per group, its normal weighted by face area so a large face counts for more
                // than a sliver.
                Vector3d accumulated = Vector3d.Zero;

                for (int j = 0; j < incident; j++)
                {
                    if (Root(groupOf, j) == i)
                    {
                        accumulated += areaVectors[facesAtVertex[from + j]];
                    }
                }

                int added = split.AddVertex(vertices[vertex]);

                if (VectorOps.TryNormalize(accumulated, out Vector3d? unit))
                {
                    newNormals.Add(new Vector3f((float)unit.Value.X, (float)unit.Value.Y, (float)unit.Value.Z));
                }
                else
                {
                    newNormals.Add(default);
                    withoutNormal++;
                }

                newTextures?.Add(mesh.TextureCoordinates[vertex]);
                newColors?.Add(mesh.VertexColors[vertex]);

                for (int j = 0; j < incident; j++)
                {
                    if (Root(groupOf, j) == i)
                    {
                        newVertexAt[from + j] = added;
                    }
                }
            }
        }

        int[] rewritten = new int[8];

        for (int face = 0; face < faceCount; face++)
        {
            ReadOnlySpan<int> corners = mesh.Face(face);

            if (rewritten.Length < corners.Length)
            {
                rewritten = new int[corners.Length];
            }

            for (int i = 0; i < corners.Length; i++)
            {
                rewritten[i] = LookUpSplit(facesStart, facesAtVertex, newVertexAt, corners[i], face);
            }

            split.AddFace(rewritten.AsSpan(0, corners.Length));
        }

        split.SetNormals(CollectionsMarshal.AsSpan(newNormals));

        if (newTextures is not null)
        {
            split.SetTextureCoordinates(CollectionsMarshal.AsSpan(newTextures));
        }

        if (newColors is not null)
        {
            split.SetVertexColors(CollectionsMarshal.AsSpan(newColors));
        }

        if (hasGroups)
        {
            split.SetFaceGroups(faceGroups);
        }

        return withoutNormal == 0
            ? OperationResult.Success
            : OperationResult.Partial(
                $"{withoutNormal} of {split.VertexCount} split vertices have no normal, because the faces " +
                "they belong to have no area.");
    }

    private static int Root(int[] groupOf, int i)
    {
        while (groupOf[i] != i)
        {
            i = groupOf[i];
        }

        return i;
    }

    private static void Merge(int[] groupOf, int a, int b)
    {
        int rootA = Root(groupOf, a);
        int rootB = Root(groupOf, b);

        if (rootA == rootB)
        {
            return;
        }

        // Point the later one at the earlier, so the surviving root is always the lowest index in the group
        // and the pass that creates vertices can recognise it by `Root(i) == i`.
        if (rootA < rootB)
        {
            groupOf[rootB] = rootA;
        }
        else
        {
            groupOf[rootA] = rootB;
        }
    }

    private static int LookUpSplit(
        int[] facesStart,
        int[] facesAtVertex,
        int[] newVertexAt,
        int vertex,
        int face)
    {
        int from = facesStart[vertex];
        int to = facesStart[vertex + 1];

        for (int i = from; i < to; i++)
        {
            if (facesAtVertex[i] == face)
            {
                return newVertexAt[i];
            }
        }

        // Unreachable: the buffer was built from these very faces.
        return newVertexAt[from];
    }

    /// <summary>
    /// One mesh holding every input mesh.
    /// </summary>
    /// <remarks>
    /// Attributes are decided across the whole set rather than pairwise: normals survive when every input has
    /// them, and are dropped — with a word about it — when one does not, because a parallel list with holes in
    /// it is worse than no list.
    /// <para>
    /// This replaced an <c>Append</c> that accumulated pairwise into a mesh it mutated, and the pairwise rule is
    /// what was wrong with it. Joining began from an empty mesh, which has no attributes of its own, so the
    /// first input's normals were compared against nothing and dropped — and since the rule only reported what
    /// the *target* had lost, an empty target reported nothing. Every join silently discarded every attribute
    /// and returned success.
    /// </para>
    /// <para>
    /// Nothing is mutated, and the totals are counted before anything is allocated, so the result is reserved
    /// once rather than grown one mesh at a time. Collect what is to be joined and join it in one call: joining
    /// inside a loop recopies the accumulated mesh at every step and turns linear work into quadratic.
    /// </para>
    /// </remarks>
    /// <param name="meshes">The meshes to join, in order. May be empty.</param>
    /// <param name="joined">
    /// The result. Not nullable, because this cannot fail — a set of meshes always joins, and the status says
    /// whether to trust the attributes rather than whether to use the mesh, as with
    /// <see cref="RenderBuffers.WriteTriangleIndices"/>.
    /// </param>
    /// <returns>
    /// <see cref="ResultStatus.Success"/>, or <see cref="ResultStatus.Partial"/> naming the attributes some
    /// inputs carried and others did not.
    /// </returns>
    public static OperationResult Join(IReadOnlyList<Mesh> meshes, out Mesh joined)
    {
        int vertexCount = 0;
        int faceCount = 0;
        int cornerCount = 0;

        bool everyNormals = true;
        bool everyTextures = true;
        bool everyColors = true;
        bool everyGroups = true;

        bool anyNormals = false;
        bool anyTextures = false;
        bool anyColors = false;
        bool anyGroups = false;

        for (int i = 0; i < meshes.Count; i++)
        {
            Mesh mesh = meshes[i];

            vertexCount += mesh.VertexCount;
            faceCount += mesh.FaceCount;
            cornerCount += mesh.FaceCornerCount;

            everyNormals &= mesh.HasNormals;
            everyTextures &= mesh.HasTextureCoordinates;
            everyColors &= mesh.HasVertexColors;
            everyGroups &= mesh.HasFaceGroups;

            anyNormals |= mesh.HasNormals;
            anyTextures |= mesh.HasTextureCoordinates;
            anyColors |= mesh.HasVertexColors;
            anyGroups |= mesh.HasFaceGroups;
        }

        // Face groups are one per face; the other three are one per vertex.
        Vector3f[]? normals = everyNormals && anyNormals ? new Vector3f[vertexCount] : null;
        Vector2f[]? textures = everyTextures && anyTextures ? new Vector2f[vertexCount] : null;
        Color32[]? colors = everyColors && anyColors ? new Color32[vertexCount] : null;
        int[]? groups = everyGroups && anyGroups ? new int[faceCount] : null;

        Mesh result = new();
        result.Reserve(vertexCount, faceCount, cornerCount);

        int vertexOffset = 0;
        int faceOffset = 0;
        int[] buffer = new int[8];

        for (int i = 0; i < meshes.Count; i++)
        {
            Mesh mesh = meshes[i];

            result.AddVertices(mesh.Vertices);

            if (normals is not null)
            {
                mesh.Normals.CopyTo(normals.AsSpan(vertexOffset));
            }

            if (textures is not null)
            {
                mesh.TextureCoordinates.CopyTo(textures.AsSpan(vertexOffset));
            }

            if (colors is not null)
            {
                mesh.VertexColors.CopyTo(colors.AsSpan(vertexOffset));
            }

            if (groups is not null)
            {
                mesh.FaceGroups.CopyTo(groups.AsSpan(faceOffset));
            }

            for (int face = 0; face < mesh.FaceCount; face++)
            {
                ReadOnlySpan<int> corners = mesh.Face(face);

                if (buffer.Length < corners.Length)
                {
                    buffer = new int[corners.Length];
                }

                for (int corner = 0; corner < corners.Length; corner++)
                {
                    buffer[corner] = corners[corner] + vertexOffset;
                }

                result.AddFace(buffer.AsSpan(0, corners.Length));
            }

            vertexOffset += mesh.VertexCount;
            faceOffset += mesh.FaceCount;
        }

        if (normals is not null)
        {
            result.SetNormals(normals);
        }

        if (textures is not null)
        {
            result.SetTextureCoordinates(textures);
        }

        if (colors is not null)
        {
            result.SetVertexColors(colors);
        }

        if (groups is not null)
        {
            result.SetFaceGroups(groups);
        }

        joined = result;

        List<string> dropped = [];

        if (anyNormals && !everyNormals)
        {
            dropped.Add("normals");
        }

        if (anyTextures && !everyTextures)
        {
            dropped.Add("texture coordinates");
        }

        if (anyColors && !everyColors)
        {
            dropped.Add("vertex colours");
        }

        if (anyGroups && !everyGroups)
        {
            dropped.Add("face groups");
        }

        return dropped.Count == 0
            ? OperationResult.Success
            : OperationResult.Partial(
                $"Dropped {string.Join(", ", dropped)}: some meshes carried them and others did not.");
    }

    /// <summary>Removes several faces at once, in place, leaving the vertices alone.</summary>
    /// <remarks>
    /// One pass over the corner buffer, so removing one face costs the same as removing a thousand. This is
    /// the only way to remove faces on purpose: a single-face version would rewrite everything after it,
    /// and called in a loop would turn into quadratic work, which is exactly what the previous library did
    /// through its vertex removal.
    /// <para>
    /// Repeated and out-of-range indices are ignored rather than treated as errors, so a caller can hand
    /// over the output of a filter without deduplicating it first.
    /// </para>
    /// </remarks>
    /// <returns>How many faces were actually removed.</returns>
    public static int RemoveFaces(Mesh mesh, ReadOnlySpan<int> faceIndices)
    {
        if (mesh.FaceCount == 0 || faceIndices.IsEmpty)
        {
            return 0;
        }

        bool[] remove = new bool[mesh.FaceCount];
        int removing = 0;

        foreach (int index in faceIndices)
        {
            if (index >= 0 && index < remove.Length && !remove[index])
            {
                remove[index] = true;
                removing++;
            }
        }

        if (removing == 0)
        {
            return 0;
        }

        List<int> corners = mesh.FaceCornerStorage;
        List<int> starts = mesh.FaceStartStorage;
        int[]? groups = mesh.HasFaceGroups ? mesh.FaceGroups.ToArray() : null;
        List<int>? keptGroups = groups is null ? null : new List<int>(remove.Length - removing);

        int write = 0;
        int faceCount = remove.Length;
        List<int> newStarts = new(faceCount - removing + 1) { 0 };

        for (int face = 0; face < faceCount; face++)
        {
            int start = starts[face];
            int end = starts[face + 1];

            if (remove[face])
            {
                continue;
            }

            for (int i = start; i < end; i++)
            {
                corners[write++] = corners[i];
            }

            newStarts.Add(write);
            keptGroups?.Add(groups![face]);
        }

        corners.RemoveRange(write, corners.Count - write);
        starts.Clear();
        starts.AddRange(newStarts);

        if (keptGroups is not null)
        {
            mesh.ClearFaceGroups();
            mesh.SetFaceGroups(CollectionsMarshal.AsSpan(keptGroups));
        }

        return removing;
    }

    /// <summary>Reverses every face, in place, so the mesh faces the other way.</summary>
    /// <remarks>
    /// Only the corner order changes; vertices, attributes and face count are untouched. Any normals the
    /// mesh already carries are now pointing the wrong way and have to be recomputed —
    /// <see cref="ComputeVertexNormals"/> after this, or clear them.
    /// <para>
    /// Needed because not every builder can work out which side is outward. A closed extrusion can, and
    /// does; a lathed profile cannot, because it need not enclose anything.
    /// </para>
    /// </remarks>
    public static void Flip(Mesh mesh)
    {
        List<int> corners = mesh.FaceCornerStorage;
        List<int> starts = mesh.FaceStartStorage;

        for (int face = 0; face < mesh.FaceCount; face++)
        {
            int start = starts[face];
            int end = starts[face + 1] - 1;

            while (start < end)
            {
                (corners[start], corners[end]) = (corners[end], corners[start]);
                start++;
                end--;
            }
        }
    }

    /// <summary>A copy of the mesh with every face split into triangles.</summary>
    /// <remarks>
    /// The vertices are untouched and in the same order, because ear clipping adds none. That is what makes
    /// this cheap in the ways that matter: every per-vertex attribute — normals, texture coordinates,
    /// colours — carries across unchanged, and nothing needs rewelding afterwards.
    /// <para>
    /// Face groups are remapped rather than copied, so each triangle inherits the group of the face it came
    /// from. A group therefore keeps meaning the same thing before and after, whether it is standing in for
    /// a material or for a smoothing boundary.
    /// </para>
    /// <para>
    /// A mesh that is already all triangles still comes back as a copy, so the caller never has to work out
    /// whether the result aliases the input.
    /// </para>
    /// </remarks>
    /// <param name="mesh">The mesh to split.</param>
    /// <param name="triangulated">
    /// The triangle-only copy, or <see langword="null"/> when the call failed outright.
    /// </param>
    /// <returns>
    /// <see cref="ResultStatus.Partial"/> when any face was degenerate or non-planar enough that it had to
    /// be fanned instead of clipped.
    /// </returns>
    public static OperationResult Triangulate(Mesh mesh, out Mesh? triangulated)
    {
        triangulated = new Mesh();
        triangulated.AddVertices(mesh.Vertices);

        if (mesh.HasNormals)
        {
            triangulated.SetNormals(mesh.Normals);
        }

        if (mesh.HasTextureCoordinates)
        {
            triangulated.SetTextureCoordinates(mesh.TextureCoordinates);
        }

        if (mesh.HasVertexColors)
        {
            triangulated.SetVertexColors(mesh.VertexColors);
        }

        int triangleCount = 0;

        for (int face = 0; face < mesh.FaceCount; face++)
        {
            triangleCount += Triangulation.TriangleCount(mesh.CornersInFace(face));
        }

        triangulated.Reserve(mesh.VertexCount, triangleCount * 3, triangleCount);

        int[] buffer = new int[3];
        List<int>? groups = mesh.HasFaceGroups ? new List<int>(triangleCount) : null;
        ReadOnlySpan<int> sourceGroups = mesh.HasFaceGroups ? mesh.FaceGroups : default;

        int troubled = 0;
        string? firstProblem = null;

        for (int face = 0; face < mesh.FaceCount; face++)
        {
            int faceTriangles = Triangulation.TriangleCount(mesh.CornersInFace(face));

            if (faceTriangles == 0)
            {
                continue;
            }

            if (buffer.Length < faceTriangles * 3)
            {
                buffer = new int[faceTriangles * 3];
            }

            OperationResult result = Triangulation.WriteFaceTriangles(
                mesh,
                face,
                buffer.AsSpan(0, faceTriangles * 3));

            if (!result.IsSuccess)
            {
                troubled++;
                firstProblem ??= result.Message;
            }

            for (int i = 0; i < faceTriangles; i++)
            {
                triangulated.AddFace(buffer[i * 3], buffer[(i * 3) + 1], buffer[(i * 3) + 2]);
                groups?.Add(sourceGroups[face]);
            }
        }

        if (groups is not null)
        {
            triangulated.SetFaceGroups(CollectionsMarshal.AsSpan(groups));
        }

        if (troubled == 0)
        {
            return OperationResult.Success;
        }

        return OperationResult.Partial(
            $"{troubled} of {mesh.FaceCount} faces could not be triangulated cleanly and were fanned " +
            $"instead. First: {firstProblem}");
    }

}
