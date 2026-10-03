using System.Net;
using System.Text;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;

using static Phenome.Apps.GrasshopperLink.Bridge.Verbs.Plumbing;

namespace Phenome.Apps.GrasshopperLink.Bridge.Verbs;

/// <summary>Lengths, areas and volumes of the geometry on a parameter, and how two sets of it compare.</summary>
/// <remarks>
/// <c>peek</c> reports what the data is and not its size. Without this verb, geometry arithmetic takes throwaway
/// script components. This verb gives those results directly: curve length, overlap area between closed
/// profiles, and distance between curves.
/// <para>
/// The verb is read-only, like <c>peek</c>. Nothing is added to the canvas; RhinoCommon computes on the data the
/// parameter already holds.
/// </para>
/// </remarks>
internal static class Measure
{
    /// <summary>
    /// More items than this are counted and summed but not listed one by one.
    /// </summary>
    private const int Listed = 200;

    /// <summary>
    /// Maximum pairs compared for <c>against</c>. Each boolean intersection runs on the UI thread; 2,500
    /// already takes seconds on a large layout.
    /// </summary>
    private const int Pairs = 2500;

    internal static string Answer(HttpListenerRequest request)
    {
        Guid id = Guid.Parse(request.QueryString["id"] ?? throw new ArgumentException("measure needs ?id=guid."));
        string? against = request.QueryString["against"];

        return OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            double tolerance = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

            IGH_Param first = Find(document, id, request.QueryString["side"], request.QueryString["param"]);
            List<Item> these = Items(first);

            StringBuilder json = new("{\"ok\":true");

            json.Append(",\"tolerance\":").Append(Json.Number(tolerance));
            Write(these, json);

            if (against is null)
            {
                return json.Append('}').ToString();
            }

            IGH_Param second = Find(
                document,
                Guid.Parse(against),
                request.QueryString["againstSide"],
                request.QueryString["againstParam"]);

            // Comparing a set with itself checks each pair once and never an item against itself, answering
            // "do any of these profiles overlap".
            bool itself = ReferenceEquals(first, second);
            List<Item> those = itself ? these : Items(second);

            Between(these, those, itself, tolerance, json);

            return json.Append('}').ToString();
        });
    }

    /// <summary>One item of geometry, with where it sat in the tree.</summary>
    private sealed record Item(string At, object? Shape, string Kind);

    private static IGH_Param Find(GH_Document document, Guid id, string? side, string? param)
    {
        IGH_DocumentObject thing = document.FindObject(id, topLevelOnly: true)
            ?? throw new KeyNotFoundException($"No object {id} on the canvas.");

        // Output by default, unlike peek: measure what a component produced.
        return LocateBy(thing, side ?? "output", param);
    }

    private static List<Item> Items(IGH_Param parameter)
    {
        List<Item> items = [];

        foreach (Grasshopper.Kernel.Data.GH_Path path in parameter.VolatileData.Paths)
        {
            System.Collections.IList branch = parameter.VolatileData.get_Branch(path);

            for (int index = 0; index < branch.Count; index++)
            {
                object? shape = Shape(branch[index] as IGH_Goo);

                items.Add(new Item($"{path}[{index}]", shape, Kind(shape, branch[index] as IGH_Goo)));
            }
        }

        return items;
    }

    /// <summary>The geometry inside a goo, in the few forms this verb knows how to measure.</summary>
    private static object? Shape(IGH_Goo? goo)
    {
        switch (goo)
        {
            case null:
                return null;
            case GH_Point point:
                return point.Value;
        }

        GeometryBase? geometry = GH_Convert.ToGeometryBase(goo);

        return geometry switch
        {
            Curve curve => curve,
            Brep brep => brep,
            Mesh mesh => mesh,
            Extrusion extrusion => extrusion.ToBrep(),
            Surface surface => surface.ToBrep(),
            SubD subd => subd.ToBrep(SubDToBrepOptions.Default),
            _ => null,
        };
    }

    private static string Kind(object? shape, IGH_Goo? goo) => shape switch
    {
        Point3d => "point",
        Curve => "curve",
        Brep => "brep",
        Mesh => "mesh",
        _ => goo?.TypeName ?? "null",
    };

    /// <summary>Each item's own sizes, then the totals.</summary>
    private static void Write(List<Item> items, StringBuilder json)
    {
        double length = 0;
        double area = 0;
        double volume = 0;
        int measured = 0;
        BoundingBox all = BoundingBox.Empty;

        json.Append(",\"count\":").Append(Json.Number(items.Count)).Append(",\"items\":[");

        for (int at = 0; at < items.Count; at++)
        {
            Item item = items[at];
            StringBuilder one = new();

            one.Append("{\"at\":").Append(Json.Quote(item.At));
            one.Append(",\"kind\":").Append(Json.Quote(item.Kind));

            switch (item.Shape)
            {
                case Point3d point:
                    one.Append(",\"point\":").Append(Triple(point));
                    all.Union(point);
                    measured++;
                    break;

                case Curve curve:
                {
                    double own = curve.GetLength();

                    length += own;
                    one.Append(",\"length\":").Append(Json.Number(Round(own)));
                    one.Append(",\"closed\":").Append(curve.IsClosed ? "true" : "false");

                    // Only a closed planar curve encloses an area; a closed space curve has none to give.
                    if (curve.IsClosed && curve.IsPlanar() && AreaMassProperties.Compute(curve) is { } inside)
                    {
                        area += inside.Area;
                        one.Append(",\"area\":").Append(Json.Number(Round(inside.Area)));
                    }

                    all.Union(curve.GetBoundingBox(true));
                    measured++;
                    break;
                }

                case Brep brep:
                {
                    double own = brep.GetArea();

                    area += own;
                    one.Append(",\"area\":").Append(Json.Number(Round(own)));

                    if (brep.IsSolid)
                    {
                        double held = brep.GetVolume();

                        volume += held;
                        one.Append(",\"volume\":").Append(Json.Number(Round(held)));
                    }

                    all.Union(brep.GetBoundingBox(true));
                    measured++;
                    break;
                }

                case Mesh mesh:
                {
                    double own = AreaMassProperties.Compute(mesh)?.Area ?? 0;

                    area += own;
                    one.Append(",\"area\":").Append(Json.Number(Round(own)));

                    if (mesh.IsClosed && VolumeMassProperties.Compute(mesh) is { } held)
                    {
                        volume += held.Volume;
                        one.Append(",\"volume\":").Append(Json.Number(Round(held.Volume)));
                    }

                    all.Union(mesh.GetBoundingBox(true));
                    measured++;
                    break;
                }
            }

            if (at < Listed)
            {
                json.Append(at == 0 ? "" : ",").Append(one).Append('}');
            }
        }

        json.Append(']');

        if (items.Count > Listed)
        {
            json.Append(",\"listed\":").Append(Json.Number(Listed));
        }

        json.Append(",\"total\":{\"measured\":").Append(Json.Number(measured));
        json.Append(",\"length\":").Append(Json.Number(Round(length)));
        json.Append(",\"area\":").Append(Json.Number(Round(area)));
        json.Append(",\"volume\":").Append(Json.Number(Round(volume)));

        if (all.IsValid)
        {
            json.Append(",\"box\":{\"min\":").Append(Triple(all.Min)).Append(",\"max\":").Append(Triple(all.Max)).Append('}');
        }

        json.Append('}');
    }

    /// <summary>
    /// Every pair from the two sets: the area or volume they share, and the nearest distance between them.
    /// </summary>
    /// <remarks>
    /// Two closed planar curves overlap by the area of their boolean intersection; two solids by the volume of
    /// theirs. Distance is computed for curves and points, which is what layouts are drawn from. A pair whose
    /// bounding boxes are more than the tolerance apart cannot overlap, and the boolean is skipped for it.
    /// </remarks>
    private static void Between(List<Item> these, List<Item> those, bool itself, double tolerance, StringBuilder json)
    {
        long count = itself
            ? (long)these.Count * (these.Count - 1) / 2
            : (long)these.Count * those.Count;

        if (count > Pairs)
        {
            throw new ArgumentException(
                $"That is {count} pairs, and measure compares at most {Pairs} in one call because each "
                + "one runs on Rhino's UI thread. Measure a branch at a time, or a smaller selection.");
        }

        double shared = 0;
        int overlapping = 0;
        double nearest = double.PositiveInfinity;
        (string, string)? closest = null;
        StringBuilder listed = new();
        int written = 0;

        for (int a = 0; a < these.Count; a++)
        {
            for (int b = itself ? a + 1 : 0; b < those.Count; b++)
            {
                object? one = these[a].Shape;
                object? other = those[b].Shape;

                if (Distance(one, other) is { } apart && apart < nearest)
                {
                    nearest = apart;
                    closest = (these[a].At, those[b].At);
                }

                double overlap = Overlap(one, other, tolerance);

                if (overlap <= tolerance * tolerance)
                {
                    continue;
                }

                shared += overlap;
                overlapping++;

                if (written++ < Listed)
                {
                    listed.Append(written == 1 ? "" : ",");
                    listed.Append("{\"a\":").Append(Json.Quote(these[a].At));
                    listed.Append(",\"b\":").Append(Json.Quote(those[b].At));
                    listed.Append(",\"overlap\":").Append(Json.Number(Round(overlap))).Append('}');
                }
            }
        }

        json.Append(",\"against\":{\"count\":").Append(Json.Number(those.Count));
        json.Append(",\"pairs\":").Append(Json.Number(count));
        json.Append(",\"overlapping\":").Append(Json.Number(overlapping));
        json.Append(",\"overlap\":").Append(Json.Number(Round(shared)));

        if (closest is { } pair)
        {
            json.Append(",\"nearest\":{\"distance\":").Append(Json.Number(Round(nearest)));
            json.Append(",\"a\":").Append(Json.Quote(pair.Item1));
            json.Append(",\"b\":").Append(Json.Quote(pair.Item2)).Append('}');
        }

        json.Append(",\"overlaps\":[").Append(listed).Append("]}");
    }

    /// <summary>The area two closed planar curves share, or the volume two solids do; 0 for anything else.</summary>
    private static double Overlap(object? one, object? other, double tolerance)
    {
        switch (one, other)
        {
            case (Curve a, Curve b) when a.IsClosed && b.IsClosed && a.IsPlanar() && b.IsPlanar():
            {
                if (Apart(a.GetBoundingBox(true), b.GetBoundingBox(true), tolerance))
                {
                    return 0;
                }

                Curve[] common = Curve.CreateBooleanIntersection(a, b, tolerance);

                return common.Sum(piece => AreaMassProperties.Compute(piece)?.Area ?? 0);
            }

            case (Brep a, Brep b) when a.IsSolid && b.IsSolid:
            {
                if (Apart(a.GetBoundingBox(true), b.GetBoundingBox(true), tolerance))
                {
                    return 0;
                }

                Brep[] common = Brep.CreateBooleanIntersection(a, b, tolerance) ?? [];

                return common.Sum(piece => piece.GetVolume());
            }

            default:
                return 0;
        }
    }

    /// <summary>The nearest distance between two curves or points, or null for anything else.</summary>
    private static double? Distance(object? one, object? other)
    {
        switch (one, other)
        {
            case (Point3d a, Point3d b):
                return a.DistanceTo(b);

            case (Curve a, Curve b):
                return a.ClosestPoints(b, out Point3d onA, out Point3d onB) ? onA.DistanceTo(onB) : null;

            case (Curve a, Point3d b):
                return a.ClosestPoint(b, out double t) ? a.PointAt(t).DistanceTo(b) : null;

            case (Point3d a, Curve b):
                return b.ClosestPoint(a, out double s) ? b.PointAt(s).DistanceTo(a) : null;

            default:
                return null;
        }
    }

    private static bool Apart(BoundingBox a, BoundingBox b, double tolerance) =>
        a.Max.X < b.Min.X - tolerance || b.Max.X < a.Min.X - tolerance
        || a.Max.Y < b.Min.Y - tolerance || b.Max.Y < a.Min.Y - tolerance
        || a.Max.Z < b.Min.Z - tolerance || b.Max.Z < a.Min.Z - tolerance;

    /// <summary>Nine significant figures are enough to compare two variants and short enough to read.</summary>
    private static double Round(double value) =>
        value == 0 || double.IsNaN(value) || double.IsInfinity(value)
            ? value
            : Math.Round(value, Math.Clamp(8 - (int)Math.Floor(Math.Log10(Math.Abs(value))), 0, 15));

    private static string Triple(Point3d point) =>
        $"[{Json.Number(Round(point.X))},{Json.Number(Round(point.Y))},{Json.Number(Round(point.Z))}]";
}
