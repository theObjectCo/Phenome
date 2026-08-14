using System.Diagnostics.CodeAnalysis;

namespace Phenome.Geometry.Modules;

// The surface builders that work from several profiles at once, or from a profile and a rail. Both are
// the same idea — stitch a run of rings together — and both spend most of their length deciding what the
// rings should be rather than stitching them.
public static partial class MeshBuilders
{
    /// <summary>
    /// A surface stretched through a run of cross-sections.
    /// </summary>
    /// <remarks>
    /// Every section must have the same number of points, and corresponding points are joined in order. That
    /// is a real constraint rather than an oversight: matching up sections of different lengths means
    /// resampling them, and how to resample — by length, by corner, by curvature — is a decision the caller
    /// is far better placed to make than this function is. Build the sections to match, or resample them
    /// with <see cref="PolylineOps.TryDivideByCount"/> first.
    /// <para>
    /// Sections are joined in the order given, so the direction each one is wound in matters: two sections
    /// wound opposite ways produce a twist through the middle rather than a tube.
    /// </para>
    /// </remarks>
    /// <param name="sections">The cross-sections, in order; at least two.</param>
    /// <param name="closedLoop">
    /// Whether to join the last section back to the first, making a ring of sections rather than a run.
    /// </param>
    /// <param name="capped">
    /// Whether to close the first and last sections. Ignored when <paramref name="closedLoop"/> is set,
    /// since there is no end, and when the sections are open.
    /// </param>
    /// <param name="mesh">The result, or <see langword="null"/> when the call failed outright.</param>
    /// <returns>
    /// <see cref="ResultStatus.Partial"/> when capping was asked for but is not possible;
    /// <see cref="ResultStatus.Failed"/> when there are too few sections or their point counts disagree.
    /// </returns>
    public static OperationResult CreateLoft(
        IReadOnlyList<Polyline> sections,
        bool closedLoop,
        bool capped,
        out Mesh? mesh)
    {
        mesh = null;

        if (sections.Count < 2)
        {
            return OperationResult.Failed(
                $"A loft needs at least two sections, but {sections.Count} were given.");
        }

        bool sectionsClosed = false;
        Point3d[][] rings = new Point3d[sections.Count][];

        for (int s = 0; s < sections.Count; s++)
        {
            Polyline section = sections[s];

            if (!PolylineOps.IsValid(section))
            {
                return OperationResult.Failed($"Section {s} has fewer than two finite points.");
            }

            bool closed = PolylineOps.IsClosed(section);

            if (s == 0)
            {
                sectionsClosed = closed;
            }
            else if (closed != sectionsClosed)
            {
                return OperationResult.Failed(
                    $"Section {s} is {(closed ? "closed" : "open")} but section 0 is " +
                    $"{(sectionsClosed ? "closed" : "open")}; a loft cannot mix the two.");
            }

            rings[s] = Corners(section, closed);

            if (rings[s].Length != rings[0].Length)
            {
                return OperationResult.Failed(
                    $"Section {s} has {rings[s].Length} points but section 0 has {rings[0].Length}. Every " +
                    "section must match, because corresponding points are what get joined.");
            }
        }

        int pointsPerSection = rings[0].Length;

        mesh = new Mesh();
        mesh.Reserve(
            pointsPerSection * sections.Count,
            pointsPerSection * sections.Count * 4,
            pointsPerSection * sections.Count);

        int[][] added = new int[sections.Count][];

        for (int s = 0; s < sections.Count; s++)
        {
            added[s] = AddRing(mesh, rings[s]);
        }

        int spans = closedLoop ? sections.Count : sections.Count - 1;

        for (int s = 0; s < spans; s++)
        {
            StitchRings(mesh, added[s], added[(s + 1) % sections.Count], sectionsClosed);
        }

        if (!capped || closedLoop)
        {
            return OperationResult.Success;
        }

        if (!sectionsClosed || pointsPerSection < 3)
        {
            return OperationResult.Partial(
                "The sections are open, so the loft has open edges along its length and could not be capped.");
        }

        AddCap(mesh, added[0], reversed: true);
        AddCap(mesh, added[^1], reversed: false);

        return OperationResult.Success;
    }

    /// <summary>
    /// A profile carried along a rail, staying square to it the whole way.
    /// </summary>
    /// <remarks>
    /// One section is placed at every rail point. At an interior point the section sits on the bisector of
    /// the two segments meeting there, which is exactly a mitre — so a rectangular profile round a
    /// right-angled rail produces the joint a fabricator would cut, with no overlap and no gap.
    /// <para>
    /// The profile is rotated as little as possible from one section to the next, by the double-reflection
    /// method, rather than being rebuilt against a fixed up direction. That matters because a fixed up
    /// direction fails outright wherever the rail runs parallel to it, and introduces a spurious twist long
    /// before it does. The one thing it cannot promise is that a closed rail comes back to its starting
    /// rotation: a rail that leaves its own plane accumulates a twist that is a property of the curve, not
    /// of the method.
    /// </para>
    /// <para>
    /// The profile is not scaled, so a rail that turns tighter than the profile is wide will fold the
    /// surface through itself. That is not detected.
    /// </para>
    /// </remarks>
    /// <param name="profile">The cross-section to carry.</param>
    /// <param name="profilePlane">
    /// The frame the profile is drawn in. Its normal is what gets aligned to the rail, and its origin is
    /// what rides along it — so an off-centre origin gives an off-centre sweep, on purpose.
    /// </param>
    /// <param name="rail">The path to follow.</param>
    /// <param name="capped">Whether to close the two ends. Ignored for an open profile or a closed rail.</param>
    /// <param name="mesh">The result, or <see langword="null"/> when the call failed outright.</param>
    /// <returns>
    /// <see cref="ResultStatus.Partial"/> when capping was asked for but is not possible;
    /// <see cref="ResultStatus.Failed"/> when the profile, its plane, or the rail is unusable.
    /// </returns>
    public static OperationResult CreateSweep(
        Polyline profile,
        in Plane profilePlane,
        Polyline rail,
        bool capped,
        out Mesh? mesh)
    {
        mesh = null;

        if (!PolylineOps.IsValid(profile))
        {
            return OperationResult.Failed("The profile needs at least two finite points to sweep.");
        }

        if (!PlaneOps.IsValid(profilePlane))
        {
            return OperationResult.Failed("The profile plane is invalid.");
        }

        if (!PolylineOps.IsValid(rail))
        {
            return OperationResult.Failed("The rail needs at least two finite points to sweep along.");
        }

        bool railClosed = PolylineOps.IsClosed(rail);
        Point3d[] path = Corners(rail, railClosed);

        if (path.Length < 2)
        {
            return OperationResult.Failed("The rail collapses to a single point.");
        }

        if (!TryBuildFrames(path, railClosed, out Plane[]? frames))
        {
            return OperationResult.Failed(
                "The rail doubles back on itself somewhere, so no frame can be built there.");
        }

        bool profileClosed = PolylineOps.IsClosed(profile);
        Point3d[] corners = Corners(profile, profileClosed);

        mesh = new Mesh();
        mesh.Reserve(corners.Length * frames.Length, corners.Length * frames.Length * 4, corners.Length * frames.Length);

        int[][] rings = new int[frames.Length][];
        Point3d[] placed = new Point3d[corners.Length];

        for (int f = 0; f < frames.Length; f++)
        {
            TMatrix toFrame = Transforms.PlaneToPlane(profilePlane, frames[f]);

            for (int i = 0; i < corners.Length; i++)
            {
                placed[i] = PointOps.Transform(corners[i], toFrame);
            }

            rings[f] = AddRing(mesh, placed);
        }

        int spans = railClosed ? frames.Length : frames.Length - 1;

        for (int f = 0; f < spans; f++)
        {
            StitchRings(mesh, rings[f], rings[(f + 1) % frames.Length], profileClosed);
        }

        if (!capped || railClosed)
        {
            return OperationResult.Success;
        }

        if (!profileClosed || corners.Length < 3)
        {
            return OperationResult.Partial(
                "The profile is open, so the sweep has open edges along its length and could not be capped.");
        }

        AddCap(mesh, rings[0], reversed: true);
        AddCap(mesh, rings[^1], reversed: false);

        return OperationResult.Success;
    }

    /// <summary>
    /// Builds a frame at every rail point, turning as little as possible from one to the next.
    /// </summary>
    /// <remarks>
    /// The double-reflection method: carry the reference direction across by reflecting it twice, once in
    /// the plane bisecting the step and once in the plane bisecting the two tangents. Two reflections are a
    /// rotation, and it is the smallest one that takes the old tangent onto the new — which is the property
    /// that keeps the section from twisting for no reason.
    /// </remarks>
    private static bool TryBuildFrames(
        Point3d[] path,
        bool closed,
        [NotNullWhen(true)] out Plane[]? frames)
    {
        frames = null;
        int n = path.Length;
        Vector3d[] tangents = new Vector3d[n];

        for (int i = 0; i < n; i++)
        {
            Vector3d incoming = i == 0
                ? (closed ? path[0] - path[n - 1] : path[1] - path[0])
                : path[i] - path[i - 1];

            Vector3d outgoing = i == n - 1
                ? (closed ? path[0] - path[n - 1] : path[n - 1] - path[n - 2])
                : path[i + 1] - path[i];

            // The bisector of the two segments is the mitre plane's normal; on a straight run it is just the
            // direction, so there is no special case to write.
            if (!VectorOps.TryNormalize(incoming, out Vector3d? unitIn) ||
                !VectorOps.TryNormalize(outgoing, out Vector3d? unitOut) ||
                !VectorOps.TryNormalize(unitIn.Value + unitOut.Value, out Vector3d? bisector))
            {
                return false;
            }

            tangents[i] = bisector.Value;
        }

        if (!VectorOps.TryPerpendicularTo(tangents[0], out Vector3d? start))
        {
            return false;
        }

        Vector3d[] reference = new Vector3d[n];
        reference[0] = start.Value;

        for (int i = 0; i < n - 1; i++)
        {
            Vector3d step = path[i + 1] - path[i];
            double stepLengthSquared = VectorOps.LengthSquared(step);

            if (stepLengthSquared <= Tolerance.ZeroSquared)
            {
                reference[i + 1] = reference[i];
                continue;
            }

            Vector3d reflectedReference =
                reference[i] - (step * (2 * VectorOps.Dot(step, reference[i]) / stepLengthSquared));

            Vector3d reflectedTangent =
                tangents[i] - (step * (2 * VectorOps.Dot(step, tangents[i]) / stepLengthSquared));

            Vector3d second = tangents[i + 1] - reflectedTangent;
            double secondLengthSquared = VectorOps.LengthSquared(second);

            reference[i + 1] = secondLengthSquared <= Tolerance.ZeroSquared
                ? reflectedReference
                : reflectedReference - (second * (2 * VectorOps.Dot(second, reflectedReference) / secondLengthSquared));
        }

        frames = new Plane[n];

        for (int i = 0; i < n; i++)
        {
            if (!PlaneOps.TryCreateFromAxes(
                    path[i],
                    reference[i],
                    VectorOps.Cross(tangents[i], reference[i]),
                    out Plane? frame))
            {
                return false;
            }

            frames[i] = frame.Value;
        }

        return true;
    }
}
