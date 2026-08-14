using System.Diagnostics.CodeAnalysis;

namespace Phenome.Geometry.Modules;

// The surface builders that work from one profile: a flat region, a profile dragged along a line, a
// profile spun about an axis. The machinery they are built from is in MeshBuilders.Rings.cs.
public static partial class MeshBuilders
{
    /// <summary>
    /// A flat mesh filling a planar outline, with holes cut out of it.
    /// </summary>
    /// <remarks>
    /// The only builder here that produces triangles rather than n-gons, because a region with a hole in it
    /// is not one face — there is no way to write a ring as a single closed corner list. Regions without
    /// holes come back as triangles too, for consistency; use <see cref="CreateExtrusion"/> with no depth if a
    /// single n-gon face is what you want.
    /// </remarks>
    /// <param name="outer">The boundary.</param>
    /// <param name="holes">The openings. May be empty.</param>
    /// <param name="plane">
    /// The plane to work in; the mesh faces along its normal. Flip the plane to face the other way.
    /// </param>
    /// <param name="mesh">The region, or <see langword="null"/> when the call failed outright.</param>
    /// <returns>As <see cref="Triangulation.TryTriangulateRegion"/>.</returns>
    public static OperationResult CreatePlanarRegion(
        Polyline outer,
        IReadOnlyList<Polyline> holes,
        in Plane plane,
        out Mesh? mesh)
    {
        mesh = null;

        OperationResult result = Triangulation.TryTriangulateRegion(
            outer,
            holes,
            plane,
            out Point3d[]? vertices,
            out int[]? triangles);

        if (!result.HasOutput || vertices is null || triangles is null)
        {
            return result;
        }

        mesh = new Mesh();
        mesh.Reserve(vertices.Length, triangles.Length, triangles.Length / 3);
        mesh.AddVertices(vertices);

        for (int i = 0; i < triangles.Length; i += 3)
        {
            mesh.AddFace(triangles[i], triangles[i + 1], triangles[i + 2]);
        }

        return result;
    }

    /// <summary>
    /// A profile dragged along a straight direction.
    /// </summary>
    /// <remarks>
    /// A closed profile gives a solid; an open one gives a ribbon with two open edges. The sides come out as
    /// quads, one per profile segment, and the caps as single n-gon faces — the mesh keeps n-gons, so there
    /// is no reason to split a flat cap here when <see cref="RenderBuffers"/> can do it at the last moment.
    /// <para>
    /// A closed profile is turned counter-clockwise about <paramref name="direction"/> before building, so
    /// the result faces outwards however the profile was drawn. That is worth relying on: it means a profile
    /// coming out of an offset, a fillet or a boolean does not have to be inspected first.
    /// </para>
    /// <para>
    /// A cap with a hole in it is not this function's job — extrude the outer profile and the hole profile
    /// separately, or build the caps with <see cref="CreatePlanarRegion"/>.
    /// </para>
    /// </remarks>
    /// <param name="profile">The outline to drag.</param>
    /// <param name="direction">How far and which way to drag it.</param>
    /// <param name="capped">
    /// Whether to close the two ends. Ignored for an open profile, which has no end to close.
    /// </param>
    /// <param name="mesh">The result, or <see langword="null"/> when the call failed outright.</param>
    /// <returns>
    /// <see cref="ResultStatus.Partial"/> when caps were asked for but the profile is open;
    /// <see cref="ResultStatus.Failed"/> when the profile or the direction is unusable.
    /// </returns>
    public static OperationResult CreateExtrusion(
        Polyline profile,
        Vector3d direction,
        bool capped,
        out Mesh? mesh)
    {
        mesh = null;

        if (!PolylineOps.IsValid(profile))
        {
            return OperationResult.Failed(
                "The profile needs at least two finite points to extrude.");
        }

        if (VectorOps.IsZero(direction) || !VectorOps.IsValid(direction))
        {
            return OperationResult.Failed(
                "The extrusion direction is zero or invalid, so the result would have no depth.");
        }

        bool closed = PolylineOps.IsClosed(profile);
        Point3d[] corners = Corners(profile, closed);

        if (corners.Length < 2)
        {
            return OperationResult.Failed("The profile collapses to a single point.");
        }

        if (closed)
        {
            // Face outwards regardless of how the profile was drawn.
            OrientAbout(corners, direction, counterClockwise: true);
        }

        Point3d[] moved = Moved(corners, direction);

        mesh = new Mesh();
        mesh.Reserve(corners.Length * 2, corners.Length * 4, corners.Length + 2);

        int[] bottom = AddRing(mesh, corners);
        int[] top = AddRing(mesh, moved);

        StitchRings(mesh, bottom, top, closed);

        if (!capped || !closed || corners.Length < 3)
        {
            return capped && !closed
                ? OperationResult.Partial(
                    "The profile is open, so the extrusion has two open edges and could not be capped.")
                : OperationResult.Success;
        }

        AddCap(mesh, bottom, reversed: true);
        AddCap(mesh, top, reversed: false);

        return OperationResult.Success;
    }

    /// <summary>
    /// A planar region with holes in it, dragged along a straight direction.
    /// </summary>
    /// <remarks>
    /// What <see cref="CreateExtrusion"/> cannot do, and says so: its caps are single n-gon faces, and no closed
    /// corner list describes a face with a hole in it. So the walls here are quads as usual — the outer ring
    /// plus one per hole — and the two caps are triangulated regions.
    /// <para>
    /// The outer boundary is turned counter-clockwise about <paramref name="direction"/> and every hole the
    /// other way, so the result faces outwards however the profiles were drawn. Outwards for a hole means
    /// facing into it, which is the same statement: away from the material.
    /// </para>
    /// <para>
    /// Walls and caps share their vertices, so the edges are creases over shared vertices exactly as in
    /// <see cref="CreateExtrusion"/> — run <see cref="MeshOps.SplitAtCreases"/> before computing normals or the
    /// corners round themselves off.
    /// </para>
    /// <para>
    /// The restrictions are <see cref="Triangulation.TryTriangulateRegion"/>'s: a hole nested inside another
    /// hole is neither supported nor detected, and a hole falling outside the boundary is skipped, reported,
    /// and keeps its walls.
    /// </para>
    /// </remarks>
    /// <param name="outer">The boundary; a repeated closing point is dropped.</param>
    /// <param name="holes">The openings, each closed the same way. May be empty.</param>
    /// <param name="plane">The plane the region is measured in; which way its normal points does not matter.</param>
    /// <param name="direction">How far and which way to drag it.</param>
    /// <param name="capped">Whether to close the two ends.</param>
    /// <param name="mesh">The result, or <see langword="null"/> when the call failed outright.</param>
    /// <returns>
    /// <see cref="ResultStatus.Partial"/> when a hole was skipped or a cap could not be triangulated;
    /// <see cref="ResultStatus.Failed"/> when the plane, the direction or the boundary is unusable.
    /// </returns>
    public static OperationResult CreateExtrusionWithHoles(
        Polyline outer,
        IReadOnlyList<Polyline> holes,
        in Plane plane,
        Vector3d direction,
        bool capped,
        out Mesh? mesh)
    {
        mesh = null;

        if (!PlaneOps.IsValid(plane))
        {
            return OperationResult.Failed("The plane is invalid, so the region cannot be projected.");
        }

        if (VectorOps.IsZero(direction) || !VectorOps.IsValid(direction))
        {
            return OperationResult.Failed(
                "The extrusion direction is zero or invalid, so the result would have no depth.");
        }

        // Depth measured along the plane's own normal. A direction lying in the plane would extrude the
        // region across itself: the walls would be flat, the caps coincident, and the volume nothing.
        if (Math.Abs(VectorOps.Dot(plane.Normal, direction)) <= Tolerance.Distance)
        {
            return OperationResult.Failed(
                "The direction lies in the plane, so the extrusion would be flat rather than solid.");
        }

        if (!PolylineOps.IsValid(outer))
        {
            return OperationResult.Failed("The boundary needs at least two finite points to extrude.");
        }

        Point3d[] outerCorners = RegionCorners(outer);

        if (outerCorners.Length < 3)
        {
            return OperationResult.Failed(
                $"The boundary needs at least three corners, but {outerCorners.Length} were given.");
        }

        if (Triangulation.SelfIntersects(outerCorners, plane))
        {
            return OperationResult.Failed(
                "The boundary crosses itself, so no region bounded by it exists.");
        }

        OrientAbout(outerCorners, direction, counterClockwise: true);

        List<Point3d[]> holeCorners = [];
        List<string> skipped = [];

        for (int h = 0; h < holes.Count; h++)
        {
            if (!PolylineOps.IsValid(holes[h]))
            {
                skipped.Add($"hole {h} has no usable points");
                continue;
            }

            Point3d[] corners = RegionCorners(holes[h]);

            if (corners.Length < 3)
            {
                skipped.Add($"hole {h} has only {corners.Length} corners");
                continue;
            }

            if (Triangulation.SelfIntersects(corners, plane))
            {
                skipped.Add($"hole {h} crosses itself");
                continue;
            }

            // The other way round from the boundary, so its wall faces into the hole.
            OrientAbout(corners, direction, counterClockwise: false);
            holeCorners.Add(corners);
        }

        int ringTotal = outerCorners.Length;

        foreach (Point3d[] corners in holeCorners)
        {
            ringTotal += corners.Length;
        }

        mesh = new Mesh();
        mesh.Reserve(ringTotal * 2, ringTotal * 4, ringTotal + 2);

        // The bottom ring goes in in the same order a region triangulation lays its vertices out — the
        // boundary, then each hole — so a region index *is* a mesh index and the top is that plus the ring
        // total. That is what lets the caps index into the walls' own vertices instead of duplicating them.
        int[] bottomOuter = AddRing(mesh, outerCorners);
        int[][] bottomHoles = new int[holeCorners.Count][];

        for (int h = 0; h < holeCorners.Count; h++)
        {
            bottomHoles[h] = AddRing(mesh, holeCorners[h]);
        }

        int[] topOuter = AddRing(mesh, Moved(outerCorners, direction));
        int[][] topHoles = new int[holeCorners.Count][];

        for (int h = 0; h < holeCorners.Count; h++)
        {
            topHoles[h] = AddRing(mesh, Moved(holeCorners[h], direction));
        }

        StitchRings(mesh, bottomOuter, topOuter, closedRing: true);

        for (int h = 0; h < holeCorners.Count; h++)
        {
            StitchRings(mesh, bottomHoles[h], topHoles[h], closedRing: true);
        }

        if (!capped)
        {
            return skipped.Count == 0
                ? OperationResult.Success
                : OperationResult.Partial(SkippedNote(skipped));
        }

        // Triangulated about a normal that agrees with the direction, so the triangles come out facing the
        // way the top cap faces and the bottom cap is the same list read backwards.
        Plane capPlane = VectorOps.Dot(plane.Normal, direction) < 0 ? PlaneOps.Flipped(plane) : plane;

        Polyline[] holeProfiles = new Polyline[holeCorners.Count];

        for (int h = 0; h < holeCorners.Count; h++)
        {
            holeProfiles[h] = PolylineOps.Create(holeCorners[h]);
        }

        OperationResult clipped = Triangulation.TryTriangulateRegion(
            PolylineOps.Create(outerCorners),
            holeProfiles,
            capPlane,
            out _,
            out int[]? triangles);

        if (!clipped.HasOutput || triangles is null)
        {
            skipped.Add($"the caps could not be triangulated ({clipped.Message})");
            return OperationResult.Partial(SkippedNote(skipped));
        }

        for (int i = 0; i < triangles.Length; i += 3)
        {
            mesh.AddFace(
                triangles[i] + ringTotal,
                triangles[i + 1] + ringTotal,
                triangles[i + 2] + ringTotal);

            mesh.AddFace(triangles[i + 2], triangles[i + 1], triangles[i]);
        }

        if (skipped.Count == 0)
        {
            return clipped;
        }

        return clipped.IsSuccess
            ? OperationResult.Partial(SkippedNote(skipped))
            : OperationResult.Partial($"{clipped.Message} {SkippedNote(skipped)}");
    }

    /// <summary>
    /// A profile spun about an axis.
    /// </summary>
    /// <remarks>
    /// Profile points lying on the axis are added once and shared by every ring, so the quads that would
    /// collapse there come out as triangles instead of as slivers of zero width. That is what makes a dome
    /// or a cone come out clean rather than with a ring of degenerate faces at the pole.
    /// <para>
    /// Which way the surface faces follows from the profile's direction and the sweep's together; there is no
    /// outward side to normalise towards, because a lathed profile need not enclose anything. Use
    /// <see cref="MeshOps.Flip"/> if it comes out inside-out.
    /// </para>
    /// <para>
    /// Capping closes what can be closed with one flat face each: for a full turn, the circles traced by an
    /// open profile's endpoints, provided they are off the axis; for a partial turn, the profile itself at
    /// each end, provided it is closed. A partial turn of an open profile has a single non-planar boundary
    /// and cannot be capped at all.
    /// </para>
    /// </remarks>
    /// <param name="profile">The outline to spin.</param>
    /// <param name="axis">The axis to spin about; only its direction and position matter, not its length.</param>
    /// <param name="angleDomain">The angles in radians to sweep. Decreasing spins the other way.</param>
    /// <param name="segments">How many steps to divide the sweep into; at least one.</param>
    /// <param name="capped">Whether to close the ends.</param>
    /// <param name="mesh">The result, or <see langword="null"/> when the call failed outright.</param>
    /// <returns>
    /// <see cref="ResultStatus.Partial"/> when capping was asked for and part of it was not possible;
    /// <see cref="ResultStatus.Failed"/> when the profile, axis, domain or segment count is unusable.
    /// </returns>
    public static OperationResult CreateRevolution(
        Polyline profile,
        Line axis,
        Interval angleDomain,
        int segments,
        bool capped,
        out Mesh? mesh)
    {
        mesh = null;

        if (!PolylineOps.IsValid(profile))
        {
            return OperationResult.Failed("The profile needs at least two finite points to revolve.");
        }

        if (!LineOps.TryUnitDirection(axis, out Vector3d? axisDirection))
        {
            return OperationResult.Failed("The axis is degenerate, so there is nothing to spin about.");
        }

        if (!IntervalOps.IsValid(angleDomain) ||
            Math.Abs(IntervalOps.Length(angleDomain)) <= Tolerance.Angle)
        {
            return OperationResult.Failed("The angle domain is unset or sweeps nothing.");
        }

        if (segments < 1)
        {
            return OperationResult.Failed($"A revolve needs at least one segment, not {segments}.");
        }

        bool profileClosed = PolylineOps.IsClosed(profile);
        Point3d[] corners = Corners(profile, profileClosed);

        if (corners.Length < 2)
        {
            return OperationResult.Failed("The profile collapses to a single point.");
        }

        double sweep = IntervalOps.Length(angleDomain);
        bool fullTurn = Math.Abs(Math.Abs(sweep) - Math.Tau) <= Tolerance.Angle;

        mesh = new Mesh();

        // A point on the axis does not move, so it gets one vertex shared by every ring. The stitching then
        // sees a repeated index and emits a triangle rather than a collapsed quad.
        int[] shared = new int[corners.Length];
        bool[] onAxis = new bool[corners.Length];

        for (int i = 0; i < corners.Length; i++)
        {
            onAxis[i] = LineOps.DistanceTo(axis, corners[i]) <= Tolerance.Distance;
            shared[i] = onAxis[i] ? mesh.AddVertex(corners[i]) : -1;
        }

        // A full turn ends where it started, so the last ring is the first one and there is no seam.
        int ringCount = fullTurn ? segments : segments + 1;
        int[][] rings = new int[ringCount][];

        for (int step = 0; step < ringCount; step++)
        {
            double angle = IntervalOps.ParameterAt(angleDomain, (double)step / segments);
            TMatrix rotation = Transforms.Rotate(axis.From, axisDirection.Value, angle);

            int[] ring = new int[corners.Length];

            for (int i = 0; i < corners.Length; i++)
            {
                ring[i] = onAxis[i]
                    ? shared[i]
                    : mesh.AddVertex(PointOps.Transform(corners[i], rotation));
            }

            rings[step] = ring;
        }

        for (int step = 0; step < ringCount; step++)
        {
            int nextStep = (step + 1) % ringCount;

            if (!fullTurn && nextStep == 0)
            {
                break;
            }

            StitchRings(mesh, rings[step], rings[nextStep], profileClosed);
        }

        if (!capped)
        {
            return OperationResult.Success;
        }

        return AddRevolveCaps(mesh, rings, onAxis, fullTurn, profileClosed, corners.Length);
    }

    /// <summary>Every corner moved by the same vector.</summary>
    private static Point3d[] Moved(ReadOnlySpan<Point3d> corners, Vector3d direction)
    {
        Point3d[] moved = new Point3d[corners.Length];

        for (int i = 0; i < corners.Length; i++)
        {
            moved[i] = corners[i] + direction;
        }

        return moved;
    }

    /// <summary>
    /// The corners of a closed region's boundary, however the caller happened to close it.
    /// </summary>
    /// <remarks>
    /// A region's boundary is closed whether or not the polyline says so, and every repeat of the first point
    /// goes rather than only the last one. The stricter version matters here: the region triangulator drops a
    /// closing point too, and a boundary it counted differently would leave the caps indexing into the wrong
    /// vertices.
    /// </remarks>
    private static Point3d[] RegionCorners(Polyline boundary)
    {
        ReadOnlySpan<Point3d> points = boundary.Points;

        while (points.Length >= 2 &&
            PointOps.EpsilonEquals(points[0], points[^1], Tolerance.Distance))
        {
            points = points[..^1];
        }

        return points.ToArray();
    }

    /// <summary>Turns a closed run of corners the given way round about a direction, in place.</summary>
    private static void OrientAbout(Point3d[] corners, Vector3d direction, bool counterClockwise)
    {
        if (corners.Length < 3 ||
            !PlaneOps.TryCreateFromNormal(corners[0], direction, out Plane? reference))
        {
            return;
        }

        if (SignedAreaAbout(corners, reference.Value) > 0 != counterClockwise)
        {
            Array.Reverse(corners);
        }
    }

    private static string SkippedNote(List<string> skipped) =>
        $"Left out {skipped.Count} thing(s): {string.Join("; ", skipped)}.";

    private static double SignedAreaAbout(ReadOnlySpan<Point3d> corners, in Plane plane)
    {
        double twiceArea = 0;

        for (int i = 0, j = corners.Length - 1; i < corners.Length; j = i++)
        {
            (double uj, double vj) = PlaneOps.ClosestParameter(plane, corners[j]);
            (double ui, double vi) = PlaneOps.ClosestParameter(plane, corners[i]);
            twiceArea += (uj * vi) - (ui * vj);
        }

        return twiceArea * 0.5;
    }

    private static OperationResult AddRevolveCaps(
        Mesh mesh,
        int[][] rings,
        ReadOnlySpan<bool> onAxis,
        bool fullTurn,
        bool profileClosed,
        int profilePoints)
    {
        if (!fullTurn)
        {
            if (!profileClosed || profilePoints < 3)
            {
                return OperationResult.Partial(
                    "A partial revolve of an open profile has one non-planar boundary, so it cannot be " +
                    "capped with flat faces.");
            }

            AddCap(mesh, rings[0], reversed: true);
            AddCap(mesh, rings[^1], reversed: false);
            return OperationResult.Success;
        }

        if (profileClosed)
        {
            // A full turn of a closed profile is already closed: a torus has no ends.
            return OperationResult.Success;
        }

        // The open profile's two endpoints each trace a circle. Off the axis that circle is a flat convex
        // ring and closes with one face; on the axis it is a point and needs nothing.
        List<string> notes = [];

        AddEndCircle(mesh, rings, 0, onAxis[0], reversed: true, notes);
        AddEndCircle(mesh, rings, profilePoints - 1, onAxis[profilePoints - 1], reversed: false, notes);

        return notes.Count == 0 ? OperationResult.Success : OperationResult.Partial(string.Join(" ", notes));
    }

    private static void AddEndCircle(
        Mesh mesh,
        int[][] rings,
        int profilePoint,
        bool onAxis,
        bool reversed,
        List<string> notes)
    {
        if (onAxis)
        {
            return;
        }

        if (rings.Length < 3)
        {
            notes.Add(
                $"The circle traced by profile point {profilePoint} has only {rings.Length} segments, too " +
                "few to close with a face.");
            return;
        }

        int[] circle = new int[rings.Length];

        for (int step = 0; step < rings.Length; step++)
        {
            circle[step] = rings[step][profilePoint];
        }

        AddCap(mesh, circle, reversed);
    }
}
