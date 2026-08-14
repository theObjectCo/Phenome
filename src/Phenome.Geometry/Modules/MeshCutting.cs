using System.Runtime.InteropServices;
using Vector2f = System.Numerics.Vector2;
using Vector3f = System.Numerics.Vector3;
namespace Phenome.Geometry.Modules;

/// <summary>
/// Cuts meshes with planes.
/// </summary>
/// <remarks>
/// Each face is clipped against the plane's half-space by the Sutherland-Hodgman method. A half-space is
/// convex, which is exactly the condition that makes that method exact rather than a compromise: walk the
/// corner loop, keep the corners on the wanted side, and insert a corner wherever an edge crosses.
/// <para>
/// Intersections are cached by edge, keyed on the pair of vertices rather than on the face asking. Both
/// faces sharing an edge therefore get the same new vertex, and the result stays watertight — clipping each
/// face independently would put two vertices at the same point on every cut edge and the mesh would come
/// apart along the seam.
/// </para>
/// <para>
/// A face wholly on the wanted side is copied across untouched, so cutting a large mesh only rewrites the
/// faces the plane actually passes through. That is worth relying on at a million faces.
/// </para>
/// <para>
/// A face the plane actually crosses is split into triangles before being clipped, and only such a face is.
/// That is not a shortcut: clipping a concave n-gon directly is where Sutherland-Hodgman is known to go
/// wrong, returning a face that genuinely falls into two pieces as a single loop whose halves are joined by
/// two edges overlapping along the cut. Its area comes out right, so the error hides — until the opening's
/// boundary inherits the degeneracy and turns two separate holes into one hole touching its own outline,
/// which cannot be capped. A triangle cut by a half-space is always convex and always one piece, so the
/// case cannot arise. The mesh keeps its n-gons everywhere except in the band the plane crosses.
/// </para>
/// </remarks>
public static class MeshCutting
{
    /// <summary>
    /// Splits a mesh into the part behind a plane and the part in front of it.
    /// </summary>
    /// <remarks>
    /// Both halves come out of one pass and share the cache of cut points, so their vertices along the seam
    /// are identical values and the two can be joined back into the original.
    /// <para>
    /// A face lying in the plane is not a matter of convention: its own normal says which half it bounds, and
    /// it goes to that one. A face whose normal agrees with the plane's is the outward skin of the material
    /// behind the plane, so it belongs to <paramref name="below"/>.
    /// </para>
    /// <para>
    /// Capping closes the openings the cut leaves. The cap's normal is the plane's normal for
    /// <paramref name="below"/> and its reverse for <paramref name="above"/>, which is why the halves are
    /// named for the plane's normal rather than for anything about the mesh. Cutting a tube leaves two
    /// boundary loops, one inside the other; loops are sorted by containment so the inner ones become holes
    /// rather than separate caps.
    /// </para>
    /// <para>
    /// A cap reuses the boundary vertices it finds, so if the mesh carries normals they will be the side
    /// faces' normals and the cap will shade as though it were curved. Run
    /// <see cref="MeshOps.SplitAtCreases"/> afterwards, or clear the normals and recompute.
    /// </para>
    /// </remarks>
    /// <param name="mesh">The mesh to cut.</param>
    /// <param name="plane">The cutting plane; its normal points from <paramref name="below"/> towards
    /// <paramref name="above"/>.</param>
    /// <param name="capped">Whether to close the openings the cut leaves.</param>
    /// <param name="below">
    /// The part on the side the normal points away from, or <see langword="null"/> when the call failed
    /// outright. Empty rather than null when the whole mesh is on the other side.
    /// </param>
    /// <param name="above">The part on the side the normal points towards.</param>
    /// <returns>
    /// <see cref="ResultStatus.Partial"/> when a cap could not be closed;
    /// <see cref="ResultStatus.Failed"/> when the plane is invalid or a vertex is not finite.
    /// </returns>
    public static OperationResult SplitByPlane(
        Mesh mesh,
        in Plane plane,
        bool capped,
        out Mesh? below,
        out Mesh? above)
    {
        below = null;
        above = null;

        if (!PlaneOps.IsValid(plane))
        {
            return OperationResult.Failed("The cutting plane is invalid.");
        }

        int vertexCount = mesh.VertexCount;
        ReadOnlySpan<Point3d> vertices = mesh.Vertices;

        // Which side of the plane each vertex is on, with a band in the middle counted as neither. Without
        // that band a vertex a nanometre off the plane spawns a sliver face and a near-duplicate vertex.
        sbyte[] side = new sbyte[vertexCount];
        double[] distance = new double[vertexCount];

        for (int v = 0; v < vertexCount; v++)
        {
            if (!PointOps.IsValid(vertices[v]))
            {
                return OperationResult.Failed($"Vertex {v} is not a finite point.");
            }

            double signed = PlaneOps.SignedDistanceTo(plane, vertices[v]);
            distance[v] = signed;
            side[v] = Math.Abs(signed) <= Tolerance.Distance ? (sbyte)0 : (sbyte)(signed < 0 ? -1 : 1);
        }

        Dictionary<int, int[]> straddlingTriangles = [];
        CutPoints cuts = CollectCutPoints(mesh, side, distance, straddlingTriangles);

        below = BuildHalf(mesh, side, cuts, straddlingTriangles, keep: -1, plane);
        above = BuildHalf(mesh, side, cuts, straddlingTriangles, keep: 1, plane);

        if (!capped)
        {
            return OperationResult.Success;
        }

        OperationResult cappedBelow = Cap(below, plane, outwardIsPlaneNormal: true);
        OperationResult cappedAbove = Cap(above, plane, outwardIsPlaneNormal: false);

        if (cappedBelow.IsSuccess && cappedAbove.IsSuccess)
        {
            return OperationResult.Success;
        }

        return OperationResult.Partial(string.Join(
            " ",
            new[] { cappedBelow.Message, cappedAbove.Message }.Where(m => m is not null)));
    }

    /// <summary>
    /// Cuts a mesh with a plane and keeps only the part behind it.
    /// </summary>
    /// <remarks>
    /// <see cref="SplitByPlane"/> with the other half discarded. Flip the plane to keep the other side.
    /// <para>
    /// Because a half-space is convex, repeating this trims to any convex region: a mitred end is one call, a
    /// chamfer two, a fit inside a box six.
    /// </para>
    /// </remarks>
    /// <param name="mesh">The mesh to cut.</param>
    /// <param name="plane">The cutting plane; its normal points at what gets removed.</param>
    /// <param name="capped">Whether to close the opening the cut leaves.</param>
    /// <param name="trimmed">The kept part, or <see langword="null"/> when the call failed outright.</param>
    /// <returns>As <see cref="SplitByPlane"/>.</returns>
    public static OperationResult TrimByPlane(
        Mesh mesh,
        in Plane plane,
        bool capped,
        out Mesh? trimmed)
    {
        OperationResult result = SplitByPlane(mesh, plane, capped, out trimmed, out _);
        return result;
    }

    /// <summary>The points where edges cross the plane, one per edge however many faces share it.</summary>
    private readonly struct CutPoints
    {
        public CutPoints(Dictionary<long, int> byEdge, List<Point3d> points, List<int> from, List<int> to, List<double> parameters)
        {
            ByEdge = byEdge;
            Points = points;
            From = from;
            To = to;
            Parameters = parameters;
        }

        public Dictionary<long, int> ByEdge { get; }

        public List<Point3d> Points { get; }

        /// <summary>The vertex the edge came from, for interpolating attributes.</summary>
        public List<int> From { get; }

        /// <summary>The vertex the edge went to.</summary>
        public List<int> To { get; }

        /// <summary>How far along the edge the crossing sits.</summary>
        public List<double> Parameters { get; }
    }

    private static long EdgeKey(int a, int b) =>
        a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    /// <summary><see langword="true"/> when the face has corners strictly on both sides of the plane.</summary>
    /// <remarks>
    /// Independent of which half is being built, so both halves agree about which faces get triangulated and
    /// therefore about where the seam runs.
    /// </remarks>
    private static bool Straddles(ReadOnlySpan<int> corners, sbyte[] side)
    {
        bool below = false;
        bool above = false;

        foreach (int corner in corners)
        {
            if (side[corner] < 0)
            {
                below = true;
            }
            else if (side[corner] > 0)
            {
                above = true;
            }
        }

        return below && above;
    }

    /// <summary>
    /// Finds every edge that will be clipped and where it crosses, plus the triangulation of the faces the
    /// plane passes through.
    /// </summary>
    /// <remarks>
    /// The triangulation happens here rather than while building each half, for two reasons. A diagonal a
    /// triangulation introduces is an edge the original mesh never had, so if it crosses the plane it needs a
    /// cut point like any other — collecting those means knowing the triangles first. And both halves have to
    /// be handed the *same* triangles, or their seams would not line up.
    /// </remarks>
    private static CutPoints CollectCutPoints(
        Mesh mesh,
        sbyte[] side,
        double[] distance,
        Dictionary<int, int[]> straddlingTriangles)
    {
        Dictionary<long, int> byEdge = [];
        List<Point3d> points = [];
        List<int> from = [];
        List<int> to = [];
        List<double> parameters = [];

        for (int face = 0; face < mesh.FaceCount; face++)
        {
            ReadOnlySpan<int> corners = mesh.Face(face);

            if (!Straddles(corners, side))
            {
                continue;
            }

            if (corners.Length > 3)
            {
                int[] triangles = new int[Triangulation.TriangleCount(corners.Length) * 3];
                Triangulation.WriteFaceTriangles(mesh, face, triangles);
                straddlingTriangles[face] = triangles;

                for (int t = 0; t < triangles.Length; t += 3)
                {
                    AddCrossing(triangles[t], triangles[t + 1]);
                    AddCrossing(triangles[t + 1], triangles[t + 2]);
                    AddCrossing(triangles[t + 2], triangles[t]);
                }

                continue;
            }

            for (int i = 0; i < corners.Length; i++)
            {
                AddCrossing(corners[i], corners[(i + 1) % corners.Length]);
            }
        }

        return new CutPoints(byEdge, points, from, to, parameters);

        void AddCrossing(int a, int b)
        {
            // Only an edge with a vertex strictly either side needs a new point. An edge ending on the plane
            // already has its crossing: the vertex that sits there.
            if (side[a] * side[b] != -1)
            {
                return;
            }

            long key = EdgeKey(a, b);

            if (byEdge.ContainsKey(key))
            {
                return;
            }

            // Solved from the lower-indexed vertex whichever way the face traverses the edge, so the two
            // faces sharing it agree to the last bit rather than to within a rounding error.
            int lower = Math.Min(a, b);
            int higher = Math.Max(a, b);
            double t = distance[lower] / (distance[lower] - distance[higher]);

            byEdge[key] = points.Count;
            points.Add(PointOps.Lerp(mesh.Vertices[lower], mesh.Vertices[higher], t));
            from.Add(lower);
            to.Add(higher);
            parameters.Add(t);
        }
    }

    private static Mesh BuildHalf(
        Mesh mesh,
        sbyte[] side,
        CutPoints cuts,
        Dictionary<int, int[]> straddlingTriangles,
        int keep,
        in Plane plane)
    {
        int vertexCount = mesh.VertexCount;
        Mesh result = new();

        // Pool indices run over the original vertices then the cut points, so one remap array covers both.
        int[] remap = new int[vertexCount + cuts.Points.Count];
        Array.Fill(remap, -1);

        List<Vector3f>? normals = mesh.HasNormals ? [] : null;
        List<Vector2f>? textures = mesh.HasTextureCoordinates ? [] : null;
        List<Color32>? colors = mesh.HasVertexColors ? [] : null;
        List<int>? groups = mesh.HasFaceGroups ? [] : null;

        // A copy rather than the span, because the local function below cannot capture a ref struct.
        int[]? sourceGroups = mesh.HasFaceGroups ? mesh.FaceGroups.ToArray() : null;

        List<int> loop = [];
        List<int> mapped = [];

        for (int face = 0; face < mesh.FaceCount; face++)
        {
            ReadOnlySpan<int> corners = mesh.Face(face);

            bool hasKept = false;
            bool hasDropped = false;

            foreach (int corner in corners)
            {
                if (side[corner] == keep)
                {
                    hasKept = true;
                }
                else if (side[corner] == -keep)
                {
                    hasDropped = true;
                }
            }

            if (!hasKept && !hasDropped)
            {
                // Every corner is in the plane. The face's own normal says which half it is the skin of, so
                // there is nothing to decide by convention.
                if (!MeshOps.TryFaceNormal(mesh, face, out Vector3d? faceNormal))
                {
                    continue;
                }

                double agreement = VectorOps.Dot(faceNormal.Value, plane.Normal);

                if ((keep == -1 && agreement <= 0) || (keep == 1 && agreement >= 0))
                {
                    continue;
                }

                Emit(corners);
                continue;
            }

            if (!hasKept)
            {
                continue;
            }

            if (!hasDropped)
            {
                Emit(corners);
                continue;
            }

            // A face the plane passes through was split into triangles when the cut points were collected.
            // Clipping an n-gon directly is where Sutherland-Hodgman goes wrong: a concave face that
            // genuinely falls into two pieces comes back as one loop whose halves are joined by a pair of
            // edges overlapping along the cut, and that degeneracy propagates into the boundary of the
            // opening and makes it uncappable. A triangle cannot do that — cut by a half-space it is always
            // convex and always one piece.
            if (!straddlingTriangles.TryGetValue(face, out int[]? triangles))
            {
                loop.Clear();
                ClipLoop(corners, side, cuts, keep, vertexCount, loop);
                Emit(CollectionsMarshal.AsSpan(loop));
                continue;
            }

            for (int t = 0; t < triangles.Length; t += 3)
            {
                loop.Clear();
                ClipLoop(triangles.AsSpan(t, 3), side, cuts, keep, vertexCount, loop);
                Emit(CollectionsMarshal.AsSpan(loop));
            }

            continue;

            void Emit(ReadOnlySpan<int> poolLoop)
            {
                mapped.Clear();

                foreach (int poolIndex in poolLoop)
                {
                    int mappedIndex = Use(result, remap, poolIndex, mesh, cuts, normals, textures, colors);

                    if (mapped.Count == 0 || mapped[^1] != mappedIndex)
                    {
                        mapped.Add(mappedIndex);
                    }
                }

                if (mapped.Count > 1 && mapped[0] == mapped[^1])
                {
                    mapped.RemoveAt(mapped.Count - 1);
                }

                if (mapped.Count < 3)
                {
                    return;
                }

                result.AddFace(CollectionsMarshal.AsSpan(mapped));
                groups?.Add(sourceGroups![face]);
            }
        }

        if (normals is not null)
        {
            result.SetNormals(CollectionsMarshal.AsSpan(normals));
        }

        if (textures is not null)
        {
            result.SetTextureCoordinates(CollectionsMarshal.AsSpan(textures));
        }

        if (colors is not null)
        {
            result.SetVertexColors(CollectionsMarshal.AsSpan(colors));
        }

        if (groups is not null)
        {
            result.SetFaceGroups(CollectionsMarshal.AsSpan(groups));
        }

        return result;
    }

    /// <summary>Sutherland-Hodgman against one half-space, in pool indices.</summary>
    private static void ClipLoop(
        ReadOnlySpan<int> corners,
        sbyte[] side,
        CutPoints cuts,
        int keep,
        int vertexCount,
        List<int> loop)
    {
        for (int i = 0; i < corners.Length; i++)
        {
            int a = corners[i];
            int b = corners[(i + 1) % corners.Length];

            // Corners on the plane belong to both halves, so the test is "not on the far side" rather than
            // "on the near side".
            if (side[a] != -keep)
            {
                loop.Add(a);
            }

            if (side[a] * side[b] == -1)
            {
                loop.Add(vertexCount + cuts.ByEdge[EdgeKey(a, b)]);
            }
        }
    }

    private static int Use(
        Mesh result,
        int[] remap,
        int poolIndex,
        Mesh source,
        CutPoints cuts,
        List<Vector3f>? normals,
        List<Vector2f>? textures,
        List<Color32>? colors)
    {
        if (remap[poolIndex] >= 0)
        {
            return remap[poolIndex];
        }

        int vertexCount = source.VertexCount;
        int added;

        if (poolIndex < vertexCount)
        {
            added = result.AddVertex(source.Vertices[poolIndex]);
            normals?.Add(source.Normals[poolIndex]);
            textures?.Add(source.TextureCoordinates[poolIndex]);
            colors?.Add(source.VertexColors[poolIndex]);
        }
        else
        {
            int cut = poolIndex - vertexCount;
            int from = cuts.From[cut];
            int to = cuts.To[cut];
            float t = (float)cuts.Parameters[cut];

            added = result.AddVertex(cuts.Points[cut]);

            // A new vertex on an edge takes its attributes from the edge, not from either end, or a texture
            // seam appears exactly where the cut is.
            normals?.Add(Vector3f.Normalize(Vector3f.Lerp(source.Normals[from], source.Normals[to], t)));
            textures?.Add(Vector2f.Lerp(source.TextureCoordinates[from], source.TextureCoordinates[to], t));
            colors?.Add(ColorOps.Lerp(source.VertexColors[from], source.VertexColors[to], t));
        }

        remap[poolIndex] = added;
        return added;
    }

    /// <summary>Closes the openings a cut left, in place.</summary>
    /// <remarks>
    /// The openings are found from the mesh itself rather than remembered from the cut: an edge lying in the
    /// plane and used by exactly one face is on the boundary of an opening. Chaining those edges by the
    /// direction their faces wound them gives loops that are already oriented, and their signed area about
    /// the plane says whether that orientation faces out or in.
    /// </remarks>
    private static OperationResult Cap(Mesh mesh, in Plane plane, bool outwardIsPlaneNormal)
    {
        if (mesh.FaceCount == 0)
        {
            return OperationResult.Success;
        }

        Dictionary<long, int> uses = [];
        List<(int From, int To)> onPlane = [];
        ReadOnlySpan<Point3d> vertices = mesh.Vertices;

        for (int face = 0; face < mesh.FaceCount; face++)
        {
            ReadOnlySpan<int> corners = mesh.Face(face);

            for (int i = 0; i < corners.Length; i++)
            {
                int a = corners[i];
                int b = corners[(i + 1) % corners.Length];
                long key = EdgeKey(a, b);
                uses[key] = uses.GetValueOrDefault(key) + 1;

                if (PlaneOps.Contains(plane, vertices[a]) && PlaneOps.Contains(plane, vertices[b]))
                {
                    onPlane.Add((a, b));
                }
            }
        }

        Dictionary<int, int> next = [];

        foreach ((int from, int to) in onPlane)
        {
            if (uses[EdgeKey(from, to)] == 1)
            {
                next[from] = to;
            }
        }

        if (next.Count == 0)
        {
            return OperationResult.Success;
        }

        List<List<int>> loops = [];
        HashSet<int> visited = [];

        foreach (int start in next.Keys)
        {
            if (!visited.Add(start))
            {
                continue;
            }

            List<int> loop = [start];
            int current = start;

            while (next.TryGetValue(current, out int following) && following != start)
            {
                if (!visited.Add(following))
                {
                    // Ran into a chain already walked, so this is not a clean loop.
                    loop.Clear();
                    break;
                }

                loop.Add(following);
                current = following;
            }

            if (loop.Count >= 3)
            {
                loops.Add(loop);
            }
        }

        if (loops.Count == 0)
        {
            return OperationResult.Partial(
                "The cut left an opening whose boundary edges do not form closed loops, so it was left open.");
        }

        return AddCaps(mesh, plane, loops, outwardIsPlaneNormal);
    }

    private static OperationResult AddCaps(
        Mesh mesh,
        in Plane plane,
        List<List<int>> loops,
        bool outwardIsPlaneNormal)
    {
        // Project once; every containment and orientation test below is two-dimensional.
        double[][] loopU = new double[loops.Count][];
        double[][] loopV = new double[loops.Count][];
        double[] areas = new double[loops.Count];
        ReadOnlySpan<Point3d> vertices = mesh.Vertices;

        for (int i = 0; i < loops.Count; i++)
        {
            List<int> loop = loops[i];
            loopU[i] = new double[loop.Count];
            loopV[i] = new double[loop.Count];

            for (int j = 0; j < loop.Count; j++)
            {
                (double u, double v) = PlaneOps.ClosestParameter(plane, vertices[loop[j]]);
                loopU[i][j] = u;
                loopV[i][j] = v;
            }

            areas[i] = SignedArea(loopU[i], loopV[i]);
        }

        // How many other loops each one sits inside. Even means it bounds material and is an outline; odd
        // means it is a hole in the loop immediately containing it. That handles an island inside a hole
        // without a special case, because such an island is back to an even depth.
        int[] depth = new int[loops.Count];
        int[] container = new int[loops.Count];
        Array.Fill(container, -1);

        for (int i = 0; i < loops.Count; i++)
        {
            double smallestContainer = double.PositiveInfinity;

            for (int j = 0; j < loops.Count; j++)
            {
                if (i == j || !ContainsPoint(loopU[j], loopV[j], loopU[i][0], loopV[i][0]))
                {
                    continue;
                }

                depth[i]++;
                double size = Math.Abs(areas[j]);

                if (size < smallestContainer)
                {
                    smallestContainer = size;
                    container[i] = j;
                }
            }
        }

        List<string> notes = [];

        for (int i = 0; i < loops.Count; i++)
        {
            if (depth[i] % 2 != 0)
            {
                continue;
            }

            List<int> holes = [];

            for (int j = 0; j < loops.Count; j++)
            {
                if (depth[j] % 2 != 0 && container[j] == i)
                {
                    holes.Add(j);
                }
            }

            if (holes.Count == 0)
            {
                // One flat face, concave or not; the mesh keeps n-gons and the render path splits it later.
                mesh.AddFace(Oriented(mesh, loops[i], areas[i], outwardIsPlaneNormal));
                continue;
            }

            OperationResult region = AddRegionCap(
                mesh, plane, loops, loopU, loopV, areas, i, holes, outwardIsPlaneNormal);

            if (region.Message is not null)
            {
                notes.Add(region.Message);
            }
        }

        return notes.Count == 0 ? OperationResult.Success : OperationResult.Partial(string.Join(" ", notes));
    }

    private static OperationResult AddRegionCap(
        Mesh mesh,
        in Plane plane,
        List<List<int>> loops,
        double[][] loopU,
        double[][] loopV,
        double[] areas,
        int outer,
        List<int> holes,
        bool outwardIsPlaneNormal)
    {
        ReadOnlySpan<Point3d> vertices = mesh.Vertices;

        Polyline outline = PolylineOps.Create(Gather(vertices, loops[outer]));
        Polyline[] holeOutlines = new Polyline[holes.Count];

        for (int h = 0; h < holes.Count; h++)
        {
            holeOutlines[h] = PolylineOps.Create(Gather(vertices, loops[holes[h]]));
        }

        // The triangulator winds counter-clockwise about the plane it is given, so handing it the flipped
        // plane is how the cap on the other half comes out facing the right way.
        Plane capPlane = outwardIsPlaneNormal ? plane : PlaneOps.Flipped(plane);

        OperationResult result = Triangulation.TryTriangulateRegion(
            outline,
            holeOutlines,
            capPlane,
            out Point3d[]? points,
            out int[]? triangles);

        if (!result.HasOutput || points is null || triangles is null)
        {
            return OperationResult.Partial(
                $"A cut opening with {holes.Count} hole(s) could not be capped: {result.Message}");
        }

        // The triangulator indexed into its own copy of the points; every one of those is a vertex the mesh
        // already has, in loop order, so no new vertices are needed.
        int[] toMesh = new int[points.Length];
        int write = 0;

        foreach (int vertex in loops[outer])
        {
            toMesh[write++] = vertex;
        }

        foreach (int hole in holes)
        {
            foreach (int vertex in loops[hole])
            {
                toMesh[write++] = vertex;
            }
        }

        for (int i = 0; i < triangles.Length; i += 3)
        {
            mesh.AddFace(toMesh[triangles[i]], toMesh[triangles[i + 1]], toMesh[triangles[i + 2]]);
        }

        return OperationResult.Success;
    }

    private static Point3d[] Gather(ReadOnlySpan<Point3d> vertices, List<int> loop)
    {
        Point3d[] points = new Point3d[loop.Count];

        for (int i = 0; i < loop.Count; i++)
        {
            points[i] = vertices[loop[i]];
        }

        return points;
    }

    /// <summary>The loop wound so that its face points out of the half being capped.</summary>
    private static int[] Oriented(Mesh mesh, List<int> loop, double signedArea, bool outwardIsPlaneNormal)
    {
        bool counterClockwise = signedArea > 0;

        if (counterClockwise == outwardIsPlaneNormal)
        {
            return [.. loop];
        }

        int[] flipped = new int[loop.Count];

        for (int i = 0; i < loop.Count; i++)
        {
            flipped[i] = loop[loop.Count - 1 - i];
        }

        return flipped;
    }

    private static double SignedArea(ReadOnlySpan<double> u, ReadOnlySpan<double> v)
    {
        double twiceArea = 0;

        for (int i = 0, j = u.Length - 1; i < u.Length; j = i++)
        {
            twiceArea += (u[j] * v[i]) - (u[i] * v[j]);
        }

        return twiceArea * 0.5;
    }

    /// <summary>Crossing count, so a concave loop answers correctly.</summary>
    private static bool ContainsPoint(
        ReadOnlySpan<double> u,
        ReadOnlySpan<double> v,
        double pointU,
        double pointV)
    {
        bool inside = false;

        for (int i = 0, j = u.Length - 1; i < u.Length; j = i++)
        {
            if (v[i] > pointV == v[j] > pointV)
            {
                continue;
            }

            double crossing = u[i] + ((pointV - v[i]) * (u[j] - u[i]) / (v[j] - v[i]));

            if (pointU < crossing)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
