using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Phenome.Apps.GrasshopperLink.Definition;

/// <summary>
/// Gives a group the signature of a virtual component: named parameters at its edges, and nothing crossing
/// the boundary except through them.
/// </summary>
/// <remarks>
/// Ports are floating parameters at a group boundary. Wires entering or leaving the group are redirected through
/// them, and internal members connect only to the group's inlets and outlets. The interface can then be inspected
/// without reading the implementation, and internal changes do not modify external wiring.
/// <para>
/// The rule is enforced by an operation because a convention that nothing enforces is easily bypassed.
/// </para>
/// </remarks>
internal static class Signature
{
    /// <summary>
    /// The text that marks a parameter as a port this verb planted. A second run recognises its own ports by it.
    /// </summary>
    /// <remarks>
    /// Without the marker, a repeated call cannot identify the ports planted earlier and may plant more. The
    /// resulting parallel paths can share endpoints: a disconnection then appears to have no effect, data is
    /// duplicated, and deleting a port that looks unused severs live connections.
    /// </remarks>
    private const string Mark = "phenome-link:port";

    /// <summary>
    /// Marks a parameter as a group's inlet or outlet, for the verb that planted it to recognise later.
    /// </summary>
    /// <remarks>
    /// Ports declared by the <c>group</c> verb must receive the same marker as ports planted by
    /// <see cref="Signature"/>. Otherwise <c>signature</c> may plant a duplicate port, and an unwired declared
    /// outlet can be omitted from a terminal group's reported signature.
    /// <para>
    /// The side is recorded explicitly, and wires are not the only source for it. Wire-based inference fails before
    /// a port is wired, which is normal during a signature-first build: an inlet containing a constant could be
    /// classified as neither side, and a newly declared signature could be reported as having no ports.
    /// </para>
    /// </remarks>
    internal static void MarkAsPort(IGH_Param parameter, string planter, string side) =>
        parameter.Description = $"{Mark} - a group's {side}, planted by {planter}.";

    private static bool IsPort(IGH_Param parameter) =>
        parameter.Description?.StartsWith(Mark, StringComparison.Ordinal) == true;

    /// <summary>
    /// The side a port was planted as, or null for one that never recorded it.
    /// </summary>
    /// <remarks>
    /// Null is returned for ports created by an older version that recorded only that the parameter was an edge
    /// port. In that case <see cref="Ports"/> falls back to wire-based classification. Existing documents keep
    /// working, and they get the explicit side only when their ports are replanted.
    /// </remarks>
    private static string? DeclaredSide(IGH_Param parameter) =>
        !IsPort(parameter) ? null
        : parameter.Description!.Contains("a group's inlet", StringComparison.Ordinal) ? "inlet"
        : parameter.Description!.Contains("a group's outlet", StringComparison.Ordinal) ? "outlet"
        : null;

    /// <summary>
    /// A parameter already standing at a boundary, however it got there.
    /// </summary>
    /// <remarks>
    /// This recognizes boundary parameters created by an author, as opposed to ports marked by this verb. A relay
    /// fed from outside the group and read inside it works as an inlet, and planting another port before it creates
    /// a redundant chained pair.
    /// </remarks>
    private static bool StandsAtEdge(IGH_Param parameter, HashSet<Guid> inside)
    {
        // Sliders, panels, swatches, toggles and value lists hold values of their own and are never relays.
        if (HoldsValue(parameter))
        {
            return false;
        }

        bool Outside(IGH_Param end) =>
            !inside.Contains((end.Attributes?.GetTopLevel?.DocObject ?? end).InstanceGuid);

        bool fedFromOutside = parameter.SourceCount > 0 && parameter.Sources.All(Outside);
        bool readInside = parameter.Recipients.Count > 0 && parameter.Recipients.All(reader => !Outside(reader));

        // An inlet takes every source from outside and feeds only readers inside. An outlet is the reverse.
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

        // Reject shared objects before signing. Each owner may create a port for the shared member, and a port
        // belonging to one owner is external to the other: each pass can add more duplicates. Shared objects
        // must be removed from all but one group before signing.
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

        Bridge.Verbs.Plumbing.Solve(document);

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
        // Group crossings by external source. Members fed by the same source share one inlet, and the group
        // exposes one input for that source however many members use it.
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
            // Exclude existing ports from the consumers needing redirection. Otherwise a later run can attempt to
            // route a port's external source back into that same port, leaving it disconnected.
            List<IGH_Param> needy = [.. sinks.Where(sink => !IsPort(sink) && !StandsAtEdge(sink, inside))];

            if (needy.Count == 0)
            {
                continue;
            }

            // Reuse an existing marked port that already carries this source instead of planting a duplicate.
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

            IGH_Param inlet = Like(needy[0], NameFor(source, needy[0]), "inlet");

            // Create attributes only when absent. A second CreateAttributes call can orphan linked parameter
            // attributes; see the explanation in the `add` verb.
            if (inlet.Attributes is null)
            {
                inlet.CreateAttributes();
            }

            inlet.Attributes!.Pivot = new System.Drawing.PointF(left - 90, y);
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
                // Consider all readers outside this group, including ports owned by other groups. If an external
                // port reads an internal member directly, the wire crosses this group's boundary without an outlet.
                // When a consumer is signed before its producer, the producer would otherwise take the consumer's
                // inlet for an existing signature and stay without outlets. Once this group's outlet exists, the
                // external inlet reads that outlet.
                List<IGH_Param> readers = [.. output.Recipients
                    .Where(reader => !inside.Contains(
                        (reader.Attributes?.GetTopLevel?.DocObject ?? reader).InstanceGuid))];

                if (readers.Count == 0)
                {
                    continue;
                }

                // Treat an output already marked as a port, or already functioning as a boundary port, as signed.
                if (member is IGH_Param bare && (IsPort(bare) || StandsAtEdge(bare, inside)))
                {
                    continue;
                }

                // Reuse an existing marked outlet that carries this output and redirect external readers to it.
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

                IGH_Param outlet = Like(output, NameFor(output, output), "outlet");

                // Create attributes only when absent, for the reason described for inlets.
                if (outlet.Attributes is null)
                {
                    outlet.CreateAttributes();
                }

                outlet.Attributes!.Pivot = new System.Drawing.PointF(right + 40, y);
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

    /// <summary>Every object the group holds, at any depth of nesting.</summary>
    internal static HashSet<Guid> Members(GH_Document document, GH_Group group)
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

    /// <summary>A parameter that holds a value of its own and takes no wire in: a slider, a panel and the like.</summary>
    private static bool HoldsValue(IGH_Param parameter) =>
        parameter is GH_NumberSlider
            or GH_Panel
            or GH_ColourSwatch
            or GH_BooleanToggle
            or GH_ValueList
            or GH_MultiDimensionalSlider;

    /// <summary>A floating parameter of the same type as the socket it stands for.</summary>
    /// <remarks>
    /// A value holder is the exception. Copying its type planted a second slider as the outlet of a slider:
    /// a slider takes no wire in, so the copy ignored the one from the original, and every reader past the
    /// group read the copy's default of 0.25. A value holder's outlet is a plain parameter of the data it
    /// gives instead.
    /// </remarks>
    private static IGH_Param Like(IGH_Param shape, string name, string side)
    {
        IGH_Param made = HoldsValue(shape)
            ? Carrier(shape.Type)
            : global::Grasshopper.Instances.ComponentServer.EmitObjectProxy(shape.ComponentGuid)?.CreateInstance()
                as IGH_Param
            ?? new Grasshopper.Kernel.Parameters.Param_GenericObject();

        made.NickName = name;
        MarkAsPort(made, "signature", side);
        made.Access = shape.Access;
        made.Optional = true;

        return made;
    }

    /// <summary>The plain parameter for one kind of data, and a generic one for anything else.</summary>
    private static IGH_Param Carrier(Type data) =>
        data == typeof(Grasshopper.Kernel.Types.GH_Number) ? new Grasshopper.Kernel.Parameters.Param_Number()
        : data == typeof(Grasshopper.Kernel.Types.GH_String) ? new Grasshopper.Kernel.Parameters.Param_String()
        : data == typeof(Grasshopper.Kernel.Types.GH_Boolean) ? new Grasshopper.Kernel.Parameters.Param_Boolean()
        : data == typeof(Grasshopper.Kernel.Types.GH_Colour) ? new Grasshopper.Kernel.Parameters.Param_Colour()
        : data == typeof(Grasshopper.Kernel.Types.GH_Point) ? new Grasshopper.Kernel.Parameters.Param_Point()
        : new Grasshopper.Kernel.Parameters.Param_GenericObject();

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
    /// Determine a group's current inlets and outlets by inspecting port markers and boundary wiring.
    /// </summary>
    /// <remarks>
    /// This operation belongs here because port classification is defined by this file. Ports are recognized either
    /// by this verb's marker or by boundary wiring, and hand-built ports are included.
    ///
    /// A declared port's recorded side takes precedence over wire-based inference; see <see cref="MarkAsPort"/>.
    /// Wire inference is retained for hand-built ports and for older ports that did not record their side.
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

        // A marked port with no recipients is an outlet. Terminal groups produce the definition's output, which
        // no object consumes. Requiring an external recipient would make every terminal group report no outlets,
        // which hides their values in <c>peek</c> and leaves their geometry out of the preview sweep.
        //
        // This fallback is restricted to marked ports. An unmarked parameter fed internally but not read could be
        // a leftover relay and must not be inferred as an outlet.
        bool Terminal(IGH_Param port) =>
            IsPort(port)
                && port.Recipients.Count == 0
                && port.SourceCount > 0
                && port.Sources.All(source => !Outside(source));

        // The counterpart of <c>Terminal</c>: a marked port with no sources is still an inlet when internal members
        // read it. The value may be stored in the parameter itself, as with a knob, and no wire supplies it.
        bool Initial(IGH_Param port) =>
            IsPort(port)
                && port.SourceCount == 0
                && port.Recipients.Count > 0
                && port.Recipients.All(reader => !Outside(reader));

        // Prefer the declared side. If a port has both external sources and external readers, classify it as an
        // inlet and include it only once, because a signature must not place one parameter in both lists.
        string? Side(IGH_Param port) =>
            DeclaredSide(port)
                ?? (port.Sources.Any(Outside) || Initial(port) ? "inlet"
                    : port.Recipients.Any(Outside) || Terminal(port) ? "outlet"
                    : null);

        List<IGH_Param> inlets = [.. ports.Where(port => Side(port) == "inlet")];
        List<IGH_Param> outlets = [.. ports.Where(port => Side(port) == "outlet")];

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
