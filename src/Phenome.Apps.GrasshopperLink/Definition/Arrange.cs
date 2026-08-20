using System.Drawing;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Phenome.Apps.GrasshopperLink.Definition;

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

        /// <summary>
        /// What this block is, for ordering two of them that the dataflow cannot separate.
        /// </summary>
        /// <remarks>
        /// An instance guid, because it is the only identity here that no layout pass rewrites: position is the
        /// thing being decided, and document order is rewritten by the restacking at the end.
        /// </remarks>
        internal Guid Key => Group?.InstanceGuid ?? Node?.InstanceGuid ?? Guid.Empty;
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

        // Where everything was, before any of it is touched. Two things need this: the count at the end, which
        // should say how many objects ended up somewhere else rather than how many were written to, and the
        // correction below, which cannot be measured until the layout has been applied once.
        Dictionary<IGH_DocumentObject, PointF> before = [];

        foreach (IGH_DocumentObject thing in document.Objects)
        {
            if (thing.Attributes is { } attributes)
            {
                before[thing] = attributes.Pivot;
            }
        }

        foreach (Block root in roots)
        {
            Apply(document, root, origin.X, origin.Y);
        }

        Captions(document, groups);

        // And now the correction that makes running this twice mean the same as running it once.
        //
        // The anchor above is the top-left of where the objects *were*, but the layout does not put its first
        // object at its own top-left: inside a group it is inset by the frame's padding and the room the label
        // needs. So the result sat down and to the right of the anchor by that inset, the next run took the new
        // positions as its anchor and added the inset again, and the whole definition walked across the canvas
        // a group's padding at a time - measured at 26 by 52 pixels per run, for ever.
        //
        // It only bit when the top-left-most object was inside a group, which is why arrange looked idempotent
        // when tested on loose objects and was not. Translating the finished layout back onto the anchor fixes
        // it whatever the inset happens to be, without the layout needing to know about padding at all.
        // Measured on pivots rather than on bounds, and that is not a detail: Attributes.Bounds is computed
        // during a layout pass and cached, so reading it straight after writing a pivot gives the position the
        // object used to have. The first attempt at this correction measured bounds, found no difference
        // because it was comparing an old number with itself, and the drift carried on exactly as before.
        // A pivot is the thing that was just written, so it is the thing that can be read back.
        PointF anchor = nodes
            .Select(thing => before[thing])
            .Aggregate((kept, next) => new PointF(Math.Min(kept.X, next.X), Math.Min(kept.Y, next.Y)));

        PointF landed = nodes
            .Select(thing => thing.Attributes!.Pivot)
            .Aggregate((kept, next) => new PointF(Math.Min(kept.X, next.X), Math.Min(kept.Y, next.Y)));

        PointF drift = new(anchor.X - landed.X, anchor.Y - landed.Y);

        if (Math.Abs(drift.X) > 0.5f || Math.Abs(drift.Y) > 0.5f)
        {
            foreach (IGH_DocumentObject thing in document.Objects)
            {
                if (thing is GH_Group || thing.Attributes is not { } attributes)
                {
                    continue;
                }

                attributes.Pivot = new PointF(attributes.Pivot.X + drift.X, attributes.Pivot.Y + drift.Y);
                attributes.ExpireLayout();
                attributes.PerformLayout();
            }
        }

        // Counted from where things ended up against where they started, which is the only measure a caller can
        // check: a settled document answers zero however much was written on the way there.
        int moved = 0;

        foreach ((IGH_DocumentObject thing, PointF was) in before)
        {
            if (thing.Attributes is { } now
                && (Math.Abs(now.Pivot.X - was.X) > 0.5f || Math.Abs(now.Pivot.Y - was.Y) > 0.5f))
            {
                moved++;
            }
        }

        Restack(document, groups, groupById);

        return moved;
    }

    /// <summary>
    /// Notes, put where they belong once everything else has a place.
    /// </summary>
    /// <remarks>
    /// A note is not a node: it carries no data, has no ports and takes part in no dataflow, so it has no
    /// business in the layout algebra above - which is why it was excluded from it, and why it then sat wherever
    /// it happened to be created while every component moved out from under it. That is how a scribble ends up
    /// across a group's sliders, reported from the field in those words.
    /// <para>
    /// A pass afterwards instead, which cannot disturb a layout that has already been decided. The rule needs no
    /// new field on anything: <b>a note's group is what it is about</b>. In a group, it is that group's caption
    /// and goes above the group's other members; in no group, it is about the document and goes above the whole
    /// thing. An agent already says which by passing <c>group</c> to <c>place</c>, and <c>describe</c> already
    /// reports it back.
    /// </para>
    /// <para>
    /// Measured from the members rather than from the frame, deliberately. A note that belongs to a group is one
    /// of its members, so the frame is drawn around the note as well - reading the frame to decide where to put
    /// the note would be a loop, and each run would push it further out. Measuring the members that are not
    /// notes is stable, which is what makes running arrange three times give the same answer three times.
    /// </para>
    /// </remarks>
    private static int Captions(GH_Document document, List<GH_Group> groups)
    {
        int moved = 0;
        HashSet<Guid> spoken = [];

        // The highest line any caption was written on. Tracked as it goes rather than measured afterwards,
        // because a caption's Bounds does not move until the next layout pass - so asking the canvas where the
        // captions ended up would answer where they used to be, and the document's own notes would be stacked
        // straight on top of them. Measured once, that is exactly what happened.
        float ceiling = float.MaxValue;

        foreach (GH_Group group in groups)
        {
            List<IGH_DocumentObject> notes = [];
            PointF corner = new(float.MaxValue, float.MaxValue);
            bool any = false;

            foreach (Guid member in group.ObjectIDs)
            {
                if (document.FindObject(member, topLevelOnly: true) is not { Attributes: { } attributes } thing)
                {
                    continue;
                }

                if (IsNote(thing))
                {
                    notes.Add(thing);
                    continue;
                }

                // Pivots, not bounds. Bounds is computed during a layout pass and cached, and the layout has
                // just moved every one of these - so reading bounds here answers where the members used to be,
                // the caption is placed against a body that has moved out from under it, and the next run puts
                // it somewhere else again. Measured: two captions swapping places on alternate runs. A pivot is
                // what the layout wrote, so it is the thing that can be read back.
                corner = new PointF(
                    Math.Min(corner.X, attributes.Pivot.X),
                    Math.Min(corner.Y, attributes.Pivot.Y));

                any = true;
            }

            if (!any)
            {
                continue;
            }

            // Stacked upwards from just above the body, in the order the group holds them, so two captions do
            // not land on each other.
            float above = corner.Y - CaptionGap;

            foreach (IGH_DocumentObject note in notes)
            {
                if (!spoken.Add(note.InstanceGuid))
                {
                    continue;
                }

                above -= note.Attributes!.Bounds.Height;
                moved += Put(note, new PointF(corner.X, above));
                ceiling = Math.Min(ceiling, above);
                above -= CaptionGap / 2;
            }
        }

        // Whatever belongs to no group belongs to the document: a title, a credit, a warning to whoever opens
        // it. Above everything, which is the one place a reader looks first and no component ever wants.
        List<IGH_DocumentObject> loose = [.. document.Objects
            .Where(thing => IsNote(thing) && !spoken.Contains(thing.InstanceGuid) && thing.Attributes is not null)];

        if (loose.Count == 0)
        {
            return moved;
        }

        PointF everything = new(float.MaxValue, float.MaxValue);
        bool anything = false;

        foreach (IGH_DocumentObject thing in document.Objects)
        {
            if (IsNote(thing) || thing is GH_Group || thing.Attributes is not { } attributes)
            {
                continue;
            }

            everything = new PointF(
                Math.Min(everything.X, attributes.Pivot.X),
                Math.Min(everything.Y, attributes.Pivot.Y));

            anything = true;
        }

        if (!anything)
        {
            return moved;
        }

        // Above the captions as well as above the components, so a document's title does not land on a group's
        // caption. Both are notes and neither is in the layout, so nothing else would have kept them apart.
        float band = Math.Min(everything.Y, ceiling) - CaptionGap;

        foreach (IGH_DocumentObject note in loose)
        {
            band -= note.Attributes!.Bounds.Height;
            moved += Put(note, new PointF(everything.X, band));
            band -= CaptionGap / 2;
        }

        return moved;
    }

    /// <summary>Whether this is something to read rather than something to run.</summary>
    /// <remarks>
    /// A scribble always is. A panel only when nothing is wired to it either way: a panel in the middle of a
    /// definition is a probe on the data and belongs where the data is, while an unwired one is a caption.
    /// </remarks>
    private static bool IsNote(IGH_DocumentObject thing) =>
        thing is GH_Scribble
        || (thing is GH_Panel panel && panel.SourceCount == 0 && panel.Recipients.Count == 0);

    /// <summary>
    /// Moves a note's pivot to a point, counting it only when it was not already there.
    /// </summary>
    /// <remarks>
    /// In pivot space throughout, for the reason given where the body is measured: everything around this has
    /// just been moved, and bounds do not catch up until the next layout pass. A caption is placed relative to
    /// its group's pivots and written as a pivot, so nothing in the calculation depends on a number that is
    /// about to change.
    /// </remarks>
    private static int Put(IGH_DocumentObject note, PointF want)
    {
        PointF pivot = note.Attributes!.Pivot;

        if (Math.Abs(pivot.X - want.X) < 0.5f && Math.Abs(pivot.Y - want.Y) < 0.5f)
        {
            return 0;
        }

        note.Attributes.Pivot = want;
        note.Attributes.ExpireLayout();
        note.Attributes.PerformLayout();

        return 1;
    }

    /// <summary>How far a caption sits clear of what it describes.</summary>
    private const float CaptionGap = 24f;

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

                // Ranked first, and where two blocks rank the same, ordered by identity.
                //
                // Two groups with no wire between them rank identically for ever, and List.Sort is not stable,
                // so which came first was decided by the sort's internals and could differ between two runs on
                // the same document. Measured: two unconnected groups swapping places on alternate arranges,
                // for ever, each swap reported as seven objects moved.
                //
                // The tiebreak has to be something this pass does not itself change, which ruled out the first
                // attempt: the block's index comes from the order of document.Objects, and Restack reorders that
                // very list by calling ArrangeObject to push frames back and notes forward. Sorting by a key
                // that arrange rewrites on every run is no tiebreak at all - it swapped exactly as before.
                // An instance guid is fixed for an object's life and no pass touches it.
                column.Sort((a, b) =>
                {
                    int byRank = rank[a].CompareTo(rank[b]);

                    return byRank != 0 ? byRank : blocks[a].Key.CompareTo(blocks[b].Key);
                });
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

            // The pivot sits at its own offset inside the bounds; keeping that offset lands the object's
            // top-left exactly where the layout said.
            PointF want = new(
                x + (pivot.X - bounds.X),
                y + (pivot.Y - bounds.Y));

            // An object already where the layout wants it is not moved, and saying otherwise costs twice:
            // the answer's count stops meaning anything on a settled document, and every rerun pushes an
            // undo step per object that undoes nothing. Arranging twice is a normal thing to do - it is the
            // finishing move - so the second run should report nothing and record nothing.
            //
            // Half a pixel, not equality: the layout is deterministic from the same inputs, but bounds come
            // from text measurement, and half a pixel is below anything a canvas can show anyway.
            if (Math.Abs(pivot.X - want.X) < 0.5f && Math.Abs(pivot.Y - want.Y) < 0.5f)
            {
                return 0;
            }

            document.UndoUtil.RecordGenericObjectEvent("Phenome Link: arrange", node);

            node.Attributes.Pivot = want;

            // Expire *and* recompute, rather than expiring and hoping. Bounds is worked out during a layout
            // pass and cached, so between a pivot being written and the next pass, Bounds and Pivot disagree -
            // and the sum three lines up converts between exactly those two. One write per object per arrange
            // hid it, because Grasshopper repainted in between; anything that moves an object twice in one pass
            // reads a stale offset the second time and lands the object somewhere else again. That is how two
            // groups came to swap places on alternate runs. Recomputing here costs a layout per moved object
            // and removes the whole class of fault.
            node.Attributes.ExpireLayout();
            node.Attributes.PerformLayout();

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

        // And notes to the very front, which is the other half of putting them where they belong. A group's
        // frame is a tinted rectangle drawn over whatever is behind it, so a caption sitting underneath one is
        // washed out and a caption underneath a component is not there at all. A note is the one thing on a
        // canvas whose entire purpose is to be read, so it is the one thing that should never be behind
        // anything. Depth is as much a part of "where it goes" as the coordinates are.
        foreach (IGH_DocumentObject note in document.Objects.Where(IsNote).ToList())
        {
            document.ArrangeObject(note, GH_Arrange.MoveToFront);
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
