using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Phenome.Apps.GrasshopperLink;

/// <summary>
/// Gives a group the signature of a virtual component: named parameters at its edges, and nothing crossing
/// the boundary except through them.
/// </summary>
/// <remarks>
/// This is the discipline that makes a group behave like a function rather than a coloured rectangle. Every
/// wire that enters is re-landed on a floating parameter just inside the left edge, and every wire that
/// leaves departs from one at the right edge; the members then talk only to their own inlets and outlets.
/// A reader - or an agent editing later - can then take in what a group needs and what it yields without
/// reading a single component inside it, and moving or replacing the innards touches nothing outside.
/// <para>
/// Written as a verb rather than left to doctrine on purpose: a rule that nothing performs is a rule that
/// loses to convenience, which is exactly what the first agent-built definitions showed.
/// </para>
/// </remarks>
internal static class Signature
{
    /// <summary>
    /// What marks a parameter as a port this verb planted, so running it again recognises its own work.
    /// </summary>
    /// <remarks>
    /// Idempotence is not a nicety here. Without a mark, a second call cannot tell a port from any other
    /// parameter, so it plants another one - and a third call another - each carrying a copy of the wires
    /// through the boundary. A canvas then holds parallel chains that share endpoints: disconnecting one
    /// appears to do nothing (the twin still feeds the target), data arrives doubled, and deleting the
    /// "unused" copies severs the live ones. That is precisely the wreck reported from the field, and it
    /// traces back to this one missing mark.
    /// </remarks>
    private const string Mark = "phenome-link:port";

    private static bool IsPort(IGH_Param parameter) =>
        parameter.Description?.StartsWith(Mark, StringComparison.Ordinal) == true;

    /// <summary>
    /// A parameter already standing at a boundary, whoever put it there.
    /// </summary>
    /// <remarks>
    /// The mark recognises this verb's own work; this recognises an author's. A lone relay whose wires all
    /// come from outside and go inside is an inlet by construction, and planting a second one in front of
    /// it is how a canvas ends up with pairs of parameters chained nose to tail - which is exactly what
    /// happened to an author who named their own ports.
    /// </remarks>
    private static bool StandsAtEdge(IGH_Param parameter, HashSet<Guid> inside)
    {
        // A slider, a panel, a swatch: things with values of their own, not relays.
        if (parameter is Grasshopper.Kernel.Special.GH_NumberSlider
            or Grasshopper.Kernel.Special.GH_Panel
            or Grasshopper.Kernel.Special.GH_ColourSwatch
            or Grasshopper.Kernel.Special.GH_BooleanToggle)
        {
            return false;
        }

        bool Outside(IGH_Param end) =>
            !inside.Contains((end.Attributes?.GetTopLevel?.DocObject ?? end).InstanceGuid);

        bool fedFromOutside = parameter.SourceCount > 0 && parameter.Sources.All(Outside);
        bool readInside = parameter.Recipients.Count > 0 && parameter.Recipients.All(reader => !Outside(reader));

        // An inlet: everything in from outside, everything out to inside. An outlet is the mirror.
        if (fedFromOutside && readInside)
        {
            return true;
        }

        bool fedInside = parameter.SourceCount > 0 && parameter.Sources.All(source => !Outside(source));
        bool readOutside = parameter.Recipients.Count > 0 && parameter.Recipients.All(Outside);

        return fedInside && readOutside;
    }

    /// <summary>Gives every group a signature, or just one when asked. Returns what it added.</summary>
    internal static string Apply(GH_Document document, Guid? only)
    {
        List<GH_Group> groups = [.. document.Objects.OfType<GH_Group>()
            .Where(group => only is null || group.InstanceGuid == only)];

        if (groups.Count == 0)
        {
            throw new KeyNotFoundException(only is null
                ? "There are no groups on the canvas."
                : $"No group {only} on the canvas.");
        }

        // Refused outright while any object belongs to two groups. Both owners consider it theirs and each
        // wants a port for it, and the port one plants is an outsider to the other - so every run adds two
        // more, forever. A field report counted twenty-six strays and hours of hand-rewiring from exactly
        // this. It cannot be signed sensibly; it has to be un-shared first, and review says which objects.
        List<string> shared = [];

        foreach (GH_Group group in document.Objects.OfType<GH_Group>())
        {
            foreach (Guid member in group.ObjectIDs)
            {
                if (document.Objects.OfType<GH_Group>().Count(other => other.ObjectIDs.Contains(member)) > 1
                    && document.FindObject(member, topLevelOnly: true) is { } thing
                    && !shared.Contains(Name(thing)))
                {
                    shared.Add(Name(thing));
                }
            }
        }

        if (shared.Count > 0)
        {
            throw new InvalidOperationException(
                $"{shared.Count} object(s) belong to more than one group ({string.Join(", ", shared.Take(8))}"
                + (shared.Count > 8 ? ", …" : "")
                + "). Signing that would plant a port per owner and grow by two on every run. Take each "
                + "object out of all but one group first - review lists them as 'shared object'.");
        }

        System.Text.StringBuilder json = new("{\"ok\":true,\"groups\":[");
        bool first = true;

        foreach (GH_Group group in groups)
        {
            if (!first)
            {
                json.Append(',');
            }

            first = false;

            (int inlets, int outlets) = Give(document, group);

            json.Append("{\"id\":").Append(Json.Quote(group.InstanceGuid.ToString()));
            json.Append(",\"name\":").Append(Json.Quote(group.NickName));
            json.Append(",\"inlets\":").Append(Json.Number(inlets));
            json.Append(",\"outlets\":").Append(Json.Number(outlets)).Append('}');
        }

        document.NewSolution(false);

        return json.Append("]}").ToString();
    }

    private static (int Inlets, int Outlets) Give(GH_Document document, GH_Group group)
    {
        HashSet<Guid> inside = Members(document, group);

        List<IGH_DocumentObject> members = [.. inside
            .Select(id => document.FindObject(id, topLevelOnly: true))
            .Where(thing => thing is not null and not GH_Group)
            .Cast<IGH_DocumentObject>()];

        if (members.Count == 0)
        {
            return (0, 0);
        }

        float left = members.Min(thing => thing.Attributes!.Bounds.Left);
        float right = members.Max(thing => thing.Attributes!.Bounds.Right);
        float top = members.Min(thing => thing.Attributes!.Bounds.Top);

        int inlets = Inlets(document, group, members, inside, left, top);
        int outlets = Outlets(document, group, members, inside, right, top);

        group.ExpireCaches();

        return (inlets, outlets);
    }

    /// <summary>One inlet per external source, whatever it feeds inside.</summary>
    private static int Inlets(
        GH_Document document,
        GH_Group group,
        List<IGH_DocumentObject> members,
        HashSet<Guid> inside,
        float left,
        float top)
    {
        // Grouped by the far end of the wire: two members fed by the same slider share one inlet, which is
        // the point - the group takes one value, not one per use.
        Dictionary<IGH_Param, List<IGH_Param>> crossings = [];

        foreach (IGH_DocumentObject member in members)
        {
            foreach (IGH_Param input in Arrange.InputsOf(member))
            {
                foreach (IGH_Param source in input.Sources.ToArray())
                {
                    IGH_DocumentObject from = source.Attributes?.GetTopLevel?.DocObject ?? source;

                    if (inside.Contains(from.InstanceGuid))
                    {
                        continue;
                    }

                    if (!crossings.TryGetValue(source, out List<IGH_Param>? sinks))
                    {
                        crossings[source] = sinks = [];
                    }

                    sinks.Add(input);
                }
            }
        }

        int made = 0;
        float y = top;

        foreach ((IGH_Param source, List<IGH_Param> sinks) in crossings)
        {
            // A port of ours is *supposed* to take a wire from outside - that is its whole job. Leaving it
            // in this list is how a second run told a port to stop listening to the slider and listen to
            // itself instead, which GH refuses, leaving the port fed by nothing at all.
            List<IGH_Param> needy = [.. sinks.Where(sink => !IsPort(sink) && !StandsAtEdge(sink, inside))];

            if (needy.Count == 0)
            {
                continue;
            }

            // A port of ours already carries this source in - reuse it rather than planting a twin.
            if (members.OfType<IGH_Param>()
                    .FirstOrDefault(port => IsPort(port) && port.Sources.Contains(source))
                is { } known)
            {
                foreach (IGH_Param sink in needy.Where(sink => !ReferenceEquals(sink, known)))
                {
                    sink.RemoveSource(source);
                    sink.AddSource(known);
                }

                continue;
            }

            IGH_Param inlet = Like(needy[0], NameFor(source, needy[0]));

            inlet.CreateAttributes();
            inlet.Attributes.Pivot = new System.Drawing.PointF(left - 90, y);
            y += 30;

            document.AddObject(inlet, update: false);
            document.UndoUtil.RecordAddObjectEvent("Phenome Link: signature", inlet);

            inlet.AddSource(source);

            foreach (IGH_Param sink in needy)
            {
                sink.RemoveSource(source);
                sink.AddSource(inlet);
            }

            group.AddObject(inlet.InstanceGuid);
            made++;
        }

        return made;
    }

    /// <summary>One outlet per internal output that anything outside reads.</summary>
    private static int Outlets(
        GH_Document document,
        GH_Group group,
        List<IGH_DocumentObject> members,
        HashSet<Guid> inside,
        float right,
        float top)
    {
        int made = 0;
        float y = top;

        foreach (IGH_DocumentObject member in members)
        {
            foreach (IGH_Param output in OutputsOf(member))
            {
                // A port reading this output is the signature already working - mine or another group's -
                // so it is not a crossing to fix. Without this, ports beget ports.
                List<IGH_Param> readers = [.. output.Recipients
                    .Where(reader => !inside.Contains(
                        (reader.Attributes?.GetTopLevel?.DocObject ?? reader).InstanceGuid))
                    .Where(reader => !IsPort(reader))];

                if (readers.Count == 0)
                {
                    continue;
                }

                // Already a port - ours by the mark, or the author's by where it stands.
                if (member is IGH_Param bare && (IsPort(bare) || StandsAtEdge(bare, inside)))
                {
                    continue;
                }

                // A port of ours already carries this output out - send the outside readers to it.
                if (members.OfType<IGH_Param>()
                        .FirstOrDefault(port => IsPort(port) && port.Sources.Contains(output))
                    is { } known)
                {
                    foreach (IGH_Param reader in readers)
                    {
                        reader.RemoveSource(output);
                        reader.AddSource(known);
                    }

                    continue;
                }

                IGH_Param outlet = Like(output, NameFor(output, output));

                outlet.CreateAttributes();
                outlet.Attributes.Pivot = new System.Drawing.PointF(right + 40, y);
                y += 30;

                document.AddObject(outlet, update: false);
                document.UndoUtil.RecordAddObjectEvent("Phenome Link: signature", outlet);

                outlet.AddSource(output);

                foreach (IGH_Param reader in readers)
                {
                    reader.RemoveSource(output);
                    reader.AddSource(outlet);
                }

                group.AddObject(outlet.InstanceGuid);
                made++;
            }
        }

        return made;
    }

    /// <summary>Every object the group holds, through one level of nesting or ten.</summary>
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

                if (document.FindObject(member, topLevelOnly: true) is GH_Group child
                    && !ReferenceEquals(child, at))
                {
                    pending.Enqueue(child);
                }
            }
        }

        return inside;
    }

    /// <summary>A floating parameter of the same type as the socket it stands for.</summary>
    private static IGH_Param Like(IGH_Param shape, string name)
    {
        IGH_Param made =
            global::Grasshopper.Instances.ComponentServer.EmitObjectProxy(shape.ComponentGuid)?.CreateInstance()
                as IGH_Param
            ?? new Grasshopper.Kernel.Parameters.Param_GenericObject();

        made.NickName = name;
        made.Description = $"{Mark} - a group's edge, planted by signature.";
        made.Access = shape.Access;
        made.Optional = true;

        return made;
    }

    /// <summary>
    /// A name a reader can use: what the wire was called where it came from, or where it lands.
    /// </summary>
    private static string NameFor(IGH_Param source, IGH_Param sink)
    {
        string from = source.NickName;

        if (!string.IsNullOrWhiteSpace(from) && from.Length > 1)
        {
            return from;
        }

        return string.IsNullOrWhiteSpace(sink.Name) ? "Value" : sink.Name;
    }

    /// <summary>
    /// A group's ports as they stand right now: what comes in, what goes out, and the shape of the data on
    /// each. The group's current type, in other words, read rather than declared.
    /// </summary>
    /// <remarks>
    /// This lives here because this file owns what a port <em>is</em>. Two kinds count: one this verb
    /// planted, which carries the mark, and one an author planted themselves, which is recognised the same
    /// way <see cref="StandsAtEdge"/> recognises it while signing - otherwise a hand-built group would look
    /// like it had no signature at all.
    ///
    /// The direction is not stored anywhere and does not need to be: a port fed from outside the group is
    /// an inlet, one read from outside is an outlet. That is the same rule the planting uses, so the two
    /// cannot disagree.
    /// </remarks>
    internal static (List<IGH_Param> Inlets, List<IGH_Param> Outlets) Ports(GH_Document document, GH_Group group)
    {
        HashSet<Guid> inside = Members(document, group);

        List<IGH_Param> ports = [.. inside
            .Select(id => document.FindObject(id, topLevelOnly: true))
            .OfType<IGH_Param>()
            .Where(parameter => IsPort(parameter) || StandsAtEdge(parameter, inside))];

        bool Outside(IGH_Param end) =>
            !inside.Contains((end.Attributes?.GetTopLevel?.DocObject ?? end).InstanceGuid);

        List<IGH_Param> inlets = [.. ports.Where(port => port.Sources.Any(Outside))];
        List<IGH_Param> outlets = [.. ports.Where(port => port.Recipients.Any(Outside))];

        // A port wired both ways is an inlet: it takes from outside first, and calling it both would count
        // one object twice in a signature that is meant to read as a function's type.
        outlets.RemoveAll(inlets.Contains);

        return (inlets, outlets);
    }

    private static string Name(IGH_DocumentObject thing) =>
        string.IsNullOrWhiteSpace(thing.NickName) ? thing.Name : thing.NickName;

    private static IEnumerable<IGH_Param> OutputsOf(IGH_DocumentObject thing) => thing switch
    {
        IGH_Component component => component.Params.Output,
        IGH_Param parameter => [parameter],
        _ => [],
    };
}
