using System.Drawing;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Phenome.Apps.GrasshopperLink.Definition;

/// <summary>
/// Reads the document against the composition rules and says where it falls short.
/// </summary>
/// <remarks>
/// Checks the composition rules that can be measured and names each concrete problem it finds. The checks cover
/// overlapping frames, unnamed groups, wires crossing boundaries without parameters, group size, excessive nesting
/// and ungrouped objects. Qualities that cannot be measured, such as whether a group performs one function, are
/// inferred only through heuristics, for example a name that lists more than one responsibility.
/// </remarks>
internal static class Review
{
    private const int TooMany = 31;

    /// <summary>Above this number of runs in one branch, a product of two inputs is treated as likely unintended.</summary>
    private const int Suspicious = 100;

    /// <summary>
    /// The four group roles and their RGB colours: user-modifiable inputs are blue, baked Rhino output is red,
    /// preview-only geometry is yellow, and plain functions are grey.
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

            // A blue group feeding three or more other groups is a central input bank. Each input belongs in the
            // group whose function it controls.
            if (Near(group.Colour.R, 70) && Near(group.Colour.G, 110) && Near(group.Colour.B, 255)
                && Serves(document, inside, groupById) is > 2 and var served)
            {
                Say(findings, group, "input bank",
                    $"This blue group feeds {served} other groups - put each input in the group that uses it. "
                    + "A reader then finds each knob next to its effect.");
            }

            // A group colour outside the palette means its role has not been assigned.
            if (!Palette.Any(role =>
                Near(group.Colour.R, role.R) && Near(group.Colour.G, role.G) && Near(group.Colour.B, role.B)))
            {
                Say(findings, group, "no role colour",
                    "The colour is off the palette - user-modifiable inputs [70,110,255], plain function "
                    + "[150,150,150], geometry baked to Rhino [255,60,60], preview-only geometry [255,220,0].");
            }
        }

        // An object shared by two groups forces their frames to overlap, because each frame contains all of its
        // members. Layout cannot separate the frames; removing the object from one of the groups separates them.
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

        // Check frame overlap, excluding a group nested on purpose inside its mother group.
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
                                ? "a note reaches past the members it captions, and the frame drawn around it is "
                                    + "wider than the room the layout reserved. Run arrange, which reserves the "
                                    + "width and height of each group's captions."
                                : "run arrange, which lays groups out as whole blocks."),
                        groups[i].InstanceGuid));
                }
            }
        }

        // Include runtime errors and warnings. Reporting only composition faults can make a definition that does
        // not run appear clean.
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

        // A relay feeding directly into another relay in the same group duplicates the same value. Across group
        // boundaries, the same wiring pattern is an intended signature. Check direct group ownership to distinguish
        // the cases.
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

            // Use the directly owning group, not an ancestor through nesting. Otherwise a mother group contains
            // both ends of a legitimate outlet-to-inlet connection between sibling groups and the connection is
            // incorrectly reported as a duplicate.
            GH_Group? mine = groups.FirstOrDefault(group => group.ObjectIDs.Contains(thing.InstanceGuid));
            GH_Group? theirs = groups.FirstOrDefault(group => group.ObjectIDs.Contains(feeder.InstanceGuid));

            if (mine is not null && ReferenceEquals(mine, theirs))
            {
                findings.Add(Finding(
                    "chained ports",
                    $"'{Named(feeder)}' passes straight into '{Named(thing)}' inside '{mine.NickName}' - two "
                    + "parameters carrying the same value. Keep one and delete the other.",
                    thing.InstanceGuid));
            }
        }

        // An object that neither feeds anything nor draws has no effect and only adds to what a reader must check.
        // Such objects often remain after wiring changes, for example parameters that no longer carry data or
        // components whose output was redirected.
        foreach (IGH_DocumentObject thing in nodes)
        {
            // Producing visible preview output is a purpose even when no object reads the result.
            if (thing is IGH_PreviewObject { IsPreviewCapable: true, Hidden: false })
            {
                continue;
            }

            List<IGH_Param> outputs = [.. OutputsOf(thing)];

            // An object with no outputs may intentionally terminate the dataflow, such as a preview or bake target.
            if (outputs.Count == 0 || outputs.Any(output => output.Recipients.Count > 0))
            {
                continue;
            }

            // Annotations and colour selections do not require outgoing wires.
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

        // Parameter modifiers can hide structural transformations. Simplify is especially unsafe because the items
        // it removes depend on the data received.
        foreach (IGH_DocumentObject thing in nodes)
        {
            foreach (IGH_Param side in Arrange.InputsOf(thing).Concat(OutputsOf(thing)).Distinct())
            {
                if (side.Simplify)
                {
                    findings.Add(Finding(
                        "simplify",
                        $"'{Named(thing)}' has the simplify modifier on '{side.Name}' - never use it. What it "
                        + "drops depends on the data it receives, and the definition behaves differently in "
                        + "another file. Change structure visibly, with a component.",
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

        // Check data matching. A component receiving the same data twice can silently produce duplicated geometry
        // without reporting an error.
        foreach (IGH_Component component in nodes.OfType<IGH_Component>())
        {
            if (Matching(component) is { } finding)
            {
                findings.Add(finding);
            }
        }

        // Check that multiple sources on one input use compatible path depths. Grasshopper concatenates by path,
        // and sources at different depths, such as {0} and {0;0}, do not enter the same branch. The component may
        // process partial data on each pass and produce an unexpected result without an error. For example, an
        // outline at one depth and an offset at another can produce two separate surfaces instead of one surface
        // with a hole.
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
                        + $"{string.Join(" and ", depths)} deep - they never meet in one branch. Each is "
                        + "processed on its own, and the result is not the one list the wiring suggests. Bring "
                        + "them to one depth with a Flatten or Graft component, where a reader can see it.",
                        thing.InstanceGuid));
                }
            }
        }

        // Check whether a component nickname differs from its original. A component's nickname identifies its
        // type, and descriptive names belong on parameters.
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

        // Detect script components because components are normally preferable when available.
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

        // Exclude annotations from the ungrouped-component count. A document-level note does not need a functional
        // group. An unwired panel is an annotation too, although a panel is a parameter and counts among the nodes.
        int loose = nodes.Count(thing =>
            !grouped.Contains(thing.InstanceGuid) && !IsAnnotation(thing));

        if (loose > 0)
        {
            findings.Add(Finding(
                "ungrouped",
                $"{loose} object(s) belong to no group - every component should live in the function that uses it.",
                null));
        }

        // Check whether a note overlaps another object. Arrange places notes as captions after laying out the
        // dataflow. A note can overlap something before that pass, or when it is not in the group it explains.
        // The overlap affects readability and not execution, and it is reported as polish.
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
                    $"A note sits on top of '{Named(other)}' - arrange places notes as captions. Put it in "
                        + "the group it explains, or move it clear.",
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

    /// <summary>Count the other groups to which this group supplies data.</summary>
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

    /// <summary>Compare a colour channel with a tolerance for rounding performed by Grasshopper's colour picker.</summary>
    private static bool Near(int one, int other) => Math.Abs(one - other) <= 12;

    /// <summary>The area occupied by a group's layout members, excluding annotations.</summary>
    /// <remarks>
    /// A frame overlap has two causes, and the finding names which one it is. Layout sizes a group from its nodes
    /// plus a band reserved for its captions. A caption added or lengthened since the last arrange can widen the
    /// frame while the nodes stay apart. Comparing node bodies separates a real layout overlap from a caption
    /// extending beyond it, and arrange answers both.
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
    /// The finding kinds that stop a definition from working. Every other kind is polish.
    /// </summary>
    /// <remarks>
    /// A blocking finding means the definition is incorrect or does not work. A polish finding is a composition
    /// improvement. Callers use the distinction to fix correctness before optional cleanup.
    /// </remarks>
    private static readonly string[] Blocking =
    [
        "error", "multiplies", "shared object", "no signature", "simplify", "hidden mapping",
        "mismatched paths",
    ];

    /// <summary>
    /// Whether an object is an annotation, there to be read and carrying no data.
    /// </summary>
    /// <remarks>
    /// A scribble is always an annotation. A panel is an annotation only when it has no sources or recipients;
    /// a wired panel is a data probe and belongs to the function whose data it observes.
    /// </remarks>
    private static bool IsAnnotation(IGH_DocumentObject thing) =>
        thing is GH_Scribble
        || (thing is GH_Panel panel
            && panel.SourceCount == 0
            && panel.Recipients.Count == 0);

    /// <summary>What data matching makes of a component's inputs, when there is something to say.</summary>
    /// <remarks>
    /// Runs are counted according to <c>GH_Component</c> iteration. Branch <i>i</i> of each input is paired with
    /// branch <i>i</i> of the others, and an input with fewer branches reuses its final branch. The item count for
    /// a paired branch is the longest list, except under shortest-list matching. List inputs count once per branch.
    /// Tree inputs and cross-reference components are excluded because their structure is explicitly selected.
    /// <para>
    /// A component that runs more times than its largest input holds items is applying some data to several
    /// branches of another input. That can be an intended product, such as a grafted input against a list, or the
    /// result of a flattened tree. More than <see cref="Suspicious"/> runs in one branch is blocking; a smaller product may
    /// be intentional, such as a 20 by 20 grid, and is polish. Matching list inputs of similar length are not a
    /// product. For example, 1,500 centres paired with 1,500 radii produce 1,500 circles.
    /// </para>
    /// </remarks>
    private static string? Matching(IGH_Component component)
    {
        if (component is GH_Component { DataComparison: GH_DataComparison.CrossReference })
        {
            return null;
        }

        List<IGH_Param> fed = [.. component.Params.Input
            .Where(input => input.Access != GH_ParamAccess.tree && input.VolatileDataCount > 0)];

        if (fed.Count == 0)
        {
            return null;
        }

        bool shortest = component is GH_Component { DataComparison: GH_DataComparison.ShortestList };
        int branches = fed.Max(input => input.VolatileData.PathCount);

        int Count(IGH_Param input, int at) => input.Access == GH_ParamAccess.list
            ? 1
            : input.VolatileData.get_Branch(Math.Min(at, input.VolatileData.PathCount - 1))?.Count ?? 0;

        long runs = 0;
        int fattest = 0;
        string? uneven = null;

        for (int at = 0; at < branches; at++)
        {
            List<(IGH_Param Input, int Items)> here = [.. fed.Select(input => (input, Count(input, at)))];

            int once = shortest ? here.Min(one => one.Items) : here.Max(one => one.Items);

            runs += once;
            fattest = Math.Max(fattest, once);

            List<(IGH_Param Input, int Items)> lists = [.. here
                .Where(one => one.Input.Access == GH_ParamAccess.item && one.Items > 1)
                .OrderByDescending(one => one.Items)];

            if (uneven is null && lists.Count > 1 && lists[0].Items != lists[^1].Items)
            {
                IGH_Param widest = fed.First(input => input.VolatileData.PathCount == branches);

                uneven = $"{component.Name} pairs '{lists[0].Input.Name}' ({lists[0].Items} items) with "
                    + $"'{lists[^1].Input.Name}' ({lists[^1].Items}) in branch {widest.VolatileData.Paths[at]}, "
                    + (shortest
                        ? "and shortest-list matching drops the items the shorter list has no partner for. "
                        : "and the last item of the shorter list is repeated for the remaining runs. ")
                    + "Confirm that is meant; Repeat Data or Shortest List on the canvas would say it where a "
                    + "reader can see it.";
            }
        }

        long most = fed.Max(input => input.Access == GH_ParamAccess.list
            ? input.VolatileData.PathCount
            : (long)input.VolatileDataCount);

        if (runs > most)
        {
            string held = string.Join(", ", fed.Select(input =>
                $"'{input.Name}' {input.VolatileData.PathCount} branch(es) of {input.VolatileDataCount} item(s)"));

            string what = $"{component.Name} runs {runs} times, up to {fattest} in one branch, more than the "
                + $"{most} items its largest input holds ({held}). Grasshopper pairs branches by index, and an input with fewer branches lends "
                + "its last one to every extra branch. ";

            return fattest > Suspicious
                ? Finding(
                    "multiplies",
                    what + "Look upstream for a flatten or a lost tree structure. If every item of one input "
                    + "really should meet every item of another, a Cross Reference component says so on the "
                    + "canvas.",
                    component.InstanceGuid)
                : Finding(
                    "crosses",
                    what + "A grafted input against a list makes a grid this way. Confirm with peek that this "
                    + "is the intended count.",
                    component.InstanceGuid);
        }

        if (uneven is not null)
        {
            return Finding("uneven lists", uneven, component.InstanceGuid);
        }

        List<string> several = [.. fed
            .Where(input => input.Access == GH_ParamAccess.item
                && Enumerable.Range(0, input.VolatileData.PathCount).Any(at => Count(input, at) > 1))
            .Select(input => $"'{input.Name}'")];

        return several.Count == 0
            ? null
            : Finding(
                "broadcast",
                $"{component.Name} runs {runs} times over {branches} branch(es), once per item of "
                + $"{string.Join(" and ", several)}. Confirm with peek that this is the intended count.",
                component.InstanceGuid);
    }

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

    /// <summary>A parameter that passes data on, as opposed to one that owns or displays a value.</summary>
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
