using System.Drawing;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Phenome.Apps.GrasshopperLink.Definition;

/// <summary>
/// Lays the document out like a diagram renderer, treating each group as one block.
/// </summary>
/// <remarks>
/// The mermaid layout, sized for a canvas and applied to a hierarchy rather than a flat graph: a group is one
/// box, its members are laid out inside it, and the boxes are layered and ordered by the same rules. Laying out
/// members individually interleaves the members of different groups, and interleaved members force their frames
/// to overlap regardless of spacing.
/// <para>
/// Within a level, blocks are layered by longest path from the sources, and nothing sits left of what feeds it.
/// Barycenter sweeps alternate between both directions to reduce crossings, and in the right-to-left half of
/// each sweep the sources are ordered by the sockets they feed. Sizes come from the objects' real bounds. Groups
/// get padding for their frame and the label above it, and mother groups are moved to the back afterwards.
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

    /// <summary>How near its planned pivot, in pixels, an object counts as in place. See Place.</summary>
    private const float Settled = 1.5f;

    /// <summary>One box in the layout: a single object, or a group with boxes of its own inside.</summary>
    private sealed class Block
    {
        internal IGH_DocumentObject? Node;
        internal GH_Group? Group;
        internal List<Block> Children = [];
        internal List<IGH_DocumentObject> Leaves = [];
        internal SizeF Size;
        internal PointF At;

        /// <summary>The height reserved above the members for the group's own captions.</summary>
        internal float Band;

        /// <summary>
        /// Where the group's captions stack up from, once the block is applied: the body's top-left corner.
        /// </summary>
        internal PointF? Base;

        /// <summary>
        /// The block's identity, used to order two blocks that the dataflow does not separate.
        /// </summary>
        /// <remarks>
        /// The instance guid is the only identity here that no layout pass rewrites. The layout sets positions, and
        /// the restacking at the end rewrites document order.
        /// </remarks>
        internal Guid Key => Group?.InstanceGuid ?? Node?.InstanceGuid ?? Guid.Empty;
    }

    /// <summary>Arranges the whole document. Returns how many objects moved.</summary>
    internal static int Whole(GH_Document document)
    {
        // Notes are laid out by Captions afterwards, and an unwired panel counts as a note. Laying them out here as
        // well would move them twice.
        List<IGH_DocumentObject> nodes = [.. document.Objects
            .Where(thing => thing is IGH_Component or IGH_Param && thing.Attributes is not null && !IsNote(thing))];

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

        // Ownership. Grasshopper allows an object in several groups. The first group that lists it owns it,
        // because the layout places each object in exactly one location.
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

        // Blocks: every unowned group is a root box, and every unowned object is a root box of its own.
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
                roots.Add(new Block { Node = thing, Leaves = [thing], Size = Pixels(thing.Attributes!.Bounds.Size) });
            }
        }

        foreach (Block root in roots)
        {
            Measure(root, upstream);
        }

        LayoutLevel(roots, upstream, BlockGapX, BlockGapY);

        // Plan from the old top-left. ToCorner moves the finished document into the corner afterwards, once the
        // frames and captions it has to clear are drawn.
        PointF origin = nodes
            .Select(thing => thing.Attributes!.Bounds.Location)
            .Aggregate((kept, next) => new PointF(Math.Min(kept.X, next.X), Math.Min(kept.Y, next.Y)));

        // Record where everything was before any of it moves. The moved count at the end needs this: it counts the
        // objects that ended somewhere else, which can differ from how many were written. The drift correction
        // below needs it too: the anchor is the top-left of these old pivots, and the plan is moved onto it.
        Dictionary<IGH_DocumentObject, PointF> before = [];

        foreach (IGH_DocumentObject thing in document.Objects)
        {
            if (thing.Attributes is { } attributes)
            {
                before[thing] = attributes.Pivot;
            }
        }

        // Plan every pivot before writing any. The drift correction below is added to the plan, and each object
        // moves at most once.
        Dictionary<IGH_DocumentObject, PointF> wants = [];

        foreach (Block root in roots)
        {
            Plan(root, origin.X, origin.Y, wants);
        }

        // Correct the plan. With the correction, running this twice gives the same result as running it once.
        //
        // The anchor is the top-left of where the objects were. The layout does not place its first object at its
        // own top-left: inside a group it is inset by the frame's padding and the room the label needs. The result
        // lands down and to the right of the anchor by that inset. The next run takes the new positions as its
        // anchor and adds the inset again, and the definition drifts across the canvas (measured at 26 by 52 pixels
        // per run). Translating the plan back onto the anchor removes the drift for any inset.
        //
        // The correction is folded into the plan, which avoids a second move over objects the layout has just
        // written. Such a move, with the layout origin read from bounds (which Grasshopper rounds against the
        // pivot), does not cancel the first one: a second pass moved 46 objects by one pixel.
        PointF anchor = nodes
            .Select(thing => before[thing])
            .Aggregate((kept, next) => new PointF(Math.Min(kept.X, next.X), Math.Min(kept.Y, next.Y)));

        PointF landed = nodes
            .Where(wants.ContainsKey)
            .Select(thing => wants[thing])
            .DefaultIfEmpty(anchor)
            .Aggregate((kept, next) => new PointF(Math.Min(kept.X, next.X), Math.Min(kept.Y, next.Y)));

        PointF drift = new(MathF.Round(anchor.X - landed.X), MathF.Round(anchor.Y - landed.Y));

        foreach ((IGH_DocumentObject node, PointF want) in wants)
        {
            Place(document, node, new PointF(want.X + drift.X, want.Y + drift.Y));
        }

        foreach (Block block in blockOfGroup.Values)
        {
            if (block.Base is { } reserved)
            {
                block.Base = new PointF(reserved.X + drift.X, reserved.Y + drift.Y);
            }
        }

        Captions(document, groups, blockOfGroup);

        Restack(document, groups, groupById);

        ToCorner(document, groups, before);

        // Compare where each object ended with where it started. A settled document reports zero however much was
        // written on the way.
        int moved = 0;

        foreach ((IGH_DocumentObject thing, PointF was) in before)
        {
            if (thing.Attributes is { } now
                && (Math.Abs(now.Pivot.X - was.X) > 0.5f || Math.Abs(now.Pivot.Y - was.Y) > 0.5f))
            {
                moved++;
            }
        }

        return moved;
    }

    /// <summary>Where the top-left corner of the arranged document goes, in canvas pixels.</summary>
    private const float Corner = 20;

    /// <summary>
    /// Moves the whole document so that the rectangle around everything drawn starts at (<see cref="Corner"/>,
    /// <see cref="Corner"/>).
    /// </summary>
    /// <remarks>
    /// The rectangle counts the group frames, the group names drawn above them and every note, which the layout
    /// itself does not see. A document left at negative coordinates sat across the edge of Grasshopper's page:
    /// <c>canvas_image</c> drew the page's shadow through it, and an agent moved the document by script to get
    /// away from the edge.
    /// <para>
    /// The layout keeps its own anchor, and this runs after it, on a document whose frames are already laid out.
    /// On a settled document the rectangle is already in the corner and nothing moves. A difference below
    /// <see cref="Settled"/> is left alone, because bounds are rounded against the pivot and can differ by a
    /// pixel between two positions of the same object.
    /// </para>
    /// </remarks>
    private static void ToCorner(
        GH_Document document,
        List<GH_Group> groups,
        Dictionary<IGH_DocumentObject, PointF> before)
    {
        RectangleF? all = null;

        foreach (IGH_DocumentObject thing in document.Objects)
        {
            if (thing.Attributes is not { } attributes)
            {
                continue;
            }

            RectangleF bounds = attributes.Bounds;

            // A group's name is a balloon drawn above the middle of its frame, outside the frame's bounds. The
            // size follows GH_GraphicsUtil.RenderBalloonTag.
            if (thing is GH_Group group && !string.IsNullOrWhiteSpace(group.NickName))
            {
                SizeF text = GH_FontServer.MeasureString(
                    group.NickName,
                    GH_FontServer.StandardAdjusted);

                bounds = RectangleF.Union(bounds, new RectangleF(
                    bounds.X + (bounds.Width / 2) - ((text.Width + 6) / 2),
                    bounds.Y - (text.Height + 8),
                    text.Width + 6,
                    text.Height + 2));
            }

            all = all is null ? bounds : RectangleF.Union(all.Value, bounds);
        }

        if (all is not { } extent)
        {
            return;
        }

        float dx = Math.Abs(Corner - extent.X) < Settled ? 0 : MathF.Round(Corner - extent.X);
        float dy = Math.Abs(Corner - extent.Y) < Settled ? 0 : MathF.Round(Corner - extent.Y);

        if (dx == 0 && dy == 0)
        {
            return;
        }

        foreach (IGH_DocumentObject thing in document.Objects)
        {
            if (thing is GH_Group || thing.Attributes is not { } attributes)
            {
                continue;
            }

            // Place recorded an undo step for every object it moved, holding where the object stood before this
            // arrange. An object still at its old pivot has no step yet.
            if (before.TryGetValue(thing, out PointF was) && attributes.Pivot == was)
            {
                document.UndoUtil.RecordGenericObjectEvent("Phenome Link: arrange", thing);
            }

            attributes.Pivot = new PointF(attributes.Pivot.X + dx, attributes.Pivot.Y + dy);
            attributes.ExpireLayout();
            attributes.PerformLayout();
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
    }

    /// <summary>
    /// Places notes after every other object has a position.
    /// </summary>
    /// <remarks>
    /// A note has no ports and takes no part in the dataflow, and the layout leaves it out. This pass places it
    /// afterwards and cannot disturb the layout already decided. The rule needs no new field, because a note's
    /// group says what the note is about. A note in a group is that group's caption and goes above the group's
    /// other members. A note in no group is about the document and goes above everything. An agent already sets
    /// this by passing <c>group</c> to <c>place</c>, and <c>describe</c> reports it back.
    /// <para>
    /// Captions are measured from the members, not from the frame. A note in a group is one of its members, and
    /// the frame is drawn around the note too. Placing the note from the frame would feed its own output back as
    /// input and push it further out on each run. The members that are not notes give a stable measure, and
    /// repeated arrange passes give the same result.
    /// </para>
    /// </remarks>
    private static int Captions(GH_Document document, List<GH_Group> groups, Dictionary<Guid, Block> blockOfGroup)
    {
        int moved = 0;
        HashSet<Guid> spoken = [];

        // The highest line any caption was written on, tracked while writing. A caption's Bounds does not move
        // until the next layout pass. Reading the captions back from the canvas returns where they were before, and
        // the document's own notes would be stacked on top of them.
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

                // Read pivots here. Bounds is cached during a layout pass, and the layout has just moved every
                // member: bounds still give the positions from before the move, and a caption placed from them
                // lands against a body that has shifted. A pivot holds what the layout wrote.
                corner = new PointF(
                    Math.Min(corner.X, attributes.Pivot.X),
                    Math.Min(corner.Y, attributes.Pivot.Y));

                any = true;
            }

            // The layout reserved a band for these above the body and recorded where the body starts. Measuring the
            // members instead puts a mother group's caption against its child groups' pivots. A group does not keep
            // its pivot up to date, and the caption can land on a component inside.
            if (blockOfGroup.GetValueOrDefault(group.InstanceGuid)?.Base is { } reserved)
            {
                corner = reserved;
                any = true;
            }

            if (!any)
            {
                continue;
            }

            // Stack the captions upwards without overlap, from just above the body, in the order the group holds
            // them.
            float above = MathF.Round(corner.Y) - CaptionGap;

            foreach (IGH_DocumentObject note in notes)
            {
                if (!spoken.Add(note.InstanceGuid))
                {
                    continue;
                }

                above -= Pixels(note.Attributes!.Bounds.Size).Height;
                moved += Put(note, new PointF(MathF.Round(corner.X), above));
                ceiling = Math.Min(ceiling, above);
                above -= CaptionGap / 2;
            }
        }

        // A note in no group belongs to the document, for example a title, a credit or a warning. It goes above
        // everything, where a reader looks first and no component sits.
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

        // The document's notes go above the group captions as well as the components. Both are notes and neither
        // is in the layout, and only this line keeps a document title off a group's caption.
        float band = Math.Min(everything.Y, ceiling) - CaptionGap;

        foreach (IGH_DocumentObject note in loose)
        {
            band -= note.Attributes!.Bounds.Height;
            moved += Put(note, new PointF(everything.X, band));
            band -= CaptionGap / 2;
        }

        return moved;
    }

    /// <summary>Whether this object is a note, something to read.</summary>
    /// <remarks>
    /// A scribble always is. A panel is a note only when nothing is wired to it in either direction. A panel in
    /// the middle of a definition is a probe on the data and stays where the data is; an unwired one is a caption.
    /// </remarks>
    private static bool IsNote(IGH_DocumentObject thing) =>
        thing is GH_Scribble
        || (thing is GH_Panel panel && panel.SourceCount == 0 && panel.Recipients.Count == 0);

    /// <summary>
    /// Moves a note's pivot to a point, counting it only when it was not already there.
    /// </summary>
    /// <remarks>
    /// Works in pivot space throughout. Everything around the note has just moved, and bounds are not updated
    /// until the next layout pass. The caption is read and written as a pivot, and the calculation does not depend
    /// on a number that is about to change.
    /// </remarks>
    private static int Put(IGH_DocumentObject note, PointF want)
    {
        PointF pivot = note.Attributes!.Pivot;

        if (Math.Abs(pivot.X - want.X) < Settled && Math.Abs(pivot.Y - want.Y) < Settled)
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
                block.Children.Add(new Block { Node = node, Leaves = [node], Size = Pixels(node.Attributes!.Bounds.Size) });
            }
        }

        return block;
    }

    /// <summary>A block's size, from the inside out: children laid out first, then the frame around them.</summary>
    private static void Measure(Block block, Dictionary<IGH_DocumentObject, List<IGH_DocumentObject>> upstream)
    {
        if (block.Node is { } node)
        {
            block.Size = Pixels(node.Attributes!.Bounds.Size);
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

        // The block's size includes room for the group's captions, and the box the layout reserves is the box the
        // frame is drawn around. A caption is one unwrapped line and is often wider than what it captions: a 503 px
        // caption over a block at x=100 reached x=603, and the neighbour started at 579. The band adds up the
        // captions the way Captions stacks them, and the topmost caption ends at the top of the band.
        List<SizeF> captions = Notes(block.Group!);
        float widest = captions.Count == 0 ? 0 : captions.Max(caption => caption.Width);

        block.Band = captions.Count == 0
            ? 0
            : CaptionGap + captions.Sum(caption => caption.Height) + ((captions.Count - 1) * CaptionGap / 2);

        block.Size = new SizeF(
            Math.Max(inner.Width, widest) + (2 * GroupPad),
            inner.Height + (2 * GroupPad) + GroupLabel + block.Band);
    }

    /// <summary>
    /// Lays out one level of boxes: layers them left to right, orders each layer to reduce crossings and stacks
    /// it. Returns the space used.
    /// </summary>
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

        // Walk the blocks in guid order. In a cycle the block walked first lands right of the others. Arrival order
        // is document order, which Restack reverses on every run, and walking in it would make two mutually feeding
        // groups swap columns on every pass.
        foreach (int i in Enumerable.Range(0, blocks.Count).OrderBy(i => blocks[i].Key))
        {
            LayerOf(i);
        }

        // For each block, the wires it sends to other blocks, seen from the reading side: the block that reads,
        // and how far down that block the receiving socket sits, as a fraction of the block's height.
        List<(int Reader, double Down)>[] readers = Readers(blocks, owner);

        // Place a block with no feeders in the column just left of its nearest reader. Longest path from the
        // sources alone puts every source in the first column. Groups feeding a component three columns along would
        // then stand among the groups feeding the first one, and their wires would cross all of them.
        for (int i = 0; i < blocks.Count; i++)
        {
            if (feeders[i].Count == 0 && readers[i].Count > 0)
            {
                layer[i] = Math.Max(layer[i], readers[i].Min(read => layer[read.Reader]) - 1);
            }
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

        // Start the sweeps from the current top-to-bottom order in each column.
        //
        // The sweeps do not start from document order. Restack rewrites it on every run by sending each group to
        // the back in turn, which reverses them, and a block with nothing wired to it keeps the position it starts
        // from (16 unconnected groups came out reversed with about 160 objects moved per pass). This pass decides
        // position, and a second run starts from the order the first one left. Ties go to the guid, which no pass
        // rewrites.
        float[] top = new float[blocks.Count];

        for (int i = 0; i < blocks.Count; i++)
        {
            top[i] = blocks[i].Leaves.Count == 0
                ? 0
                : blocks[i].Leaves.Min(leaf => leaf.Attributes?.Bounds.Y ?? 0);
        }

        foreach (List<int> column in columns)
        {
            column.Sort((a, b) =>
            {
                int byTop = top[a].CompareTo(top[b]);

                return byTop != 0 ? byTop : blocks[a].Key.CompareTo(blocks[b].Key);
            });
        }

        double[] rank = new double[blocks.Count];

        void Order(List<int> column)
        {
            // Sort by rank, and break equal ranks by the block's guid.
            //
            // Two groups with no wire between them keep equal ranks through every sweep. List.Sort is not stable,
            // and without the tie-break their order depends on the sort's internals and can differ between runs on
            // the same document. An instance guid is fixed for an object's life and no pass changes it.
            column.Sort((a, b) =>
            {
                int byRank = rank[a].CompareTo(rank[b]);

                return byRank != 0 ? byRank : blocks[a].Key.CompareTo(blocks[b].Key);
            });
        }

        // A block's index in its column, or the fallback when the column does not contain it (a cycle).
        int Place(int block, int fallback)
        {
            int at = columns[layer[block]].IndexOf(block);

            return at < 0 ? fallback : at;
        }

        for (int sweep = 0; sweep < Sweeps; sweep++)
        {
            // Left to right: a block goes level with what feeds it.
            foreach (List<int> column in columns)
            {
                for (int i = 0; i < column.Count; i++)
                {
                    int block = column[i];

                    rank[block] = feeders[block].Count == 0
                        ? i
                        : feeders[block].Average(feeder => Place(feeder, i));
                }

                Order(column);
            }

            // Right to left: a block goes level with the socket it feeds. This pass puts the source of a
            // component's first input above the source of its second, and the inputs of a group in the order of
            // its inlets. A source has no feeders, and the left-to-right pass leaves a column of sources in the
            // order it started in. This half runs last in each sweep, and the sweeps end with the sources in socket
            // order.
            for (int c = columns.Length - 1; c >= 0; c--)
            {
                List<int> column = columns[c];

                for (int i = 0; i < column.Count; i++)
                {
                    int block = column[i];

                    rank[block] = readers[block].Count == 0
                        ? i
                        : readers[block].Average(read => Place(read.Reader, i) + read.Down);
                }

                Order(column);
            }
        }

        float x = 0;
        float tallest = 0;

        foreach (List<int> column in columns)
        {
            // A column can be empty: two groups can feed each other through different members, and a cycle in the
            // block graph leaves a layer number with nothing on it. Max over an empty column throws "Sequence
            // contains no elements" and fails the whole verb.
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

    /// <summary>
    /// For each block, the blocks that read from it and how far down each reader the socket sits.
    /// </summary>
    /// <remarks>
    /// The fraction is measured on the reading block as it was laid out inside: the leaf's own offset in its
    /// block plus the socket's share of the leaf's height, over the block's height. A block of one component
    /// gives its first input 0.5 / n and its last (n - 0.5) / n. In a group the inlet near the top gets a small
    /// number, and its source ranks above the source of an inlet lower down.
    /// </remarks>
    private static List<(int Reader, double Down)>[] Readers(List<Block> blocks, Dictionary<IGH_DocumentObject, int> owner)
    {
        List<(int Reader, double Down)>[] readers = new List<(int, double)>[blocks.Count];

        for (int i = 0; i < blocks.Count; i++)
        {
            readers[i] = [];
        }

        for (int j = 0; j < blocks.Count; j++)
        {
            Block reader = blocks[j];
            float height = Math.Max(1, reader.Size.Height);

            foreach (IGH_DocumentObject leaf in reader.Leaves)
            {
                List<IGH_Param> inputs = [.. InputsOf(leaf)];
                float offset = OffsetOf(reader, leaf);
                float tall = leaf.Attributes?.Bounds.Height ?? 0;

                for (int k = 0; k < inputs.Count; k++)
                {
                    double down = (offset + (tall * (k + 0.5) / inputs.Count)) / height;

                    foreach (IGH_Param source in inputs[k].Sources)
                    {
                        IGH_DocumentObject from = source.Attributes?.GetTopLevel?.DocObject ?? source;

                        if (owner.TryGetValue(from, out int i) && i != j)
                        {
                            readers[i].Add((j, Math.Clamp(down, 0, 0.999)));
                        }
                    }
                }
            }
        }

        return readers;
    }

    /// <summary>How far below a block's top edge one of its leaves was laid out.</summary>
    private static float OffsetOf(Block block, IGH_DocumentObject leaf)
    {
        if (block.Node is not null)
        {
            return 0;
        }

        foreach (Block child in block.Children)
        {
            if (child.Leaves.Contains(leaf))
            {
                return GroupPad + GroupLabel + child.At.Y + OffsetOf(child, leaf);
            }
        }

        return 0;
    }

    /// <summary>A size in whole pixels, the unit every block is measured and stacked in.</summary>
    /// <remarks>
    /// Bounds come from text measurement and are fractional, and Grasshopper rounds them against the pivot. The
    /// same note can measure a pixel taller or shorter depending on its last position. Without rounding, the band
    /// reserved for it changes with it, and a second pass moves the body below by a pixel.
    /// </remarks>
    private static SizeF Pixels(SizeF size) => new(MathF.Ceiling(size.Width), MathF.Ceiling(size.Height));

    /// <summary>The sizes of a group's own captions, the notes it holds directly.</summary>
    private static List<SizeF> Notes(GH_Group group)
    {
        if (group.OnPingDocument() is not { } document)
        {
            return [];
        }

        return [.. group.ObjectIDs
            .Select(id => document.FindObject(id, topLevelOnly: true))
            .Where(thing => thing is not null && IsNote(thing) && thing.Attributes is not null)
            .Select(thing => Pixels(thing!.Attributes.Bounds.Size))];
    }

    /// <summary>Relative positions become planned pivots, a block and its contents at a time.</summary>
    private static void Plan(Block block, float dx, float dy, Dictionary<IGH_DocumentObject, PointF> wants)
    {
        float x = block.At.X + dx;
        float y = block.At.Y + dy;

        if (block.Node is { } node)
        {
            RectangleF bounds = node.Attributes!.Bounds;
            PointF pivot = node.Attributes.Pivot;

            // Keep the pivot's own offset inside the bounds, and the object's top-left lands where the layout put
            // it. Round to whole pixels: Grasshopper rounds an object's bounds against its pivot, and a fractional
            // pivot measures differently next time.
            wants[node] = new PointF(
                MathF.Round(x + (pivot.X - bounds.X)),
                MathF.Round(y + (pivot.Y - bounds.Y)));

            return;
        }

        float body = y + GroupPad + GroupLabel + block.Band;

        block.Base = new PointF(x + GroupPad, body);

        foreach (Block child in block.Children)
        {
            Plan(child, x + GroupPad, body, wants);
        }
    }

    /// <summary>Writes one planned pivot, unless the object already stands there.</summary>
    private static void Place(GH_Document document, IGH_DocumentObject node, PointF want)
    {
        PointF pivot = node.Attributes!.Pivot;

        // Do not move an object already in place. Moving it anyway has two costs: the moved count stops meaning
        // anything on a settled document, and every rerun records an undo step per object that undoes nothing.
        // Arranging twice is normal, and the second run should report nothing and record nothing.
        //
        // The test allows a difference below Settled (1.5 px) on each axis. Grasshopper rounds an object's bounds
        // against its pivot: a parameter fifty-and-a-fraction pixels wide measures fifty at one position and fifty-one at the
        // next, and everything laid out after it lands a pixel along. A pixel is below anything a canvas shows,
        // and a target that close counts as already in place.
        if (Math.Abs(pivot.X - want.X) < Settled && Math.Abs(pivot.Y - want.Y) < Settled)
        {
            return;
        }

        document.UndoUtil.RecordGenericObjectEvent("Phenome Link: arrange", node);

        node.Attributes.Pivot = want;

        // Expire *and* recompute the layout. Bounds is computed during a layout pass and cached. Until the next
        // pass Bounds and Pivot disagree, and anything converting one to the other reads the old position (two
        // groups swapped places on alternate passes this way). Recomputing here costs one layout per moved object
        // and removes that class of fault.
        node.Attributes.ExpireLayout();
        node.Attributes.PerformLayout();
    }

    /// <summary>
    /// Recomputes every frame and sends groups behind their contents and mother groups behind their children.
    /// </summary>
    private static void Restack(GH_Document document, List<GH_Group> groups, Dictionary<Guid, GH_Group> groupById)
    {
        // Lay out now, before the next repaint. A group's frame is derived from its members' bounds and cached.
        // Until a layout runs, both the canvas and anything reading /canvas report the old frames and conclude the
        // groups overlap.
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

        // Mother groups go last and end up furthest back of all.
        foreach (GH_Group group in groups.Where(group => IsMother(group, groupById)))
        {
            document.ArrangeObject(group, GH_Arrange.MoveToBack);
        }

        // Bring notes to the front, which is the second half of placing them. A group frame is a tinted rectangle
        // drawn over what is behind it. A caption under a frame is washed out, and a caption under a component is
        // invisible. A note is never left behind anything.
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
