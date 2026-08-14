namespace Phenome.Geometry.Modules;

/// <summary>
/// Splits planar outlines into triangles by ear clipping.
/// </summary>
/// <remarks>
/// The outline is projected into the plane's own coordinates and clipped there, so every test is a
/// two-dimensional sign test with a definite notion of which side is inside. Doing it in three dimensions
/// would have no such notion.
/// <para>
/// An outline of <c>n</c> corners always yields exactly <c>n - 2</c> triangles, and a region with
/// <c>h</c> holes yields <c>n + 2h - 2</c> where <c>n</c> counts every corner including the holes'. That
/// count holds even when the result is <see cref="ResultStatus.Partial"/>, which is what lets a caller size
/// a buffer up front.
/// </para>
/// <para>
/// No vertices are added. Holes are merged into the outline by bridging, which repeats two existing corners
/// in the traversal rather than inventing new positions, so the triangles index straight into the points
/// that went in. Ear clipping is the deliberate choice over a constrained Delaunay triangulation: the
/// outlines here have tens of corners, not thousands, so the asymptotics do not matter, and rendering
/// cannot tell a well-shaped triangle from a sliver. What it buys is a few hundred lines of arithmetic with
/// no exact predicates to get wrong.
/// </para>
/// <para>
/// Triangle quality is therefore not guaranteed. If a later feature needs it — a scalar field drawn across
/// a region, an offset, a subdivision — the route is a Delaunay edge-flip pass over this output, not a
/// different triangulator.
/// </para>
/// </remarks>
public static class Triangulation
{
    /// <summary>How many triangles an outline of the given corner count becomes.</summary>
    /// <remarks>
    /// Any simple polygon of <c>n</c> corners splits into exactly <c>n - 2</c> triangles, whatever its
    /// shape, so a buffer can be sized before the outline is looked at.
    /// </remarks>
    public static int TriangleCount(int cornerCount) => Math.Max(0, cornerCount - 2);

    /// <summary>
    /// How many triangles a region of the given outer corner count and hole corner counts becomes.
    /// </summary>
    /// <remarks>
    /// Each hole costs two extra triangles beyond its own corners, which is the price of the two repeated
    /// corners its bridge introduces.
    /// </remarks>
    public static int RegionTriangleCount(int outerCornerCount, ReadOnlySpan<int> holeCornerCounts)
    {
        int corners = outerCornerCount;

        for (int i = 0; i < holeCornerCounts.Length; i++)
        {
            corners += holeCornerCounts[i] + 2;
        }

        return Math.Max(0, corners - 2);
    }

    /// <summary>
    /// Triangulates a closed outline lying in a plane, indexing into the points given.
    /// </summary>
    /// <remarks>
    /// The outline must not repeat its first point at the end; pass the corners only. Winding does not
    /// matter — the outline is turned counter-clockwise in the projection before clipping — but the
    /// triangles come back wound to match <paramref name="outline"/> as given, so a mesh face built from
    /// them keeps the normal the caller intended.
    /// </remarks>
    /// <param name="outline">The corners, in order, without a repeated closing point.</param>
    /// <param name="plane">The plane to project into; its normal decides which winding is positive.</param>
    /// <param name="triangles">
    /// Three indices into <paramref name="outline"/> per triangle, or <see langword="null"/> when the call
    /// failed outright. Non-null whenever the result reports <see cref="OperationResult.HasOutput"/>.
    /// </param>
    /// <returns>
    /// <see cref="ResultStatus.Success"/> when every triangle came from a real ear;
    /// <see cref="ResultStatus.Partial"/> when clipping stalled and the remainder had to be fanned, which
    /// means the outline crosses itself or doubles back;
    /// <see cref="ResultStatus.Failed"/> when there is nothing to triangulate.
    /// </returns>
    public static OperationResult TryTriangulate(
        ReadOnlySpan<Point3d> outline,
        in Plane plane,
        out int[]? triangles)
    {
        triangles = null;

        if (!PlaneOps.IsValid(plane))
        {
            return OperationResult.Failed("The plane is invalid, so the outline cannot be projected.");
        }

        if (outline.Length < 3)
        {
            return OperationResult.Failed(
                $"An outline needs at least three corners to triangulate, but {outline.Length} were given.");
        }

        for (int i = 0; i < outline.Length; i++)
        {
            if (!PointOps.IsValid(outline[i]))
            {
                return OperationResult.Failed($"Corner {i} of the outline is not a finite point.");
            }
        }

        int n = outline.Length;
        int[] loop = new int[n];
        double[] u = new double[n];
        double[] v = new double[n];

        Project(outline, plane, u, v);

        if (Crosses(u, v))
        {
            return OperationResult.Failed(
                "The outline crosses itself, so no triangulation of it exists.");
        }

        // Clip counter-clockwise always, so every sign test has one meaning. Reversing the traversal rather
        // than the points means the emitted indices still refer to the outline as the caller gave it.
        bool reversed = SignedArea(u, v) < 0;

        for (int i = 0; i < n; i++)
        {
            loop[i] = reversed ? n - 1 - i : i;
        }

        if (reversed)
        {
            Reverse(u);
            Reverse(v);
        }

        triangles = new int[TriangleCount(n) * 3];
        OperationResult result = ClipEars(loop, u, v, triangles, out _);

        if (reversed)
        {
            // Clipping ran on the reversed traversal, so every triangle came out counter-clockwise about the
            // plane normal. Swapping two corners of each puts the winding back to the outline's own, which a
            // mesh face built from these depends on for its normal.
            for (int i = 0; i < triangles.Length; i += 3)
            {
                (triangles[i], triangles[i + 2]) = (triangles[i + 2], triangles[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Whether an outline crosses itself once projected into a plane.
    /// </summary>
    /// <remarks>
    /// Every pair of non-adjacent edges is tested, so this costs the square of the corner count. At the
    /// tens of corners these outlines have that is nothing, and it converts a self-crossing outline from a
    /// plausible-looking wrong answer into a reported failure — ear clipping alone cannot tell: a bowtie
    /// clips without ever stalling and hands back overlapping triangles.
    /// <para>
    /// Only proper crossings count. Edges that merely touch, or that lie along each other, are not
    /// detected, so this is a test worth trusting when it says yes and not when it says no.
    /// </para>
    /// </remarks>
    /// <param name="outline">The corners, in order, without a repeated closing point.</param>
    /// <param name="plane">The plane to project into.</param>
    public static bool SelfIntersects(ReadOnlySpan<Point3d> outline, in Plane plane)
    {
        if (outline.Length < 4 || !PlaneOps.IsValid(plane))
        {
            return false;
        }

        double[] u = new double[outline.Length];
        double[] v = new double[outline.Length];
        Project(outline, plane, u, v);

        return Crosses(u, v);
    }

    /// <summary>
    /// Triangulates a closed outline lying in a plane fitted to it, indexing into the points given.
    /// </summary>
    /// <remarks>
    /// The plane comes from <see cref="PlaneOps.TryCreateFromBestFit"/>, so its normal — and therefore the winding
    /// of the output — depends on the point order only through the fit. Pass a plane explicitly when the
    /// normal matters.
    /// </remarks>
    /// <param name="outline">The corners, in order, without a repeated closing point.</param>
    /// <param name="triangles">
    /// Three indices into <paramref name="outline"/> per triangle, or <see langword="null"/> when the call
    /// failed outright.
    /// </param>
    /// <returns>
    /// <see cref="ResultStatus.Failed"/> when no plane can be fitted, which happens when the corners are
    /// collinear or coincident; otherwise as <see cref="TryTriangulate(ReadOnlySpan{Point3d}, in Plane, out int[])"/>.
    /// </returns>
    public static OperationResult TryTriangulate(ReadOnlySpan<Point3d> outline, out int[]? triangles)
    {
        triangles = null;

        if (!PlaneOps.TryCreateFromBestFit(outline, out Plane? plane, out _))
        {
            return OperationResult.Failed(
                "No plane fits the outline, so it is collinear, coincident, or too short to triangulate.");
        }

        return TryTriangulate(outline, plane.Value, out triangles);
    }

    /// <summary>
    /// Triangulates a closed polyline lying in a plane, indexing into the polyline's points.
    /// </summary>
    /// <remarks>
    /// A repeated closing point is dropped before clipping, so both a closed and an unclosed polyline of the
    /// same shape triangulate the same way. The indices refer to the polyline's own points, so the dropped
    /// duplicate simply never appears in the output.
    /// </remarks>
    /// <param name="outline">The outline; may or may not repeat its first point at the end.</param>
    /// <param name="plane">The plane to project into.</param>
    /// <param name="triangles">
    /// Three indices into <paramref name="outline"/>'s points per triangle, or <see langword="null"/> when
    /// the call failed outright.
    /// </param>
    /// <returns>As <see cref="TryTriangulate(ReadOnlySpan{Point3d}, in Plane, out int[])"/>.</returns>
    public static OperationResult TryTriangulate(
        Polyline outline,
        in Plane plane,
        out int[]? triangles)
    {
        return TryTriangulate(CornersOf(outline), plane, out triangles);
    }

    /// <summary>
    /// Triangulates a planar region bounded by an outer outline with holes cut out of it.
    /// </summary>
    /// <remarks>
    /// Each hole is joined to the outer outline by a bridge: the traversal walks out to the hole along a
    /// pair of coincident edges, all the way round it, and back. That turns the region into one outline that
    /// ordinary ear clipping can handle, at the cost of two repeated corners per hole. The repeats are in
    /// the traversal only — <paramref name="vertices"/> holds each position once, so the output feeds
    /// straight into a mesh with no welding needed.
    /// <para>
    /// Holes are bridged in order of how far right they reach, each to whatever the outline has become, so a
    /// hole may bridge to an earlier hole rather than to the outer boundary. Winding is normalised, so
    /// neither the outer outline nor the holes need to be given any particular way round.
    /// </para>
    /// <para>
    /// A hole nested inside another hole is not supported and there is no attempt to detect it; the result
    /// will be wrong rather than reported. A hole that falls outside the outer outline is detected, skipped,
    /// and reported as <see cref="ResultStatus.Partial"/>.
    /// </para>
    /// </remarks>
    /// <param name="outer">The outer boundary; a repeated closing point is dropped.</param>
    /// <param name="holes">The holes; each has its repeated closing point dropped. May be empty.</param>
    /// <param name="plane">The plane to project into; its normal decides which winding is positive.</param>
    /// <param name="vertices">
    /// The outer corners followed by each hole's corners, in the order the holes were given, or
    /// <see langword="null"/> when the call failed outright. This is what
    /// <paramref name="triangles"/> indexes into.
    /// </param>
    /// <param name="triangles">
    /// Three indices into <paramref name="vertices"/> per triangle, or <see langword="null"/> when the call
    /// failed outright.
    /// </param>
    /// <returns>
    /// <see cref="ResultStatus.Partial"/> when a hole was skipped or clipping stalled;
    /// <see cref="ResultStatus.Failed"/> when the outer outline or the plane is unusable.
    /// </returns>
    public static OperationResult TryTriangulateRegion(
        Polyline outer,
        IReadOnlyList<Polyline> holes,
        in Plane plane,
        out Point3d[]? vertices,
        out int[]? triangles)
    {
        vertices = null;
        triangles = null;

        if (!PlaneOps.IsValid(plane))
        {
            return OperationResult.Failed("The plane is invalid, so the region cannot be projected.");
        }

        ReadOnlySpan<Point3d> outerCorners = CornersOf(outer);

        if (outerCorners.Length < 3)
        {
            return OperationResult.Failed(
                $"The outer outline needs at least three corners, but {outerCorners.Length} were given.");
        }

        if (SelfIntersects(outerCorners, plane))
        {
            return OperationResult.Failed(
                "The outer outline crosses itself, so no triangulation of the region exists.");
        }

        // One flat vertex array: outer corners, then each hole's corners. Every index from here on refers
        // into this, including the repeated ones a bridge introduces.
        List<Point3d> allVertices = [];
        // Copied one at a time rather than with AddRange, which only takes a span from .NET 9 and this
        // library also builds for the .NET 7 that Rhino 8 hosts.
        foreach (Point3d corner in outerCorners)
        {
            allVertices.Add(corner);
        }

        List<int[]> holeLoops = [];
        List<string> skipped = [];

        for (int h = 0; h < holes.Count; h++)
        {
            ReadOnlySpan<Point3d> holeCorners = CornersOf(holes[h]);

            if (holeCorners.Length < 3)
            {
                skipped.Add($"hole {h} has only {holeCorners.Length} corners");
                continue;
            }

            if (SelfIntersects(holeCorners, plane))
            {
                skipped.Add($"hole {h} crosses itself");
                continue;
            }

            int[] holeLoop = new int[holeCorners.Length];

            for (int i = 0; i < holeCorners.Length; i++)
            {
                holeLoop[i] = allVertices.Count;
                allVertices.Add(holeCorners[i]);
            }

            holeLoops.Add(holeLoop);
        }

        Point3d[] vertexArray = [.. allVertices];

        for (int i = 0; i < vertexArray.Length; i++)
        {
            if (!PointOps.IsValid(vertexArray[i]))
            {
                return OperationResult.Failed($"Corner {i} of the region is not a finite point.");
            }
        }

        double[] vertexU = new double[vertexArray.Length];
        double[] vertexV = new double[vertexArray.Length];
        Project(vertexArray, plane, vertexU, vertexV);

        // The outer outline runs counter-clockwise and every hole the other way, so that a hole reads as a
        // hole once it is spliced into the same traversal.
        List<int> loop = BuildOriented(outerCorners.Length, vertexU, vertexV, counterClockwise: true);

        for (int h = 0; h < holeLoops.Count; h++)
        {
            holeLoops[h] = OrientLoop(holeLoops[h], vertexU, vertexV, counterClockwise: false);
        }

        // Bridging always casts its ray to the right, so taking the holes in order of how far right they
        // reach means the ray from each one has the outline it needs already in place.
        holeLoops.Sort((a, b) => MaxU(b, vertexU).CompareTo(MaxU(a, vertexU)));

        foreach (int[] holeLoop in holeLoops)
        {
            if (!TryBridgeHole(loop, holeLoop, vertexU, vertexV))
            {
                skipped.Add($"a hole reaching x={MaxU(holeLoop, vertexU):G4} lies outside the outer outline");
            }
        }

        int[] loopArray = [.. loop];
        double[] loopU = new double[loopArray.Length];
        double[] loopV = new double[loopArray.Length];

        for (int i = 0; i < loopArray.Length; i++)
        {
            loopU[i] = vertexU[loopArray[i]];
            loopV[i] = vertexV[loopArray[i]];
        }

        vertices = vertexArray;
        triangles = new int[TriangleCount(loopArray.Length) * 3];

        OperationResult clipped = ClipEars(loopArray, loopU, loopV, triangles, out _);

        if (skipped.Count == 0)
        {
            return clipped;
        }

        string note = $"Skipped {skipped.Count} hole(s): {string.Join("; ", skipped)}.";

        return clipped.IsSuccess
            ? OperationResult.Partial(note)
            : OperationResult.Partial($"{clipped.Message} {note}");
    }

    /// <summary>
    /// Whether a quadrilateral should be split across its first diagonal rather than its second.
    /// </summary>
    /// <remarks>
    /// The shorter diagonal gives the better-shaped pair of triangles and, on a quad that is not quite
    /// planar, the one that follows the surface more closely. Splitting a quad this way costs two distance
    /// comparisons and no allocation at all, which is why <see cref="RenderBuffers"/> handles quads here
    /// rather than sending them through the clipper — at a million faces that difference is the whole
    /// budget.
    /// </remarks>
    /// <param name="a">The first corner.</param>
    /// <param name="b">The second corner.</param>
    /// <param name="c">The third corner.</param>
    /// <param name="d">The fourth corner.</param>
    /// <returns>
    /// <see langword="true"/> to split into <c>abc</c> and <c>acd</c>, <see langword="false"/> to split into
    /// <c>abd</c> and <c>bcd</c>.
    /// </returns>
    public static bool SplitsOnFirstDiagonal(Point3d a, Point3d b, Point3d c, Point3d d) =>
        PointOps.DistanceSquaredTo(a, c) <= PointOps.DistanceSquaredTo(b, d);

    /// <summary>
    /// Triangulates one face of a mesh into a caller-owned buffer, as vertex indices.
    /// </summary>
    /// <remarks>
    /// Handles the three cases separately because they cost wildly different amounts. A triangle is copied.
    /// A quad picks its shorter diagonal. Only a face with five corners or more runs the clipper, which is
    /// the only path that allocates.
    /// </remarks>
    /// <param name="mesh">The mesh to read.</param>
    /// <param name="faceIndex">Which face to split.</param>
    /// <param name="destination">
    /// Receives three vertex indices per triangle, and must hold room for corners minus two triangles.
    /// </param>
    /// <returns>
    /// <see cref="ResultStatus.Partial"/> when the face is degenerate enough to have no usable normal, or
    /// when clipping stalled; the buffer is filled either way.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    public static OperationResult WriteFaceTriangles(Mesh mesh, int faceIndex, Span<int> destination)
    {
        ReadOnlySpan<int> corners = mesh.Face(faceIndex);
        int required = TriangleCount(corners.Length) * 3;

        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"Face {faceIndex} needs room for {required} indices, but the buffer holds " +
                $"{destination.Length}.",
                nameof(destination));
        }

        ReadOnlySpan<Point3d> vertices = mesh.Vertices;

        if (corners.Length == 3)
        {
            destination[0] = corners[0];
            destination[1] = corners[1];
            destination[2] = corners[2];
            return OperationResult.Success;
        }

        if (corners.Length == 4)
        {
            bool first = SplitsOnFirstDiagonal(
                vertices[corners[0]],
                vertices[corners[1]],
                vertices[corners[2]],
                vertices[corners[3]]);

            if (first)
            {
                destination[0] = corners[0];
                destination[1] = corners[1];
                destination[2] = corners[2];
                destination[3] = corners[0];
                destination[4] = corners[2];
                destination[5] = corners[3];
            }
            else
            {
                destination[0] = corners[0];
                destination[1] = corners[1];
                destination[2] = corners[3];
                destination[3] = corners[1];
                destination[4] = corners[2];
                destination[5] = corners[3];
            }

            return OperationResult.Success;
        }

        Point3d[] outline = new Point3d[corners.Length];

        for (int i = 0; i < corners.Length; i++)
        {
            outline[i] = vertices[corners[i]];
        }

        if (!MeshOps.TryFaceNormal(mesh, faceIndex, out Vector3d? normal) ||
            !PlaneOps.TryCreateFromNormal(MeshOps.FaceCenter(mesh, faceIndex), normal.Value, out Plane? plane))
        {
            FanCorners(corners, destination);
            return OperationResult.Partial(
                $"Face {faceIndex} has no usable normal, so it was fanned from its first corner instead of " +
                "triangulated.");
        }

        OperationResult result = TryTriangulate(outline, plane.Value, out int[]? local);

        if (!result.HasOutput || local is null)
        {
            FanCorners(corners, destination);
            return OperationResult.Partial(
                $"Face {faceIndex} could not be triangulated, so it was fanned from its first corner. " +
                result.Message);
        }

        // The clipper indexed into the local outline; translate back to the mesh's own vertex indices.
        for (int i = 0; i < local.Length; i++)
        {
            destination[i] = corners[local[i]];
        }

        return result;
    }

    private static void FanCorners(ReadOnlySpan<int> corners, Span<int> destination)
    {
        int write = 0;

        for (int i = 1; i < corners.Length - 1; i++)
        {
            destination[write++] = corners[0];
            destination[write++] = corners[i];
            destination[write++] = corners[i + 1];
        }
    }

    private static ReadOnlySpan<Point3d> CornersOf(Polyline polyline)
    {
        ReadOnlySpan<Point3d> points = polyline.Points;

        // A closed polyline repeats its first point; the traversal already implies the closing edge, so the
        // duplicate would only show up as a zero-area ear.
        if (points.Length >= 2 &&
            PointOps.EpsilonEquals(points[0], points[^1], Tolerance.Distance))
        {
            return points[..^1];
        }

        return points;
    }

    private static void Project(
        ReadOnlySpan<Point3d> points,
        in Plane plane,
        Span<double> u,
        Span<double> v)
    {
        Point3d origin = plane.Origin;
        Vector3d xAxis = plane.XAxis;
        Vector3d yAxis = plane.YAxis;

        for (int i = 0; i < points.Length; i++)
        {
            Vector3d offset = points[i] - origin;
            u[i] = VectorOps.Dot(offset, xAxis);
            v[i] = VectorOps.Dot(offset, yAxis);
        }
    }

    private static double SignedArea(ReadOnlySpan<double> u, ReadOnlySpan<double> v)
    {
        double twiceArea = 0;

        for (int i = 0, j = u.Length - 1; i < u.Length; j = i++)
        {
            twiceArea += (u[j] - u[i]) * (v[j] + v[i]);
        }

        return twiceArea * 0.5;
    }

    private static double SignedArea(ReadOnlySpan<int> loop, ReadOnlySpan<double> u, ReadOnlySpan<double> v)
    {
        double twiceArea = 0;

        for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
        {
            twiceArea += (u[loop[j]] - u[loop[i]]) * (v[loop[j]] + v[loop[i]]);
        }

        return twiceArea * 0.5;
    }

    private static void Reverse(Span<double> values) => values.Reverse();

    /// <summary>Whether any two non-adjacent edges of the closed traversal properly cross.</summary>
    private static bool Crosses(ReadOnlySpan<double> u, ReadOnlySpan<double> v)
    {
        int n = u.Length;

        for (int i = 0; i < n; i++)
        {
            int iNext = (i + 1) % n;

            for (int j = i + 1; j < n; j++)
            {
                int jNext = (j + 1) % n;

                // Edges sharing a corner always meet there; that is the outline being closed, not a crossing.
                if (iNext == j || jNext == i)
                {
                    continue;
                }

                if (SegmentsCross(
                        u[i], v[i], u[iNext], v[iNext],
                        u[j], v[j], u[jNext], v[jNext]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether two segments cross properly, each passing from one side of the other to the other side.
    /// </summary>
    /// <remarks>
    /// Every comparison is strict, so segments that touch at a point or lie along each other come back
    /// <see langword="false"/>. That is the conservative direction: those cases are degenerate rather than
    /// impossible, and the clipper copes with them.
    /// </remarks>
    private static bool SegmentsCross(
        double a1u, double a1v, double a2u, double a2v,
        double b1u, double b1v, double b2u, double b2v)
    {
        double d1 = Cross(b1u, b1v, b2u, b2v, a1u, a1v);
        double d2 = Cross(b1u, b1v, b2u, b2v, a2u, a2v);
        double d3 = Cross(a1u, a1v, a2u, a2v, b1u, b1v);
        double d4 = Cross(a1u, a1v, a2u, a2v, b2u, b2v);

        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
            ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static List<int> BuildOriented(
        int count,
        ReadOnlySpan<double> u,
        ReadOnlySpan<double> v,
        bool counterClockwise)
    {
        int[] loop = new int[count];

        for (int i = 0; i < count; i++)
        {
            loop[i] = i;
        }

        return [.. OrientLoop(loop, u, v, counterClockwise)];
    }

    private static int[] OrientLoop(
        int[] loop,
        ReadOnlySpan<double> u,
        ReadOnlySpan<double> v,
        bool counterClockwise)
    {
        bool isCounterClockwise = SignedArea(loop, u, v) > 0;

        if (isCounterClockwise == counterClockwise)
        {
            return loop;
        }

        int[] flipped = new int[loop.Length];

        for (int i = 0; i < loop.Length; i++)
        {
            flipped[i] = loop[loop.Length - 1 - i];
        }

        return flipped;
    }

    private static double MaxU(int[] loop, ReadOnlySpan<double> u)
    {
        double max = double.NegativeInfinity;

        foreach (int index in loop)
        {
            if (u[index] > max)
            {
                max = u[index];
            }
        }

        return max;
    }

    /// <summary>
    /// Splices a hole into the outline, walking out to it and back along a pair of coincident edges.
    /// </summary>
    /// <remarks>
    /// The hole's rightmost corner casts a ray to the right; whichever outline edge it hits first supplies
    /// the corner to bridge to. That corner then gets refined, because the nearest edge is not always
    /// visible from the hole — a spur of the outline can stand between them, and bridging across it would
    /// produce a traversal that crosses itself and a triangulation that spills outside the region.
    /// </remarks>
    private static bool TryBridgeHole(
        List<int> loop,
        int[] holeLoop,
        ReadOnlySpan<double> u,
        ReadOnlySpan<double> v)
    {
        int holeStart = 0;

        for (int i = 1; i < holeLoop.Length; i++)
        {
            if (u[holeLoop[i]] > u[holeLoop[holeStart]] ||
                (u[holeLoop[i]] == u[holeLoop[holeStart]] && v[holeLoop[i]] > v[holeLoop[holeStart]]))
            {
                holeStart = i;
            }
        }

        double holeU = u[holeLoop[holeStart]];
        double holeV = v[holeLoop[holeStart]];

        int bridge = -1;
        double bridgeHitU = double.PositiveInfinity;

        for (int i = 0; i < loop.Count; i++)
        {
            int a = loop[i];
            int b = loop[(i + 1) % loop.Count];

            // Half-open in v, so a corner sitting exactly at the ray's height is counted by one of its two
            // edges and never by both.
            if (v[a] > holeV == v[b] > holeV)
            {
                continue;
            }

            double hitU = u[a] + ((holeV - v[a]) * (u[b] - u[a]) / (v[b] - v[a]));

            // Equality is allowed on purpose: a hole whose rightmost corner sits exactly on the outline is
            // touching it, and bridging along that contact is right. Rejecting it would drop the hole.
            if (hitU < holeU || hitU >= bridgeHitU)
            {
                continue;
            }

            bridgeHitU = hitU;
            bridge = u[a] > u[b] ? i : (i + 1) % loop.Count;
        }

        if (bridge < 0)
        {
            return false;
        }

        bridge = RefineBridge(loop, bridge, holeU, holeV, bridgeHitU, u, v);

        List<int> spliced = new(loop.Count + holeLoop.Length + 2);

        for (int i = 0; i <= bridge; i++)
        {
            spliced.Add(loop[i]);
        }

        for (int k = 0; k < holeLoop.Length; k++)
        {
            spliced.Add(holeLoop[(holeStart + k) % holeLoop.Length]);
        }

        // Back the way we came: the hole's entry corner and the outline's bridge corner appear a second
        // time, which is what turns two loops into one without moving any point.
        spliced.Add(holeLoop[holeStart]);
        spliced.Add(loop[bridge]);

        for (int i = bridge + 1; i < loop.Count; i++)
        {
            spliced.Add(loop[i]);
        }

        loop.Clear();
        loop.AddRange(spliced);
        return true;
    }

    /// <summary>
    /// Picks the outline corner actually visible from the hole, among those inside the ray's triangle.
    /// </summary>
    /// <remarks>
    /// Only reflex corners can block the view, and of the candidates the one at the shallowest angle to the
    /// ray is the one nothing else hides. Ties go to the corner further right, which is the one nearer the
    /// hole along the ray.
    /// </remarks>
    private static int RefineBridge(
        List<int> loop,
        int bridge,
        double holeU,
        double holeV,
        double hitU,
        ReadOnlySpan<double> u,
        ReadOnlySpan<double> v)
    {
        int best = bridge;
        double bestTangent = double.PositiveInfinity;

        double apexU = u[loop[bridge]];
        double apexV = v[loop[bridge]];

        for (int i = 0; i < loop.Count; i++)
        {
            if (i == bridge)
            {
                continue;
            }

            int index = loop[i];
            double candidateU = u[index];
            double candidateV = v[index];

            if (candidateU <= holeU)
            {
                continue;
            }

            if (!IsReflex(loop, i, u, v))
            {
                continue;
            }

            if (!PointInTriangle(
                    candidateU, candidateV,
                    holeU, holeV,
                    hitU, holeV,
                    apexU, apexV))
            {
                continue;
            }

            // How far off the ray the candidate sits, per unit of distance along it.
            double tangent = Math.Abs(candidateV - holeV) / (candidateU - holeU);

            if (tangent < bestTangent ||
                (tangent == bestTangent && candidateU > u[loop[best]]))
            {
                best = i;
                bestTangent = tangent;
            }
        }

        return best;
    }

    private static bool IsReflex(
        List<int> loop,
        int position,
        ReadOnlySpan<double> u,
        ReadOnlySpan<double> v)
    {
        int previous = loop[(position - 1 + loop.Count) % loop.Count];
        int current = loop[position];
        int next = loop[(position + 1) % loop.Count];

        return Cross(
            u[previous], v[previous],
            u[current], v[current],
            u[next], v[next]) <= 0;
    }

    private static OperationResult ClipEars(
        ReadOnlySpan<int> loop,
        ReadOnlySpan<double> u,
        ReadOnlySpan<double> v,
        Span<int> destination,
        out int trianglesWritten)
    {
        int n = loop.Length;
        trianglesWritten = 0;

        int[] previous = new int[n];
        int[] next = new int[n];

        for (int i = 0; i < n; i++)
        {
            previous[i] = (i - 1 + n) % n;
            next[i] = (i + 1) % n;
        }

        int alive = n;
        int position = 0;
        int failures = 0;
        int write = 0;

        while (alive > 3)
        {
            int a = previous[position];
            int c = next[position];

            if (IsEar(loop, u, v, previous, next, a, position, c))
            {
                destination[write++] = loop[a];
                destination[write++] = loop[position];
                destination[write++] = loop[c];
                trianglesWritten++;

                next[a] = c;
                previous[c] = a;
                alive--;

                // Clipping changes the shape around the gap, so a neighbour that was not an ear a moment
                // ago may be one now. Stepping back rather than restarting is what keeps this quadratic
                // instead of cubic on ordinary outlines.
                position = a;
                failures = 0;
                continue;
            }

            position = c;
            failures++;

            if (failures >= alive)
            {
                break;
            }
        }

        if (alive == 3)
        {
            // The last three corners are a triangle whether or not they pass the ear test; if they are
            // collinear it has zero area, which is the honest result for an outline that contained a
            // straight run of corners.
            destination[write++] = loop[previous[position]];
            destination[write++] = loop[position];
            destination[write++] = loop[next[position]];
            trianglesWritten++;

            return OperationResult.Success;
        }

        // Stalled. Fanning what is left keeps the triangle count at the promised n - 2 so a caller's buffer
        // is still filled, and the status says the geometry is not to be trusted.
        int stalled = alive;
        int start = position;
        int previousCorner = next[start];

        for (int i = 0; i < stalled - 2; i++)
        {
            int corner = next[previousCorner];

            destination[write++] = loop[start];
            destination[write++] = loop[previousCorner];
            destination[write++] = loop[corner];
            trianglesWritten++;

            previousCorner = corner;
        }

        return OperationResult.Partial(
            $"Ear clipping stalled with {stalled} of {n} corners left, so the remainder was fanned. The " +
            "outline crosses itself, doubles back on itself, or does not lie in the plane given.");
    }

    private static bool IsEar(
        ReadOnlySpan<int> loop,
        ReadOnlySpan<double> u,
        ReadOnlySpan<double> v,
        int[] previous,
        int[] next,
        int a,
        int b,
        int c)
    {
        double au = u[a], av = v[a];
        double bu = u[b], bv = v[b];
        double cu = u[c], cv = v[c];

        // Reflex and collinear corners are not ears. Collinear ones get their chance later, once a
        // neighbour has been clipped and the corner is genuinely convex.
        if (Cross(au, av, bu, bv, cu, cv) <= 0)
        {
            return false;
        }

        for (int j = next[c]; j != a; j = next[j])
        {
            // A bridge repeats a corner, so two positions can hold the same vertex. Those are the same
            // point, not an intruder, and treating them as one is what lets a bridged outline clip at all.
            if (loop[j] == loop[a] || loop[j] == loop[b] || loop[j] == loop[c])
            {
                continue;
            }

            // Only a reflex corner can sit inside an ear; a convex one would drag its neighbours in with it.
            if (Cross(
                    u[previous[j]], v[previous[j]],
                    u[j], v[j],
                    u[next[j]], v[next[j]]) > 0)
            {
                continue;
            }

            if (PointInTriangle(u[j], v[j], au, av, bu, bv, cu, cv))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Twice the signed area of the corner, positive when it turns left and is therefore convex in a
    /// counter-clockwise outline.
    /// </summary>
    private static double Cross(
        double au, double av,
        double bu, double bv,
        double cu, double cv) =>
        ((bu - au) * (cv - av)) - ((cu - au) * (bv - av));

    /// <summary>
    /// Whether a point falls inside a counter-clockwise triangle, counting its edges as inside.
    /// </summary>
    /// <remarks>
    /// Inclusive on purpose: a corner sitting exactly on an ear's edge would leave the two triangles
    /// overlapping along it, so it has to block the ear rather than be ignored.
    /// </remarks>
    private static bool PointInTriangle(
        double pu, double pv,
        double au, double av,
        double bu, double bv,
        double cu, double cv) =>
        Cross(au, av, bu, bv, pu, pv) >= 0 &&
        Cross(bu, bv, cu, cv, pu, pv) >= 0 &&
        Cross(cu, cv, au, av, pu, pv) >= 0;
}
