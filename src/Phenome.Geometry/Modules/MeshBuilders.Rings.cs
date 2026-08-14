using System.Diagnostics.CodeAnalysis;

namespace Phenome.Geometry.Modules;

// What every surface builder here is made of: a ring of vertices, another ring, and the faces between
// them. CreateExtrusion, CreateRevolution, Loft and Sweep differ in where the rings come from and agree on everything
// after that, so the agreement lives here rather than four times over.
public static partial class MeshBuilders
{
    /// <summary>The corners of a polyline, dropping the repeated point a closed one ends with.</summary>
    private static Point3d[] Corners(Polyline polyline, bool closed)
    {
        ReadOnlySpan<Point3d> points = polyline.Points;
        return closed ? points[..^1].ToArray() : points.ToArray();
    }

    private static int[] AddRing(Mesh mesh, ReadOnlySpan<Point3d> points)
    {
        int[] indices = new int[points.Length];

        for (int i = 0; i < points.Length; i++)
        {
            indices[i] = mesh.AddVertex(points[i]);
        }

        return indices;
    }

    /// <summary>
    /// Joins two rings of matching length with one face per span.
    /// </summary>
    /// <remarks>
    /// The single piece of machinery underneath extrude, revolve, loft and sweep. A span whose two rings
    /// share a vertex — the pole of a revolve — comes out as a triangle, and one that shares both comes out
    /// as nothing at all, so a collapsed region costs no degenerate faces.
    /// </remarks>
    private static void StitchRings(Mesh mesh, ReadOnlySpan<int> a, ReadOnlySpan<int> b, bool closedRing)
    {
        int spans = closedRing ? a.Length : a.Length - 1;

        for (int i = 0; i < spans; i++)
        {
            int j = (i + 1) % a.Length;
            AddSpanFace(mesh, a[i], a[j], b[j], b[i]);
        }
    }

    private static void AddSpanFace(Mesh mesh, int p0, int p1, int p2, int p3)
    {
        if ((p0 == p3 && p1 == p2) || p0 == p2 || p1 == p3)
        {
            return;
        }

        if (p0 == p3)
        {
            mesh.AddFace(p0, p1, p2);
            return;
        }

        if (p1 == p2)
        {
            mesh.AddFace(p0, p1, p3);
            return;
        }

        if (p0 == p1)
        {
            mesh.AddFace(p0, p2, p3);
            return;
        }

        if (p2 == p3)
        {
            mesh.AddFace(p0, p1, p2);
            return;
        }

        mesh.AddFace(p0, p1, p2, p3);
    }

    /// <summary>Closes a ring with a single n-gon face.</summary>
    /// <remarks>
    /// One face, not a fan of triangles: the mesh keeps n-gons on purpose, and a cap that is concave is
    /// still one flat face. Splitting it is <see cref="RenderBuffers"/>'s business, and by then the plane is
    /// known and ear clipping handles it.
    /// </remarks>
    private static void AddCap(Mesh mesh, int[] ring, bool reversed)
    {
        if (ring.Length < 3)
        {
            return;
        }

        if (!reversed)
        {
            mesh.AddFace(ring);
            return;
        }

        int[] flipped = new int[ring.Length];

        for (int i = 0; i < ring.Length; i++)
        {
            flipped[i] = ring[ring.Length - 1 - i];
        }

        mesh.AddFace(flipped);
    }
}
