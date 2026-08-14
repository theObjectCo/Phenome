using System.Runtime.InteropServices;

namespace Phenome.Geometry.Types;

/// <summary>
/// A mutable container for an ordered run of points joined by straight segments.
/// </summary>
/// <remarks>
/// A polyline of <c>n</c> points has <c>n - 1</c> segments. Closing it means repeating the first point at
/// the end, so a closed square is five points, not four — the same convention RhinoCommon uses, and the one
/// that survives a round trip through a file or a buffer without a flag riding alongside.
/// <para>
/// The cost of that convention is that anything consuming a closed polyline has to know about the repeated
/// point; <see cref="PolylineOps.IsClosed"/> answers the question with a tolerance rather than by
/// comparing floats exactly.
/// </para>
/// <para>
/// The previous library made this type <c>List&lt;Point3d&gt;</c> by inheritance, which handed out every
/// list mutation with no way to keep an invariant, threw on an empty polyline when asked whether it was
/// closed, and had a <c>Transform</c> that silently did nothing because it mutated copies handed back by the
/// list indexer.
/// </para>
/// <para>
/// There is no public constructor;
/// <see cref="PolylineOps.Create(System.ReadOnlySpan{Point3d})"/> is the way in.
/// </para>
/// </remarks>
public sealed class Polyline
{
    private readonly List<Point3d> _points = [];

    internal Polyline()
    {
    }

    /// <summary>How many points the polyline holds.</summary>
    public int PointCount => _points.Count;

    /// <summary>The points, as a view onto the underlying storage.</summary>
    /// <remarks>
    /// No copy is made. Adding points may reallocate the storage and invalidate any span taken earlier.
    /// </remarks>
    public ReadOnlySpan<Point3d> Points => CollectionsMarshal.AsSpan(_points);

    /// <summary>The points, as a writable view onto the underlying storage.</summary>
    /// <remarks>
    /// For bulk work such as transforming every point without copying the list. Writing through this is
    /// safe; adding or removing points while holding the span is not.
    /// </remarks>
    public Span<Point3d> PointsForWriting() => CollectionsMarshal.AsSpan(_points);

    /// <summary>Appends one point and returns its index.</summary>
    public int AddPoint(Point3d point)
    {
        _points.Add(point);
        return _points.Count - 1;
    }

    /// <summary>Appends several points and returns the index of the first.</summary>
    public int AddPoints(ReadOnlySpan<Point3d> points)
    {
        int first = _points.Count;

        foreach (Point3d point in points)
        {
            _points.Add(point);
        }

        return first;
    }

    /// <summary>Removes every point.</summary>
    public void Clear() => _points.Clear();

    /// <inheritdoc/>
    public override string ToString() => $"Polyline(P {PointCount})";

    /// <summary>Reserves room without changing what the polyline holds.</summary>
    /// <remarks>
    /// Internal on purpose, for the same reason as on <see cref="Mesh"/>: a caller should never have to
    /// think about capacity, but an operation that knows its output size can skip the intermediate
    /// reallocations.
    /// </remarks>
    internal void Reserve(int pointCount) => _points.EnsureCapacity(pointCount);
}
