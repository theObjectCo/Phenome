using System.Drawing;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Phenome.Apps.GrasshopperLink;

/// <summary>
/// Lays the document out the way a diagram renderer would - and lays out groups as whole blocks.
/// </summary>
/// <remarks>
/// The mermaid recipe, sized for a canvas, but applied to a hierarchy rather than a flat graph: a group is
/// one box in the layout, its members are laid out inside it, and the boxes themselves are layered and
/// ordered by the same rules. That hierarchy is the whole difference between a readable canvas and the
/// tangle a flat layout produces - laying out members individually interleaves the members of different
/// groups, and once interleaved, the frames <em>must</em> overlap however carefully anything is spaced.
/// <para>
/// Within a level: layers by longest path from the sources (so nothing stands left of what feeds it),
/// barycenter sweeps to untangle, and sizes from the objects' real bounds. Groups get padding for their
/// frame and the label above it, and mothers are pushed to the very back afterwards.
/// </para>
/// </remarks>
internal static class Arrange
{
    private const float NodeGapX = 60;
    private const float NodeGapY = 18;
    private const float BlockGapX = 130;
    private const float BlockGapY = 80;
    private const float GroupPad = 26;
    private const float GroupLabel = 26;
    private const int Sweeps = 4;

    /// <summary>One box in the layout: a single object, or a group with boxes of its own inside.</summary>
    private sealed class Block
    {
        internal IGH_DocumentObject? Node;
        internal GH_Group? Group;
        internal List<Block> Children = [];
        internal List<IGH_DocumentObject> Leaves = [];
        internal SizeF Size;
        internal PointF At;
    }

    /// <summary>Arranges the whole document. Returns how many objects moved.</summary>
    internal static int Whole(GH_Document document)
    {
        List<IGH_DocumentObject> nodes = [.. document.Objects
            .Where(thing => thing is IGH_Component or IGH_Param && thing.Attributes is not null)];

        if (nodes.Count == 0)
        {
            return 0;
        }

        List<GH_Group> groups = [.. document.Objects.OfType<GH_Group>()];

        Dictionary<Guid, IGH_DocumentObject> nodeById = [];
        Dictionary<Guid, GH_Group> groupById = [];

        foreach (IGH_DocumentObject thing in nodes)
        {
            nodeById[thing.InstanceGuid] = thing;
        }

        foreach (GH_Group group in groups)
        {
            groupById[group.InstanceGuid] = group;
        }

        // Who owns whom. Grasshopper allows an object in several groups; the first claim wins, because a
        // layout must put each object in exactly one place.
        Dictionary<Guid, GH_Group> owner = [];

        foreach (GH_Group group in groups)
        {
            foreach (Guid member in group.ObjectIDs)
            {
                if (!owner.ContainsKey(member) && !ReferenceEquals(groupById.GetValueOrDefault(member), group))
                {
                    owner[member] = group;
                }
            }
        }

        Dictionary<IGH_DocumentObject, List<IGH_DocumentObject>> upstream = Upstream(nodes, nodeById);

        // The blocks: every unowned group is a root box, every unowned object is a root box of its own.
        Dictionary<Guid, Block> blockOfGroup = [];
        List<Block> roots = [];

        foreach (GH_Group group in groups)
        {
            if (!owner.ContainsKey(group.InstanceGuid))
            {
                roots.Add(BlockFor(group, groupById, nodeById, blockOfGroup));
            }
        }

        foreach (IGH_DocumentObject thing in nodes)
        {
            if (!owner.ContainsKey(thing.InstanceGuid))
            {
                roots.Add(new Block { Node = thing, Leaves = [thing], Size = thing.Attributes!.Bounds.Size });
            }
        }

        foreach (Block root in roots)
        {
            Measure(root, upstream);
        }

        LayoutLevel(roots, upstream, BlockGapX, BlockGapY);

        // Anchored at the old top-left, so a tidy-up does not also teleport the canvas.
        PointF origin = nodes
            .Select(thing => thing.Attributes!.Bounds.Location)
            .Aggregate((kept, next) => new PointF(Math.Min(kept.X, next.X), Math.Min(kept.Y, next.Y)));

        int moved = 0;

        foreach (Block root in roots)
        {
            moved += Apply(document, root, origin.X, origin.Y);
        }

        Restack(document, groups, groupById);

        return moved;
    }

    private static Block BlockFor(
        GH_Group group,
        Dictionary<Guid, GH_Group> groupById,
        Dictionary<Guid, IGH_DocumentObject> nodeById,
        Dictionary<Guid, Block> made)
    {
        if (made.TryGetValue(group.InstanceGuid, out Block? already))
        {
            return already;
        }

        Block block = new() { Group = group };

        made[group.InstanceGuid] = block;

        foreach (Guid member in group.ObjectIDs)
        {
            if (groupById.TryGetValue(member, out GH_Group? child) && !ReferenceEquals(child, group))
            {
                block.Children.Add(BlockFor(child, groupById, nodeById, made));
            }
            else if (nodeById.TryGetValue(member, out IGH_DocumentObject? node))
            {
                block.Children.Add(new Block { Node = node, Leaves = [node], Size = node.Attributes!.Bounds.Size });
            }
        }

        return block;
    }

    /// <summary>A block's size, from the inside out: children laid out first, then the frame around them.</summary>
    private static void Measure(Block block, Dictionary<IGH_DocumentObject, List<IGH_DocumentObject>> upstream)
    {
        if (block.Node is { } node)
        {
            block.Size = node.Attributes!.Bounds.Size;
            block.Leaves = [node];
            return;
        }

        foreach (Block child in block.Children)
        {
            Measure(child, upstream);
        }

        block.Leaves = [.. block.Children.SelectMany(child => child.Leaves)];

        bool nested = block.Children.Any(child => child.Group is not null);
        SizeF inner = LayoutLevel(
            block.Children,
            upstream,
            nested ? BlockGapX : NodeGapX,
            nested ? BlockGapY : NodeGapY);

        block.Size = new SizeF(
            inner.Width + (2 * GroupPad),
            inner.Height + (2 * GroupPad) + GroupLabel);
    }

    /// <summary>One level of boxes: layered left to right, untangled, stacked. Returns the space used.</summary>
    private static SizeF LayoutLevel(
        List<Block> blocks,
        Dictionary<IGH_DocumentObject, List<IGH_DocumentObject>> upstream,
        float gapX,
        float gapY)
    {
        if (blocks.Count == 0)
        {
            return SizeF.Empty;
        }

        Dictionary<IGH_DocumentObject, int> owner = [];

        for (int i = 0; i < blocks.Count; i++)
        {
            foreach (IGH_DocumentObject leaf in blocks[i].Leaves)
            {
                owner[leaf] = i;
            }
        }

        List<int>[] feeders = new List<int>[blocks.Count];

        for (int i = 0; i < blocks.Count; i++)
        {
            feeders[i] = [];

            foreach (IGH_DocumentObject leaf in blocks[i].Leaves)
            {
                foreach (IGH_DocumentObject from in upstream.GetValueOrDefault(leaf) ?? [])
                {
                    if (owner.TryGetValue(from, out int j) && j != i && !feeders[i].Contains(j))
                    {
                        feeders[i].Add(j);
                    }
                }
            }
        }

        int[] layer = new int[blocks.Count];
        bool[] settled = new bool[blocks.Count];
        bool[] walking = new bool[blocks.Count];

        int LayerOf(int at)
        {
            if (settled[at] || walking[at])
            {
                return layer[at];
            }

            walking[at] = true;

            int deepest = -1;

            foreach (int feeder in feeders[at])
            {
                deepest = Math.Max(deepest, LayerOf(feeder));
            }

            walking[at] = false;
            settled[at] = true;

            return layer[at] = deepest + 1;
        }

        for (int i = 0; i < blocks.Count; i++)
        {
            LayerOf(i);
        }

        int layers = layer.Max() + 1;
        List<int>[] columns = new List<int>[layers];

        for (int i = 0; i < layers; i++)
        {
            columns[i] = [];
        }

        for (int i = 0; i < blocks.Count; i++)
        {
            columns[layer[i]].Add(i);
        }

        double[] rank = new double[blocks.Count];

        for (int sweep = 0; sweep < Sweeps; sweep++)
        {
            foreach (List<int> column in columns)
            {
                for (int i = 0; i < column.Count; i++)
                {
                    int block = column[i];

                    // Feeders whose column no longer knows them (a cycle again) rank as themselves rather
                    // than as -1, which would drag a whole column to the top for no reason.
                    rank[block] = feeders[block].Count == 0
                        ? i
                        : feeders[block].Average(feeder =>
                        {
                            int at = columns[layer[feeder]].IndexOf(feeder);

                            return at < 0 ? i : at;
                        });
                }

                column.Sort((a, b) => rank[a].CompareTo(rank[b]));
            }
        }

        float x = 0;
        float tallest = 0;

        foreach (List<int> column in columns)
        {
            // An empty column is possible and used to crash the whole verb with "Sequence contains no
            // elements": two groups can feed each other through different members, and a cycle in the
            // block graph leaves a layer number with nobody standing on it.
            if (column.Count == 0)
            {
                continue;
            }

            float widest = column.Max(block => blocks[block].Size.Width);
            float y = 0;

            foreach (int block in column)
            {
                blocks[block].At = new PointF(x, y);
                y += blocks[block].Size.Height + gapY;
            }

            tallest = Math.Max(tallest, y - gapY);
            x += widest + gapX;
        }

        return new SizeF(Math.Max(0, x - gapX), tallest);
    }

    /// <summary>Relative positions become real pivots, a block and its contents at a time.</summary>
    private static int Apply(GH_Document document, Block block, float dx, float dy)
    {
        float x = block.At.X + dx;
        float y = block.At.Y + dy;

        if (block.Node is { } node)
        {
            RectangleF bounds = node.Attributes!.Bounds;
            PointF pivot = node.Attributes.Pivot;

            document.UndoUtil.RecordGenericObjectEvent("Phenome Link: arrange", node);

            // The pivot sits at its own offset inside the bounds; keeping that offset lands the object's
            // top-left exactly where the layout said.
            node.Attributes.Pivot = new PointF(
                x + (pivot.X - bounds.X),
                y + (pivot.Y - bounds.Y));

            node.Attributes.ExpireLayout();

            return 1;
        }

        int moved = 0;

        foreach (Block child in block.Children)
        {
            moved += Apply(document, child, x + GroupPad, y + GroupPad + GroupLabel);
        }

        return moved;
    }

    /// <summary>Groups behind their contents, mothers behind their children, every frame recomputed.</summary>
    private static void Restack(GH_Document document, List<GH_Group> groups, Dictionary<Guid, GH_Group> groupById)
    {
        // Laid out here and now rather than at the next repaint: a group's frame is derived from its
        // members' bounds and cached, so until something performs the layout, both the human's canvas and
        // anything reading /canvas would be told the old frames - and conclude the groups overlap.
        foreach (IGH_DocumentObject thing in document.Objects)
        {
            if (thing is not GH_Group && thing.Attributes is { } attributes)
            {
                attributes.ExpireLayout();
                attributes.PerformLayout();
            }
        }

        foreach (GH_Group group in groups)
        {
            group.ExpireCaches();

            if (group.Attributes is { } frame)
            {
                frame.ExpireLayout();
                frame.PerformLayout();
            }
        }

        foreach (GH_Group group in groups.Where(group => !IsMother(group, groupById)))
        {
            document.ArrangeObject(group, GH_Arrange.MoveToBack);
        }

        // Mothers last, so they end up furthest back of all.
        foreach (GH_Group group in groups.Where(group => IsMother(group, groupById)))
        {
            document.ArrangeObject(group, GH_Arrange.MoveToBack);
        }
    }

    private static bool IsMother(GH_Group group, Dictionary<Guid, GH_Group> groupById) =>
        group.ObjectIDs.Any(member => groupById.ContainsKey(member) && member != group.InstanceGuid);

    /// <summary>Who feeds whom, resolved to top-level objects.</summary>
    private static Dictionary<IGH_DocumentObject, List<IGH_DocumentObject>> Upstream(
        List<IGH_DocumentObject> nodes,
        Dictionary<Guid, IGH_DocumentObject> nodeById)
    {
        Dictionary<IGH_DocumentObject, List<IGH_DocumentObject>> upstream = [];

        foreach (IGH_DocumentObject thing in nodes)
        {
            List<IGH_DocumentObject> feeders = [];

            foreach (IGH_Param input in InputsOf(thing))
            {
                foreach (IGH_Param source in input.Sources)
                {
                    IGH_DocumentObject from = source.Attributes?.GetTopLevel?.DocObject ?? source;

                    if (!ReferenceEquals(from, thing)
                        && nodeById.ContainsKey(from.InstanceGuid)
                        && !feeders.Contains(from))
                    {
                        feeders.Add(from);
                    }
                }
            }

            upstream[thing] = feeders;
        }

        return upstream;
    }

    internal static IEnumerable<IGH_Param> InputsOf(IGH_DocumentObject thing) => thing switch
    {
        IGH_Component component => component.Params.Input,
        IGH_Param parameter => [parameter],
        _ => [],
    };
}
