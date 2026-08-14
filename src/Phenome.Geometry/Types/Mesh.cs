using System.Runtime.InteropServices;
using Vector2f = System.Numerics.Vector2;
using Vector3f = System.Numerics.Vector3;

namespace Phenome.Geometry.Types;

/// <summary>
/// A mutable container for a mesh with faces of any corner count.
/// </summary>
/// <remarks>
/// <para><strong>Face storage.</strong></para>
/// Faces live in two flat lists rather than as one object each: <c>_faceCorners</c> holds every face's
/// corner indices concatenated, and <c>_faceStarts</c> records where each face begins, with a sentinel at
/// the end. Face <c>i</c> is the slice between <c>_faceStarts[i]</c> and <c>_faceStarts[i + 1]</c>.
/// <para>
/// For a million quads that is 16 MB of corners plus 4 MB of offsets in <em>three</em> heap objects. One
/// array per face would be 48 MB in <em>a million</em> heap objects, which a single-threaded browser
/// garbage collector has to walk on every collection. A fixed four-corner struct would save 4 MB but give
/// up n-gons, and n-gons are what let a six-edged panel be one face.
/// </para>
/// <para>
/// The previous library already wrote this layout — but only in its serialiser, as <c>FaceCounts</c> plus
/// <c>FaceValues</c>, and it stored lengths rather than offsets, so finding face <c>i</c> meant summing
/// every length before it. Storing offsets makes random access free.
/// </para>
/// <para><strong>Attributes.</strong></para>
/// Normals, texture coordinates and vertex colours are optional parallel lists, allocated only once
/// something sets them, so an untextured single-material mesh pays nothing for the ones it does not use.
/// They are single precision because they exist only to be displayed; geometry stays in
/// <see cref="Point3d"/> at double precision, where robustness actually matters.
/// <para><strong>Mutation.</strong></para>
/// Faces can be appended cheaply. There is deliberately no method to remove a single face: with a flat
/// buffer that rewrites everything after it, and called in a loop it degrades to quadratic — the same trap
/// the previous library fell into. Remove in bulk with <see cref="MeshOps.RemoveFaces"/> instead.
/// </remarks>
public sealed class Mesh
{
    private readonly List<Point3d> _vertices = [];
    private readonly List<int> _faceCorners = [];
    private readonly List<int> _faceStarts = [0];

    private List<Vector3f>? _normals;
    private List<Vector2f>? _textureCoordinates;
    private List<Color32>? _vertexColors;
    private List<int>? _faceGroups;

    internal Mesh()
    {
    }

    /// <summary>How many vertices the mesh holds.</summary>
    public int VertexCount => _vertices.Count;

    /// <summary>How many faces the mesh holds.</summary>
    public int FaceCount => _faceStarts.Count - 1;

    /// <summary>How many corner indices all faces hold between them.</summary>
    public int FaceCornerCount => _faceCorners.Count;

    /// <summary>The vertices, as a view onto the underlying storage.</summary>
    /// <remarks>
    /// No copy is made. Adding vertices may reallocate the storage and invalidate any span taken earlier.
    /// </remarks>
    public ReadOnlySpan<Point3d> Vertices => CollectionsMarshal.AsSpan(_vertices);

    /// <summary>The vertices, as a writable view onto the underlying storage.</summary>
    /// <remarks>
    /// For bulk work such as transforming every vertex without copying the list. Writing through this is
    /// safe; adding or removing vertices while holding the span is not.
    /// </remarks>
    public Span<Point3d> VerticesForWriting() => CollectionsMarshal.AsSpan(_vertices);

    /// <summary>The corner indices of one face, as a view onto the underlying storage.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="faceIndex"/> is outside 0..<see cref="FaceCount"/>-1.
    /// </exception>
    public ReadOnlySpan<int> Face(int faceIndex)
    {
        Guard.NotNegative(faceIndex);
        Guard.LessThan(faceIndex, FaceCount);

        int start = _faceStarts[faceIndex];
        return CollectionsMarshal.AsSpan(_faceCorners).Slice(start, _faceStarts[faceIndex + 1] - start);
    }

    /// <summary>How many corners one face has.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="faceIndex"/> is outside 0..<see cref="FaceCount"/>-1.
    /// </exception>
    public int CornersInFace(int faceIndex)
    {
        Guard.NotNegative(faceIndex);
        Guard.LessThan(faceIndex, FaceCount);

        return _faceStarts[faceIndex + 1] - _faceStarts[faceIndex];
    }

    /// <summary>Appends one vertex and returns its index.</summary>
    public int AddVertex(Point3d vertex)
    {
        _vertices.Add(vertex);
        return _vertices.Count - 1;
    }

    /// <summary>Appends several vertices and returns the index of the first.</summary>
    public int AddVertices(ReadOnlySpan<Point3d> vertices)
    {
        int first = _vertices.Count;

        foreach (Point3d vertex in vertices)
        {
            _vertices.Add(vertex);
        }

        return first;
    }

    /// <summary>Appends one face and returns its index.</summary>
    /// <remarks>
    /// Corner indices are checked against the vertices that exist right now. An out-of-range index is a
    /// bug that is cheap to catch here and expensive to diagnose later, and appending is the only moment
    /// where the check is possible at all.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="corners"/> holds fewer than three indices.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A corner index does not refer to an existing vertex.
    /// </exception>
    public int AddFace(ReadOnlySpan<int> corners)
    {
        if (corners.Length < 3)
        {
            throw new ArgumentException(
                $"A face needs at least 3 corners, but {corners.Length} were given.",
                nameof(corners));
        }

        foreach (int corner in corners)
        {
            if (corner < 0 || corner >= _vertices.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(corners),
                    corner,
                    $"Corner index does not refer to an existing vertex; the mesh holds {_vertices.Count}.");
            }
        }

        foreach (int corner in corners)
        {
            _faceCorners.Add(corner);
        }

        _faceStarts.Add(_faceCorners.Count);
        return FaceCount - 1;
    }

    /// <summary>Appends a triangle and returns its index.</summary>
    public int AddFace(int a, int b, int c) => AddFace([a, b, c]);

    /// <summary>Appends a quad and returns its index.</summary>
    public int AddFace(int a, int b, int c, int d) => AddFace([a, b, c, d]);

    /// <summary>Removes every vertex, face and attribute.</summary>
    public void Clear()
    {
        _vertices.Clear();
        _faceCorners.Clear();
        _faceStarts.Clear();
        _faceStarts.Add(0);

        _normals = null;
        _textureCoordinates = null;
        _vertexColors = null;
        _faceGroups = null;
    }

    /// <summary>Whether per-vertex normals have been set.</summary>
    public bool HasNormals => _normals is not null;

    /// <summary>Whether per-vertex texture coordinates have been set.</summary>
    public bool HasTextureCoordinates => _textureCoordinates is not null;

    /// <summary>Whether per-vertex colours have been set.</summary>
    public bool HasVertexColors => _vertexColors is not null;

    /// <summary>Whether per-face group identifiers have been set.</summary>
    public bool HasFaceGroups => _faceGroups is not null;

    /// <summary>
    /// The per-vertex normals, or an empty span when none have been set.
    /// </summary>
    /// <remarks>
    /// Single precision, and in a layout a vertex buffer can take verbatim.
    /// </remarks>
    public ReadOnlySpan<Vector3f> Normals =>
        _normals is null ? default : CollectionsMarshal.AsSpan(_normals);

    /// <summary>The per-vertex texture coordinates, or an empty span when none have been set.</summary>
    public ReadOnlySpan<Vector2f> TextureCoordinates =>
        _textureCoordinates is null ? default : CollectionsMarshal.AsSpan(_textureCoordinates);

    /// <summary>The per-vertex colours, or an empty span when none have been set.</summary>
    public ReadOnlySpan<Color32> VertexColors =>
        _vertexColors is null ? default : CollectionsMarshal.AsSpan(_vertexColors);

    /// <summary>
    /// The per-face group identifiers, or an empty span when none have been set.
    /// </summary>
    /// <remarks>
    /// Lets one mesh render in several materials, and lets a subset be selected without copying geometry.
    /// This is what the previous library's empty <c>PhenomeMaterialMap</c> class was meant to be.
    /// </remarks>
    public ReadOnlySpan<int> FaceGroups =>
        _faceGroups is null ? default : CollectionsMarshal.AsSpan(_faceGroups);

    /// <summary>Replaces the per-vertex normals.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="normals"/> does not hold exactly one entry per vertex.
    /// </exception>
    public void SetNormals(ReadOnlySpan<Vector3f> normals)
    {
        RequirePerVertex(normals.Length, nameof(normals));
        Replace(ref _normals, normals);
    }

    /// <summary>Replaces the per-vertex texture coordinates.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="textureCoordinates"/> does not hold exactly one entry per vertex.
    /// </exception>
    public void SetTextureCoordinates(ReadOnlySpan<Vector2f> textureCoordinates)
    {
        RequirePerVertex(textureCoordinates.Length, nameof(textureCoordinates));
        Replace(ref _textureCoordinates, textureCoordinates);
    }

    /// <summary>Replaces the per-vertex colours.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="colors"/> does not hold exactly one entry per vertex.
    /// </exception>
    public void SetVertexColors(ReadOnlySpan<Color32> colors)
    {
        RequirePerVertex(colors.Length, nameof(colors));
        Replace(ref _vertexColors, colors);
    }

    /// <summary>Replaces the per-face group identifiers.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="groups"/> does not hold exactly one entry per face.
    /// </exception>
    public void SetFaceGroups(ReadOnlySpan<int> groups)
    {
        if (groups.Length != FaceCount)
        {
            throw new ArgumentException(
                $"Expected one group per face, so {FaceCount} entries, but {groups.Length} were given.",
                nameof(groups));
        }

        Replace(ref _faceGroups, groups);
    }

    /// <summary>Drops the per-vertex normals.</summary>
    public void ClearNormals() => _normals = null;

    /// <summary>Drops the per-vertex texture coordinates.</summary>
    public void ClearTextureCoordinates() => _textureCoordinates = null;

    /// <summary>Drops the per-vertex colours.</summary>
    public void ClearVertexColors() => _vertexColors = null;

    /// <summary>Drops the per-face group identifiers.</summary>
    public void ClearFaceGroups() => _faceGroups = null;

    /// <inheritdoc/>
    public override string ToString() => $"Mesh(V {VertexCount}; F {FaceCount})";

    /// <summary>
    /// Reserves room without changing what the mesh holds.
    /// </summary>
    /// <remarks>
    /// Internal on purpose. Growing a list by doubling is amortised constant time, so callers should never
    /// have to think about capacity; the builders in this assembly happen to know their output size up
    /// front and can avoid the intermediate reallocations, which is an implementation detail of theirs.
    /// </remarks>
    internal void Reserve(int vertexCount, int faceCount, int cornerCount)
    {
        _vertices.EnsureCapacity(vertexCount);
        _faceStarts.EnsureCapacity(faceCount + 1);
        _faceCorners.EnsureCapacity(cornerCount);
    }

    /// <summary>The corner index list, for operations that rewrite face storage wholesale.</summary>
    internal List<int> FaceCornerStorage => _faceCorners;

    /// <summary>The face offset list, for operations that rewrite face storage wholesale.</summary>
    internal List<int> FaceStartStorage => _faceStarts;

    /// <summary>The vertex list, for operations that rewrite vertex storage wholesale.</summary>
    internal List<Point3d> VertexStorage => _vertices;

    private void RequirePerVertex(int given, string parameterName)
    {
        if (given != VertexCount)
        {
            throw new ArgumentException(
                $"Expected one entry per vertex, so {VertexCount} of them, but {given} were given.",
                parameterName);
        }
    }

    private static void Replace<T>(ref List<T>? target, ReadOnlySpan<T> source)
    {
        target ??= new List<T>(source.Length);
        target.Clear();

        foreach (T item in source)
        {
            target.Add(item);
        }
    }
}
