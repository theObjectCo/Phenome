using System.Drawing;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Phenome.Apps.GrasshopperLink.Definition;

/// <summary>
/// Reads the document against the composition rules and says where it falls short.
/// </summary>
/// <remarks>
/// The mechanical half of the doctrine, so an author - human or agent - can converge instead of guess: what
/// is measurable is measured (overlapping frames, unnamed groups, wires crossing a boundary without a
/// parameter, groups too big to be one function, nesting past one level, objects in no group at all). What
/// is not measurable - whether a group really does one thing - is left to the reader, but a group whose
/// name says "and" is flagged, because a name is the honest confession of a mixed concern.
/// </remarks>
internal static class Review
{
    private const int TooMany = 31;

    /// <summary>Above this many items in a one-item socket, a broadcast stops looking deliberate.</summary>
    private const int Suspicious = 100;

    /// <summary>
    /// The four roles a group may have, and the colour each one wears: blue for the knobs a customer may
    /// turn, red for what gets baked into Rhino as the product, yellow for geometry that is only ever
    /// looked at, grey for a plain function.
    /// </summary>
    private static readonly (string Role, int R, int G, int B)[] Palette =
    [
        ("user input", 70, 110, 255),
        ("plain function", 150, 150, 150),
        ("bake to Rhino", 255, 60, 60),
        ("preview only", 255, 220, 0),
    ];

    internal static string Whole(GH_Document? document)
    {
        if (document is null)
        {
            return "{\"findings\":[]}";
        }

        List<GH_Group> groups = [.. document.Objects.OfType<GH_Group>()];
        List<IGH_DocumentObject> nodes = [.. document.Objects
            .Where(thing => thing is IGH_Component or IGH_Param && thing.Attributes is not null)];

        Dictionary<Guid, GH_Group> groupById = [];

        foreach (GH_Group group in groups)
        {
            groupById[group.InstanceGuid] = group;
        }

        List<string> findings = [];

        HashSet<Guid> grouped = [];

        foreach (GH_Group group in groups)
        {
            foreach (Guid member in Members(document, group))
            {
                grouped.Add(member);
            }
        }

        foreach (GH_Group group in groups)
        {
            string name = group.NickName ?? "";
            HashSet<Guid> inside = Members(document, group);

            if (string.IsNullOrWhiteSpace(name))
            {
                Say(findings, group, "unnamed", "A group with no name carries no abstraction - name it for the one thing it does.");
            }
            else if (name.Contains(" and ", StringComparison.OrdinalIgnoreCase)
                || name.Contains(" i ", StringComparison.OrdinalIgnoreCase)
                || name.Contains(',')
                || name.Contains('&')
                || name.Contains('+'))
            {
                Say(findings, group, "two jobs", $"'{name}' names more than one thing - split it into a group per job.");
            }

            int members = inside.Count(member => !groupById.ContainsKey(member));

            if (members >= TooMany)
            {
                Say(findings, group, "too big", $"{members} objects in one group - a function this long is usually several.");
            }

            if (group.ObjectIDs.Any(member => groupById.ContainsKey(member) && member != group.InstanceGuid)
                && inside.Where(groupById.ContainsKey).Any(child =>
                    groupById[child].ObjectIDs.Any(grandchild => groupById.ContainsKey(grandchild))))
            {
                Say(findings, group, "nested twice", "Nesting deeper than one level - flatten it.");
            }

            int bare = BareCrossings(document, group, inside);

            if (bare > 0)
            {
                Say(findings, group, "no signature",
                    $"{bare} wire(s) cross the boundary without a floating parameter - run signature so the group reads as a component.");
            }

            // A blue group that feeds the whole definition is an input bank: the knobs were collected by
            // kind instead of standing where they are used, which is the readability the colour promised.
            if (Near(group.Colour.R, 70) && Near(group.Colour.G, 110) && Near(group.Colour.B, 255)
                && Serves(document, inside, groupById) is > 2 and var served)
            {
                Say(findings, group, "input bank",
                    $"This blue group feeds {served} other groups - put each input in the group that uses it, "
                    + "so a reader finds a knob where its effect is.");
            }

            // A colour is a role, so a colour off the palette says the role was never decided.
            if (!Palette.Any(role =>
                Near(group.Colour.R, role.R) && Near(group.Colour.G, role.G) && Near(group.Colour.B, role.B)))
            {
                Say(findings, group, "no role colour",
                    "The colour is off the palette - user-modifiable inputs [70,110,255], plain function "
                    + "[150,150,150], geometry baked to Rhino [255,60,60], preview-only geometry [255,220,0].");
            }
        }

        // An object in two groups, which is the overlap nobody can lay out away: a frame is drawn around
        // every one of its members, so two groups sharing an object must reach across each other whatever
        // the layout does. arrange cannot fix it; only taking the object out of one group can.
        Dictionary<Guid, List<string>> claimed = [];

        foreach (GH_Group group in groups)
        {
            foreach (Guid member in group.ObjectIDs)
            {
                if (!claimed.TryGetValue(member, out List<string>? owners))
                {
                    claimed[member] = owners = [];
                }

                owners.Add(string.IsNullOrWhiteSpace(group.NickName) ? "an unnamed group" : $"'{group.NickName}'");
            }
        }

        foreach ((Guid member, List<string> owners) in claimed.Where(entry => entry.Value.Count > 1))
        {
            findings.Add(Finding(
                "shared object",
                $"{(document.FindObject(member, topLevelOnly: true) is { } thing ? Named(thing) : "an object")} "
                + $"belongs to {owners.Count} groups ({string.Join(", ", owners)}) - their frames must overlap "
                + "until it belongs to one. arrange cannot help with this.",
                member));
        }

        // Overlapping frames, ignoring the one case where overlap is the point.
        for (int i = 0; i < groups.Count; i++)
        {
            for (int j = i + 1; j < groups.Count; j++)
            {
                if (Related(document, groups[i], groups[j]))
                {
                    continue;
                }

                RectangleF a = groups[i].Attributes?.Bounds ?? RectangleF.Empty;
                RectangleF b = groups[j].Attributes?.Bounds ?? RectangleF.Empty;

                if (a.IntersectsWith(b))
                {
                    bool caption = !Body(document, groups[i]).IntersectsWith(Body(document, groups[j]));

                    findings.Add(Finding(
                        "overlap",
                        $"'{groups[i].NickName}' and '{groups[j].NickName}' overlap - "
                            + (caption
                                ? "a note reaches past the members it captions, so the frame drawn around it is "
                                    + "wider than the room the layout reserved. arrange will not change this - it "
                                    + "already landed where it means to. Shorten the note, or take it out of the group."
                                : "run arrange, which lays groups out as whole blocks."),
                        groups[i].InstanceGuid));
                }
            }
        }

        // What the canvas is already shouting. A review that lints the composition while ignoring seven red
        // components is worse than no review: it reports "clean" over a definition that does not run, and an
        // author told to bring the review to zero believes them.
        foreach (IGH_DocumentObject thing in nodes)
        {
            if (thing is not IGH_ActiveObject active)
            {
                continue;
            }

            foreach ((GH_RuntimeMessageLevel level, string kind) in
                new[] { (GH_RuntimeMessageLevel.Error, "error"), (GH_RuntimeMessageLevel.Warning, "warning") })
            {
                foreach (string message in active.RuntimeMessages(level).Distinct())
                {
                    findings.Add(Finding(
                        kind,
                        $"{Named(thing)}: {message}",
                        thing.InstanceGuid));
                }
            }
        }

        // Two relays nose to tail inside one group: a parameter passing straight into another parameter of
        // the same group carries nothing the first one did not. Across a boundary the same shape is the
        // signature working - one group's outlet feeding the next one's inlet - which is why this looks at
        // who owns each end rather than at the wiring alone.
        foreach (IGH_DocumentObject thing in nodes)
        {
            if (thing is not IGH_Param relay || !IsRelay(relay) || relay.SourceCount != 1)
            {
                continue;
            }

            IGH_DocumentObject feeder = relay.Sources[0].Attributes?.GetTopLevel?.DocObject ?? relay.Sources[0];

            if (feeder is not IGH_Param before || !IsRelay(before))
            {
                continue;
            }

            GH_Group? mine = groups.FirstOrDefault(group => Members(document, group).Contains(thing.InstanceGuid));
            GH_Group? theirs = groups.FirstOrDefault(group => Members(document, group).Contains(feeder.InstanceGuid));

            if (mine is not null && ReferenceEquals(mine, theirs))
            {
                findings.Add(Finding(
                    "chained ports",
                    $"'{Named(feeder)}' passes straight into '{Named(thing)}' inside '{mine.NickName}' - two "
                    + "parameters carrying the same value. Keep one and delete the other.",
                    thing.InstanceGuid));
            }
        }

        // Dead ends: an object that feeds nothing and draws nothing is doing nothing, and every one of them
        // is a thing the next reader has to check before ignoring. Leftovers of a rethink, mostly - a
        // parameter that used to carry something, a component whose output was rewired elsewhere.
        foreach (IGH_DocumentObject thing in nodes)
        {
            // It draws: that is a purpose, and it is how most geometry is shown.
            if (thing is IGH_PreviewObject { IsPreviewCapable: true, Hidden: false })
            {
                continue;
            }

            List<IGH_Param> outputs = [.. OutputsOf(thing)];

            // Nothing to feed with - a Custom Preview or a bake target is the end of the line by design.
            if (outputs.Count == 0 || outputs.Any(output => output.Recipients.Count > 0))
            {
                continue;
            }

            // A panel is prose and a swatch is a colour someone picked; neither owes anybody a wire.
            if (thing is GH_Panel or GH_Scribble or Grasshopper.Kernel.Special.GH_ColourSwatch)
            {
                continue;
            }

            bool orphan = Arrange.InputsOf(thing).All(input => input.SourceCount == 0);

            findings.Add(Finding(
                "unused",
                $"'{Named(thing)}' feeds nothing and draws nothing"
                + (orphan ? " and takes nothing either - it is a leftover; delete it." : " - a dead end: "
                    + "wire its output where it belongs, or delete it."),
                thing.InstanceGuid));
        }

        // The parameter modifiers, which hide a structural change where no reader will look for it - and
        // simplify worst of all, because what it drops depends on the data it happens to meet.
        foreach (IGH_DocumentObject thing in nodes)
        {
            foreach (IGH_Param side in Arrange.InputsOf(thing).Concat(OutputsOf(thing)).Distinct())
            {
                if (side.Simplify)
                {
                    findings.Add(Finding(
                        "simplify",
                        $"'{Named(thing)}' has the simplify modifier on '{side.Name}' - never use it: what it "
                        + "drops depends on the data it meets, so the definition behaves differently in "
                        + "someone else's file. Change structure visibly, with a component.",
                        thing.InstanceGuid));
                }

                if (side.DataMapping != GH_DataMapping.None)
                {
                    findings.Add(Finding(
                        "hidden mapping",
                        $"'{Named(thing)}' has {side.DataMapping.ToString().ToLowerInvariant()} hidden on "
                        + $"'{side.Name}' - put a Flatten or Graft component on the canvas instead, where a "
                        + "reader can see the structure change.",
                        thing.InstanceGuid));
                }
            }
        }

        // Data matching, which is where a definition goes quietly wrong rather than red: a component whose
        // input takes one item per branch, handed several, runs once per item and multiplies everything
        // downstream. Nothing complains, the geometry just doubles.
        foreach (IGH_DocumentObject thing in nodes.Where(thing => thing is IGH_Component))
        {
            IGH_Component component = (IGH_Component)thing;

            foreach (IGH_Param input in component.Params.Input)
            {
                if (input.Access != GH_ParamAccess.item || input.VolatileDataCount <= 1)
                {
                    continue;
                }

                int fattest = 0;

                foreach (Grasshopper.Kernel.Data.GH_Path path in input.VolatileData.Paths)
                {
                    fattest = Math.Max(fattest, input.VolatileData.get_Branch(path).Count);
                }

                // Not a fault by itself: a component fed four shelf heights runs four times, which is how
                // Grasshopper is meant to work. The fault is a count nobody intended, and no checker can
                // read intent - so a modest count is said as something to confirm, and only an absurd one
                // is called blocking. Calling four items a bug had an agent doubting a verified graph.
                if (fattest > 1)
                {
                    findings.Add(Finding(
                        fattest > Suspicious ? "multiplies" : "broadcast",
                        $"{component.Name}'s '{input.Name}' takes one item but holds {fattest} in a branch, so "
                        + $"it runs {fattest} times per branch. "
                        + (fattest > Suspicious
                            ? "That is far more than a definition usually intends - check for a lost tree "
                            + "structure, or clear a socket whose default is doubling it (set with a null value)."
                            : "Confirm with peek that this is the count you meant."),
                        thing.InstanceGuid));
                }
            }
        }

        // Two wires into one socket meet only where their paths agree. Grasshopper concatenates by path, so
        // sources sitting at different depths - one on {0}, one on {0;0} - never land in the same branch:
        // the component runs once per branch on half the data each time, and the result is quietly the wrong
        // shape. Nothing turns red, and the broadcast check above cannot see it either, because every branch
        // holds exactly one item. A Boundary Surfaces handed an outline on {0} and its offset on {0;0} makes
        // two separate surfaces instead of one with a hole in it, and looks right until somebody measures.
        foreach (IGH_DocumentObject thing in nodes)
        {
            foreach (IGH_Param input in Arrange.InputsOf(thing))
            {
                if (input.SourceCount < 2)
                {
                    continue;
                }

                List<int> depths = [];

                foreach (IGH_Param source in input.Sources)
                {
                    foreach (Grasshopper.Kernel.Data.GH_Path path in source.VolatileData.Paths)
                    {
                        if (!depths.Contains(path.Length))
                        {
                            depths.Add(path.Length);
                        }
                    }
                }

                if (depths.Count > 1)
                {
                    depths.Sort();

                    findings.Add(Finding(
                        "mismatched paths",
                        $"'{Named(thing)}' takes {input.SourceCount} sources on '{input.Name}' whose paths are "
                        + $"{string.Join(" and ", depths)} deep - they never meet in one branch, so each is "
                        + "processed on its own and the result is not the one list you wired for. Bring them "
                        + "to one depth with a Flatten or Graft component, where a reader can see it.",
                        thing.InstanceGuid));
                }
            }
        }

        // Renamed components: the loudest readability offence there is, and perfectly measurable - a
        // component's nickname is how everyone recognises it, and names belong on parameters instead.
        foreach (IGH_DocumentObject thing in nodes.Where(thing => thing is IGH_Component))
        {
            string original = global::Grasshopper.Instances.ComponentServer
                .EmitObjectProxy(thing.ComponentGuid)?.Desc.NickName ?? "";

            if (!string.IsNullOrEmpty(original)
                && !string.Equals(original, thing.NickName, StringComparison.Ordinal))
            {
                findings.Add(Finding(
                    "renamed",
                    $"'{thing.NickName}' is a renamed {thing.Name} - put the name on a floating parameter and give the component its own nickname back.",
                    thing.InstanceGuid));
            }
        }

        // Script where components would do: countable, and worth saying out loud.
        int scripts = nodes.Count(thing =>
            thing.GetType().GetMethod("TryGetSource", [typeof(string).MakeByRefType()]) is not null
            || thing.GetType().GetProperty("ScriptSource") is not null);

        if (scripts > 0)
        {
            findings.Add(Finding(
                "script",
                $"{scripts} script component(s) - a definition should be made of components; script only when nothing else can do the job.",
                null));
        }

        // A note that belongs to no group is not the same fault as a component that belongs to no group. The
        // rule this finding enforces is "every component lives in the function that uses it", and a note is not
        // used by anything - a document-level caption belongs to the document. A scribble never reached here
        // anyway, being neither component nor parameter; an unwired panel did, because a panel *is* a
        // parameter, so a caption written as a panel was reported as a stray object.
        int loose = nodes.Count(thing =>
            !grouped.Contains(thing.InstanceGuid) && !IsAnnotation(thing));

        if (loose > 0)
        {
            findings.Add(Finding(
                "ungrouped",
                $"{loose} object(s) belong to no group - every component should live in the function that uses it.",
                null));
        }

        // A note sitting on top of something is the fault notes actually have, and until now nothing looked
        // for it: the layout pass does not move notes, so one placed before an arrange stays where it was
        // while everything else moves out from under it. Reported as polish rather than blocking, because the
        // definition still runs - it is just unreadable, which is what a note was for.
        foreach (IGH_DocumentObject note in document.Objects.Where(IsAnnotation))
        {
            if (note.Attributes?.Bounds is not { } over)
            {
                continue;
            }

            foreach (IGH_DocumentObject other in document.Objects)
            {
                if (ReferenceEquals(other, note)
                    || other is GH_Group
                    || IsAnnotation(other)
                    || other.Attributes?.Bounds is not { } under
                    || !over.IntersectsWith(under))
                {
                    continue;
                }

                findings.Add(Finding(
                    "note covers",
                    $"A note sits on top of '{Named(other)}' - arrange does not move notes, so put it in the "
                        + "group it explains, or move it clear.",
                    note.InstanceGuid));

                break;
            }
        }

        System.Text.StringBuilder json = new("{\"findings\":[");

        json.Append(string.Join(",", findings));

        return json.Append("]}").ToString();
    }

    /// <summary>Wires in or out that do not pass through a lone parameter of the group's own.</summary>
    private static int BareCrossings(GH_Document document, GH_Group group, HashSet<Guid> inside)
    {
        int bare = 0;

        foreach (Guid member in inside)
        {
            if (document.FindObject(member, topLevelOnly: true) is not { } thing || thing is GH_Group)
            {
                continue;
            }

            bool isBoundary = thing is IGH_Param;

            foreach (IGH_Param input in Arrange.InputsOf(thing))
            {
                foreach (IGH_Param source in input.Sources)
                {
                    IGH_DocumentObject from = source.Attributes?.GetTopLevel?.DocObject ?? source;

                    if (!inside.Contains(from.InstanceGuid) && !isBoundary)
                    {
                        bare++;
                    }
                }
            }

            foreach (IGH_Param output in OutputsOf(thing))
            {
                foreach (IGH_Param reader in output.Recipients)
                {
                    IGH_DocumentObject to = reader.Attributes?.GetTopLevel?.DocObject ?? reader;

                    if (!inside.Contains(to.InstanceGuid) && !isBoundary)
                    {
                        bare++;
                    }
                }
            }
        }

        return bare;
    }

    /// <summary>How many other groups this one feeds - the measure of a knob bank.</summary>
    private static int Serves(GH_Document document, HashSet<Guid> inside, Dictionary<Guid, GH_Group> groupById)
    {
        HashSet<Guid> served = [];

        foreach (Guid member in inside)
        {
            if (document.FindObject(member, topLevelOnly: true) is not { } thing || thing is GH_Group)
            {
                continue;
            }

            foreach (IGH_Param output in OutputsOf(thing))
            {
                foreach (IGH_Param reader in output.Recipients)
                {
                    IGH_DocumentObject to = reader.Attributes?.GetTopLevel?.DocObject ?? reader;

                    if (inside.Contains(to.InstanceGuid))
                    {
                        continue;
                    }

                    foreach ((Guid id, GH_Group other) in groupById)
                    {
                        if (Members(document, other).Contains(to.InstanceGuid))
                        {
                            served.Add(id);
                        }
                    }
                }
            }
        }

        return served.Count;
    }

    /// <summary>Close enough: Grasshopper's own colour picker rounds, and so does a human eye.</summary>
    private static bool Near(int one, int other) => Math.Abs(one - other) <= 12;

    /// <summary>Where a group's members are, counting only the ones the layout puts there.</summary>
    /// <remarks>
    /// This exists to tell two kinds of frame overlap apart, because they want opposite advice. arrange sizes a
    /// group's box from its <em>nodes</em> - a note is not a node, carries no data and takes no part in the
    /// layout algebra - and then a pass afterwards puts each note above the members it captions. So a note
    /// wider than those members reaches out past the room that was reserved, the frame is drawn around the note
    /// too, and two frames touch that the layout believes it separated. Running arrange again lands on exactly
    /// the same coordinates, because arrange is idempotent, so "run arrange" would send the author round a
    /// loop. Comparing bodies rather than frames says which of the two cases this is.
    /// </remarks>
    private static RectangleF Body(GH_Document document, GH_Group group)
    {
        RectangleF body = RectangleF.Empty;

        foreach (Guid member in Members(document, group))
        {
            if (document.FindObject(member, topLevelOnly: true) is not { } thing
                || thing is GH_Group
                || IsAnnotation(thing)
                || thing.Attributes?.Bounds is not { } box)
            {
                continue;
            }

            body = body.IsEmpty ? box : RectangleF.Union(body, box);
        }

        return body;
    }

    private static bool Related(GH_Document document, GH_Group one, GH_Group other) =>
        Members(document, one).Contains(other.InstanceGuid)
        || Members(document, other).Contains(one.InstanceGuid);

    private static HashSet<Guid> Members(GH_Document document, GH_Group group)
    {
        HashSet<Guid> inside = [];
        Queue<GH_Group> pending = new([group]);

        while (pending.Count > 0)
        {
            GH_Group at = pending.Dequeue();

            foreach (Guid member in at.ObjectIDs)
            {
                if (!inside.Add(member))
                {
                    continue;
                }

                if (document.FindObject(member, topLevelOnly: true) is GH_Group child && !ReferenceEquals(child, at))
                {
                    pending.Enqueue(child);
                }
            }
        }

        return inside;
    }

    private static void Say(List<string> findings, GH_Group group, string kind, string what) =>
        findings.Add(Finding(kind, what, group.InstanceGuid));

    /// <summary>
    /// Which findings stop a definition from working, and which are only manners.
    /// </summary>
    /// <remarks>
    /// An author with limited time needs to know the difference, and an agent especially: one abandoned a
    /// working graph to chase "input bank" and spent the rest of its session repairing the damage. So a
    /// finding says whether it blocks - the definition does not run or does the wrong thing - or is polish.
    /// </remarks>
    private static readonly string[] Blocking =
    [
        "error", "multiplies", "shared object", "no signature", "simplify", "hidden mapping",
        "mismatched paths",
    ];

    /// <summary>
    /// Whether an object is there to be read rather than to carry data.
    /// </summary>
    /// <remarks>
    /// A scribble always is. A panel is one only when nothing is wired to it in either direction: a panel in
    /// the middle of a definition is a probe on the data and belongs to the function it watches, while an
    /// unwired one with words in it is a caption. The difference matters because the rules for a component do
    /// not apply to prose.
    /// </remarks>
    private static bool IsAnnotation(IGH_DocumentObject thing) =>
        thing is GH_Scribble
        || (thing is GH_Panel panel
            && panel.SourceCount == 0
            && panel.Recipients.Count == 0);

    private static string Finding(string kind, string what, Guid? id)
    {
        System.Text.StringBuilder json = new("{\"kind\":");

        json.Append(Json.Quote(kind)).Append(",\"says\":").Append(Json.Quote(what));
        json.Append(",\"severity\":").Append(Json.Quote(
            Blocking.Contains(kind) ? "blocking" : "polish"));

        if (id is { } at)
        {
            json.Append(",\"id\":").Append(Json.Quote(at.ToString()));
        }

        return json.Append('}').ToString();
    }

    /// <summary>A parameter that only carries: no value of its own, nothing to look at.</summary>
    private static bool IsRelay(IGH_Param parameter) =>
        parameter is not (Grasshopper.Kernel.Special.GH_NumberSlider
            or Grasshopper.Kernel.Special.GH_Panel
            or Grasshopper.Kernel.Special.GH_ColourSwatch
            or Grasshopper.Kernel.Special.GH_BooleanToggle);

    private static string Named(IGH_DocumentObject thing) =>
        string.IsNullOrWhiteSpace(thing.NickName) ? thing.Name : thing.NickName;

    private static IEnumerable<IGH_Param> OutputsOf(IGH_DocumentObject thing) => thing switch
    {
        IGH_Component component => component.Params.Output,
        IGH_Param parameter => [parameter],
        _ => [],
    };
}
