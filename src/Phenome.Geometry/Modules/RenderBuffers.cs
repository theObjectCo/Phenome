using System.Runtime.InteropServices;
namespace Phenome.Geometry.Modules;

/// <summary>
/// Turns a <see cref="Mesh"/> into the flat buffers a GPU wants.
/// </summary>
/// <remarks>
/// This is the boundary between the modelling side, which works in double precision and n-gons, and the
/// display side, which works in single precision and triangles. Nothing beyond this point feeds back into
/// construction, which is why dropping to <see langword="float"/> here is safe.
/// <para>
/// Attributes that the mesh already stores as single precision — normals, texture coordinates, colours —
/// are handed over as raw bytes with no copy and no per-element conversion, because the in-memory layout
/// is already the layout a vertex buffer expects. Positions are the exception: they are doubles and have
/// to be narrowed.
/// </para>
/// <para>
/// Each function comes in two shapes: <c>Create…</c> allocates a fresh array, and <c>Write…</c> fills a
/// buffer the caller owns. Prefer the second when the geometry changes every frame; allocating megabytes
/// per frame is what makes a browser stutter.
/// </para>
/// </remarks>
public static class RenderBuffers
{
    /// <summary>How many triangles the mesh becomes once its faces are split.</summary>
    /// <remarks>Each face of <c>n</c> corners contributes <c>n - 2</c> triangles.</remarks>
    public static int TriangleCount(Mesh mesh)
    {
        int total = 0;

        for (int i = 0; i < mesh.FaceCount; i++)
        {
            total += mesh.CornersInFace(i) - 2;
        }

        return total;
    }

    /// <summary>How many indices the triangle buffer needs, which is three per triangle.</summary>
    public static int TriangleIndexCount(Mesh mesh) => TriangleCount(mesh) * 3;

    /// <summary>The vertex positions as single-precision triples.</summary>
    public static float[] CreatePositions(Mesh mesh)
    {
        float[] positions = new float[mesh.VertexCount * 3];
        WritePositions(mesh, positions);
        return positions;
    }

    /// <summary>Writes the vertex positions as single-precision triples into a caller-owned buffer.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> is too small to hold three values per vertex.
    /// </exception>
    public static void WritePositions(Mesh mesh, Span<float> destination) =>
        WritePositions(mesh, Point3d.Origin, destination);

    /// <summary>
    /// Writes the vertex positions relative to <paramref name="origin"/>, as single-precision triples.
    /// </summary>
    /// <remarks>
    /// Single precision holds about seven significant digits, so a model sitting far from the world origin
    /// loses resolution where it is actually being looked at — geometry at 500 metres resolves to about
    /// 30 microns, and the wobble shows up as jitter and z-fighting. Subtracting a nearby origin here and
    /// adding it back in the view matrix keeps the numbers small. Pass <see cref="Point3d.Origin"/> when
    /// the model already sits near it.
    /// </remarks>
    /// <param name="mesh">The mesh to read.</param>
    /// <param name="origin">The local origin to measure from.</param>
    /// <param name="destination">Receives three values per vertex.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> is too small to hold three values per vertex.
    /// </exception>
    public static void WritePositions(Mesh mesh, Point3d origin, Span<float> destination)
    {
        int required = mesh.VertexCount * 3;

        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"Expected room for {required} values, three per vertex, but the buffer holds {destination.Length}.",
                nameof(destination));
        }

        ReadOnlySpan<Point3d> vertices = mesh.Vertices;

        for (int i = 0; i < vertices.Length; i++)
        {
            destination[(i * 3) + 0] = (float)(vertices[i].X - origin.X);
            destination[(i * 3) + 1] = (float)(vertices[i].Y - origin.Y);
            destination[(i * 3) + 2] = (float)(vertices[i].Z - origin.Z);
        }
    }

    /// <summary>The mesh triangulated into a fresh index buffer.</summary>
    /// <param name="mesh">The mesh to triangulate.</param>
    /// <param name="indices">
    /// Three indices per triangle, or <see langword="null"/> when the call failed outright.
    /// </param>
    /// <returns>As <see cref="WriteTriangleIndices"/>.</returns>
    public static OperationResult CreateTriangleIndices(Mesh mesh, out int[]? indices)
    {
        indices = new int[TriangleIndexCount(mesh)];
        return WriteTriangleIndices(mesh, indices);
    }

    /// <summary>Triangulates the mesh into a caller-owned index buffer.</summary>
    /// <remarks>
    /// Each face is triangulated by <see cref="Triangulation.WriteFaceTriangles"/>, which preserves winding
    /// and therefore the face normal. Triangles are copied straight through, quads are split across their
    /// shorter diagonal, and only faces of five corners or more run the ear clipper — so the common paths
    /// allocate nothing.
    /// <para>
    /// This used to fan every face from its first corner, which is correct for convex faces only: a concave
    /// n-gon came out with triangles spilling outside it, visible as a face filled in where it should have
    /// been notched. Ear clipping fixes that. The triangle count is unchanged, so a buffer sized by
    /// <see cref="TriangleIndexCount"/> still fits exactly.
    /// </para>
    /// <para>
    /// Indices are 32-bit. Above 65,535 vertices a 16-bit buffer cannot address the mesh at all, and any
    /// mesh worth streaming to a browser is past that, so there is no narrow variant to choose between.
    /// </para>
    /// </remarks>
    /// <param name="mesh">The mesh to triangulate.</param>
    /// <param name="destination">Receives three indices per triangle.</param>
    /// <returns>
    /// <see cref="ResultStatus.Partial"/> when any face had to be fanned because it is degenerate or does
    /// not lie in a plane; the buffer is filled either way, so the status is about whether to trust the
    /// result rather than whether to use it.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    public static OperationResult WriteTriangleIndices(Mesh mesh, Span<int> destination)
    {
        int required = TriangleIndexCount(mesh);

        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"Expected room for {required} indices, but the buffer holds {destination.Length}.",
                nameof(destination));
        }

        int write = 0;
        int troubled = 0;
        string? firstProblem = null;

        for (int face = 0; face < mesh.FaceCount; face++)
        {
            int span = Triangulation.TriangleCount(mesh.CornersInFace(face)) * 3;

            OperationResult result = Triangulation.WriteFaceTriangles(
                mesh,
                face,
                destination.Slice(write, span));

            if (!result.IsSuccess)
            {
                troubled++;
                firstProblem ??= result.Message;
            }

            write += span;
        }

        if (troubled == 0)
        {
            return OperationResult.Success;
        }

        return OperationResult.Partial(
            $"{troubled} of {mesh.FaceCount} faces could not be triangulated cleanly and were fanned " +
            $"instead. First: {firstProblem}");
    }

    /// <summary>
    /// The per-vertex normals as raw bytes, ready to upload without conversion.
    /// </summary>
    /// <remarks>
    /// A view onto the mesh's own storage: no copy, no allocation. Mutating the mesh invalidates it. Empty
    /// when the mesh has no normals.
    /// </remarks>
    public static ReadOnlySpan<byte> NormalBytes(Mesh mesh) =>
        MemoryMarshal.AsBytes(mesh.Normals);

    /// <summary>The per-vertex texture coordinates as raw bytes, ready to upload without conversion.</summary>
    /// <remarks>A view onto the mesh's own storage. Empty when the mesh has none.</remarks>
    public static ReadOnlySpan<byte> TextureCoordinateBytes(Mesh mesh) =>
        MemoryMarshal.AsBytes(mesh.TextureCoordinates);

    /// <summary>
    /// The per-vertex colours as raw bytes in red, green, blue, alpha order.
    /// </summary>
    /// <remarks>
    /// A view onto the mesh's own storage, four bytes per vertex. Empty when the mesh has none.
    /// </remarks>
    public static ReadOnlySpan<byte> VertexColorBytes(Mesh mesh) =>
        MemoryMarshal.AsBytes(mesh.VertexColors);
}
