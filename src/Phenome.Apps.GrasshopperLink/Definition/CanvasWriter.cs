using System.Reflection;
using System.Text;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;

namespace Phenome.Apps.GrasshopperLink.Definition;

/// <summary>
/// Writes a document down as JSON: the recipe, plus the state a pair of eyes would have.
/// </summary>
/// <remarks>
/// The shape is the transcriber's recipe - every object, its wires, its typed-in values - extended with what
/// an agent cannot infer from structure: which objects are selected, which are disabled, which draw their
/// preview in the viewport, how each parameter maps its data (flatten, graft, simplify, reverse), and
/// whether the solver is running at all. The recipe half answers "what is built"; the state half answers
/// "what is the human looking at and touching right now".
/// <para>
/// This plugin references no Phenome library on purpose, but when the components plugin happens to be
/// loaded, its components are recognised by reflection and enriched with the exact operation signature -
/// the same field the transcriber writes. Alone, the canvas is still complete; together, it is exact.
/// </para>
/// </remarks>
internal static class CanvasWriter
{
    /// <summary>The whole document, as one JSON object.</summary>
    internal static string Write(GH_Document? document)
    {
        if (document is null)
        {
            return "{\"document\":null,\"objects\":[]}";
        }

        StringBuilder json = new("{\"document\":{");

        json.Append("\"name\":").Append(Json.Quote(document.DisplayName ?? "unsaved"));

        // Stated outright rather than left to be read off the end of the name. Grasshopper appends an
        // asterisk to DisplayName for some edits and not others -- moving a slider through /set does
        // not earn one -- so a caller pattern-matching the name gets a confident wrong answer. Anyone
        // deciding whether it is safe to close Rhino needs the flag itself.
        json.Append(",\"modified\":").Append(document.IsModified ? "true" : "false");

        // The path, because "is it safe to close" is usually followed by "then save it", and a
        // document that has never been saved has nowhere to go.
        json.Append(",\"path\":").Append(Json.Quote(document.FilePath ?? ""));

        json.Append(",\"solverEnabled\":").Append(GH_Document.EnableSolutions ? "true" : "false");
        json.Append(",\"objectCount\":").Append(Json.Number(document.ObjectCount));
        json.Append("},\"objects\":[");

        bool first = true;

        foreach (IGH_DocumentObject thing in document.Objects)
        {
            if (!first)
            {
                json.Append(',');
            }

            first = false;

            switch (thing)
            {
                case IGH_Component component:
                    Describe(component, json);
                    break;

                case IGH_Param parameter:
                    DescribeLoose(parameter, json);
                    break;

                case Grasshopper.Kernel.Special.GH_Group group:
                    // A group's name is the abstraction: a reader takes in a canvas group by group before
                    // any component, so the name and the membership are the point, not decoration.
                    json.Append("{\"kind\":\"group\",\"id\":").Append(Json.Quote(group.InstanceGuid.ToString()));
                    json.Append(",\"name\":").Append(Json.Quote(group.NickName));

                    if (group.Attributes is { } frame)
                    {
                        json.Append(",\"bounds\":[")
                            .Append(Json.Number((long)frame.Bounds.X)).Append(',')
                            .Append(Json.Number((long)frame.Bounds.Y)).Append(',')
                            .Append(Json.Number((long)frame.Bounds.Width)).Append(',')
                            .Append(Json.Number((long)frame.Bounds.Height)).Append(']');
                    }

                    json.Append(",\"colour\":[")
                        .Append(Json.Number(group.Colour.R)).Append(',')
                        .Append(Json.Number(group.Colour.G)).Append(',')
                        .Append(Json.Number(group.Colour.B)).Append("],\"members\":[");

                    bool firstMember = true;

                    foreach (Guid member in group.ObjectIDs)
                    {
                        if (!firstMember)
                        {
                            json.Append(',');
                        }

                        firstMember = false;
                        json.Append(Json.Quote(member.ToString()));
                    }

                    json.Append("]}");
                    break;

                // A note carried its text and nothing else, which made it the one object on the canvas whose
                // *placement* an agent could not read back - and placement is what goes wrong with notes,
                // because they sit outside the layout pass and land on top of things. Reported from the field
                // in those terms: "I only learned my note was wrong because a human sent me a screenshot."
                // Position and box, then, on the same terms as everything else.
                case Grasshopper.Kernel.Special.GH_Scribble scribble:
                    json.Append("{\"kind\":\"note\",\"id\":").Append(Json.Quote(scribble.InstanceGuid.ToString()));
                    json.Append(",\"text\":").Append(Json.Quote(scribble.Text));
                    At(scribble, json);
                    Box(scribble, json);
                    json.Append('}');
                    break;

                default:
                    json.Append("{\"kind\":\"other\",\"id\":").Append(Json.Quote(thing.InstanceGuid.ToString()));
                    json.Append(",\"name\":").Append(Json.Quote(thing.Name));
                    DescribeState(thing, json);
                    json.Append('}');
                    break;
            }
        }

        return json.Append("]}").ToString();
    }

    /// <summary>
    /// The document as a mermaid flowchart: the shape of a definition, at a fiftieth of the JSON.
    /// </summary>
    /// <remarks>
    /// A reading view, not a writing one. Groups become subgraphs, which is exactly the layer a definition
    /// is meant to be read at, and an agent orienting itself in someone else's canvas can take in fifty
    /// lines of diagram where the full state would be thousands. What it deliberately does not carry is
    /// data: branch and item counts decide whether a definition is correct, and no diagram of topology can
    /// show them - that is what <c>peek</c> is for.
    /// <para>
    /// Node ids are short, and the full guids come back beside the diagram in <c>ids</c>, so whatever the
    /// reader decides to do next it has the addresses to do it with.
    /// </para>
    /// </remarks>
    internal static string Mermaid(GH_Document? document)
    {
        if (document is null)
        {
            return "{\"mermaid\":\"flowchart LR\",\"ids\":{}}";
        }

        // Notes are in here now. They used not to be - the filter took components and parameters, and a
        // scribble is neither - so a note explaining a group rendered as nothing at all: an agent read the
        // chart back and could not see the caption it had just written. Safe to include, because a note has no
        // parameters and is not an active object, so it draws no wires and can never be marked broken; it
        // simply appears inside the subgraph of whichever group it belongs to, which is where a caption
        // belongs. Its own membership is the anchor, so no new field is needed to say what it explains.
        Dictionary<Guid, string> shortId = [];
        List<IGH_DocumentObject> nodes = [.. document.Objects
            .Where(thing => thing is IGH_Component or IGH_Param or Grasshopper.Kernel.Special.GH_Scribble)];

        for (int i = 0; i < nodes.Count; i++)
        {
            shortId[nodes[i].InstanceGuid] = $"n{i}";
        }

        StringBuilder chart = new("flowchart LR\\n");
        HashSet<Guid> drawn = [];
        List<Grasshopper.Kernel.Special.GH_Group> groups =
            [.. document.Objects.OfType<Grasshopper.Kernel.Special.GH_Group>()];

        for (int i = 0; i < groups.Count; i++)
        {
            chart.Append($"  subgraph g{i}[{Label(groups[i].NickName, "unnamed group")}]\\n");

            foreach (Guid member in groups[i].ObjectIDs)
            {
                if (shortId.TryGetValue(member, out string? id) && drawn.Add(member))
                {
                    chart.Append($"    {id}{Node(document, member)}\\n");
                }
            }

            chart.Append("  end\\n");
        }

        foreach (IGH_DocumentObject loose in nodes.Where(thing => !drawn.Contains(thing.InstanceGuid)))
        {
            chart.Append($"  {shortId[loose.InstanceGuid]}{Node(document, loose.InstanceGuid)}\\n");
        }

        // The wires, named where a name earns its keep: which socket a wire lands on matters, which of one
        // output it left rarely does.
        foreach (IGH_DocumentObject thing in nodes)
        {
            foreach (IGH_Param input in Arrange.InputsOf(thing))
            {
                foreach (IGH_Param source in input.Sources)
                {
                    IGH_DocumentObject from = source.Attributes?.GetTopLevel?.DocObject ?? source;

                    if (!shortId.TryGetValue(from.InstanceGuid, out string? tail)
                        || !shortId.TryGetValue(thing.InstanceGuid, out string? head))
                    {
                        continue;
                    }

                    string port = thing is IGH_Component component && component.Params.Input.Count > 1
                        ? $"|{Escape(input.Name)}|"
                        : "";

                    chart.Append($"  {tail} -->{port} {head}\\n");
                }
            }
        }

        // Anything red, marked as such: a picture of a definition should show where it is unhappy.
        List<string> unhappy = [.. nodes
            .Where(thing => thing is IGH_ActiveObject active
                && active.RuntimeMessages(GH_RuntimeMessageLevel.Error).Count > 0)
            .Select(thing => shortId[thing.InstanceGuid])];

        if (unhappy.Count > 0)
        {
            chart.Append("  classDef broken stroke:#c00,stroke-width:2px\\n");
            chart.Append($"  class {string.Join(",", unhappy)} broken\\n");
        }

        StringBuilder ids = new();

        foreach ((Guid guid, string id) in shortId)
        {
            ids.Append(ids.Length > 0 ? "," : "").Append(Json.Quote(id)).Append(':')
                .Append(Json.Quote(guid.ToString()));
        }

        return $"{{\"mermaid\":\"{chart}\",\"ids\":{{{ids}}}}}";
    }

    private static string Node(GH_Document document, Guid id)
    {
        IGH_DocumentObject? thing = document.FindObject(id, topLevelOnly: true);

        if (thing is null)
        {
            return "[?]";
        }

        // A note is drawn as what it is: its own wording, in a shape that is not a component. Rendering it as
        // [Scribble] told a reader nothing - the name of the type is the one thing about a note that does not
        // matter, and its text is the only thing that does.
        if (thing is Grasshopper.Kernel.Special.GH_Scribble note)
        {
            return $"[/{Label(note.Text, "an empty note")}/]";
        }

        // A panel is a parameter, so it keeps its box, but a panel nobody wired is prose rather than data and
        // reads better as its own words.
        if (thing is GH_Panel panel
            && panel.SourceCount == 0
            && panel.Recipients.Count == 0
            && !string.IsNullOrWhiteSpace(panel.UserText))
        {
            return $"[/{Label(panel.UserText, "an empty panel")}/]";
        }

        string name = thing.Name;
        string nickname = thing.NickName;

        return string.IsNullOrWhiteSpace(nickname) || nickname == name
            ? $"[{Label(name, "?")}]"
            : $"[{Label($"{name} · {nickname}", "?")}]";
    }

    /// <summary>A mermaid label: quoted, with the characters that would end the node early taken out.</summary>
    private static string Label(string? text, string fallback) =>
        $"\\\"{Escape(string.IsNullOrWhiteSpace(text) ? fallback : text)}\\\"";

    private static string Escape(string text) => text
        .Replace("\\", "/")
        .Replace("\"", "'")
        .Replace("[", "(")
        .Replace("]", ")")
        .Replace("|", "/")
        .Replace("\n", " ");

    private static void Describe(IGH_Component component, StringBuilder into)
    {
        into.Append("{\"kind\":\"component\",\"id\":").Append(Json.Quote(component.InstanceGuid.ToString()));
        into.Append(",\"name\":").Append(Json.Quote(component.Name));
        into.Append(",\"nickname\":").Append(Json.Quote(component.NickName));
        At(component, into);

        // Which world it comes from: a Phenome component carries its operation's exact signature (found by
        // reflection - see the class remarks), anything else the library an agent would re-express.
        if (PhenomeSignature(component) is { } signature)
        {
            into.Append(",\"phenome\":").Append(Json.Quote(signature));
        }
        else
        {
            into.Append(",\"library\":").Append(Json.Quote(
                global::Grasshopper.Instances.ComponentServer.FindAssemblyByObject(component)?.Name ?? "unknown"));
        }

        DescribeState(component, into);

        into.Append(",\"inputs\":[");

        for (int i = 0; i < component.Params.Input.Count; i++)
        {
            if (i > 0)
            {
                into.Append(',');
            }

            DescribeInput(component.Params.Input[i], into);
        }

        into.Append("],\"outputs\":[");

        for (int i = 0; i < component.Params.Output.Count; i++)
        {
            if (i > 0)
            {
                into.Append(',');
            }

            IGH_Param output = component.Params.Output[i];

            into.Append("{\"name\":").Append(Json.Quote(output.Name));
            DescribeMapping(output, into);
            into.Append('}');
        }

        into.Append("]}");
    }

    /// <summary>A parameter standing on its own - a slider, a panel, a relay holding geometry.</summary>
    private static void DescribeLoose(IGH_Param parameter, StringBuilder into)
    {
        into.Append("{\"kind\":\"param\",\"id\":").Append(Json.Quote(parameter.InstanceGuid.ToString()));
        into.Append(",\"name\":").Append(Json.Quote(parameter.Name));
        into.Append(",\"nickname\":").Append(Json.Quote(parameter.NickName));
        At(parameter, into);

        DescribeState(parameter, into);

        switch (parameter)
        {
            case GH_NumberSlider slider:
                into.Append(",\"slider\":{\"value\":").Append(Json.Number((double)slider.CurrentValue));
                into.Append(",\"minimum\":").Append(Json.Number((double)slider.Slider.Minimum));
                into.Append(",\"maximum\":").Append(Json.Number((double)slider.Slider.Maximum)).Append('}');
                break;

            case GH_Panel panel:
                into.Append(",\"text\":").Append(Json.Quote(panel.UserText));

                // A wired panel shows what flows through it, not its typed text - write both.
                if (panel.SourceCount > 0)
                {
                    DescribeValues(panel, into);
                }

                break;

            default:
                DescribeValues(parameter, into);
                break;
        }

        DescribeMapping(parameter, into);
        DescribeSources(parameter, into);
        into.Append('}');
    }

    /// <summary>
    /// Where the object stands, in canvas coordinates - the textual stand-in for a picture of the canvas:
    /// with positions on every object and bounds on every group, an agent reasons about the layout without
    /// ever being sent a pixel.
    /// </summary>
    private static void At(IGH_DocumentObject thing, StringBuilder into)
    {
        if (thing.Attributes is { } attributes)
        {
            into.Append(",\"at\":[")
                .Append(Json.Number((long)attributes.Pivot.X)).Append(',')
                .Append(Json.Number((long)attributes.Pivot.Y)).Append(']');
        }
    }

    /// <summary>
    /// The rectangle the object covers, which for a note is the whole question.
    /// </summary>
    /// <remarks>
    /// A pivot alone says where something starts and nothing about what it covers, and "does this note overlap
    /// that group" cannot be answered from a point. Written as <c>[x, y, w, h]</c> so an agent can check for an
    /// overlap itself rather than asking a human to look at the screen.
    /// </remarks>
    private static void Box(IGH_DocumentObject thing, StringBuilder into)
    {
        if (thing.Attributes is { } attributes)
        {
            System.Drawing.RectangleF bounds = attributes.Bounds;

            into.Append(",\"box\":[")
                .Append(Json.Number((long)bounds.X)).Append(',')
                .Append(Json.Number((long)bounds.Y)).Append(',')
                .Append(Json.Number((long)bounds.Width)).Append(',')
                .Append(Json.Number((long)bounds.Height)).Append(']');
        }
    }

    /// <summary>
    /// What the eyes see: selection, enablement, preview. Written only when off the default, so the
    /// ordinary object stays one line and the unusual one says what is unusual about it.
    /// </summary>
    private static void DescribeState(IGH_DocumentObject thing, StringBuilder into)
    {
        if (thing.Attributes?.Selected == true)
        {
            into.Append(",\"selected\":true");
        }

        if (thing is IGH_ActiveObject { Locked: true })
        {
            into.Append(",\"enabled\":false");
        }

        if (thing is IGH_PreviewObject { IsPreviewCapable: true } preview)
        {
            into.Append(",\"previewOn\":").Append(preview.Hidden ? "false" : "true");
        }
    }

    /// <summary>Flatten, graft, simplify, reverse - written only when set, like the rest of the state.</summary>
    private static void DescribeMapping(IGH_Param parameter, StringBuilder into)
    {
        if (parameter.DataMapping != GH_DataMapping.None)
        {
            into.Append(",\"mapping\":").Append(Json.Quote(
                parameter.DataMapping == GH_DataMapping.Flatten ? "flatten" : "graft"));
        }

        if (parameter.Simplify)
        {
            into.Append(",\"simplify\":true");
        }

        if (parameter.Reverse)
        {
            into.Append(",\"reverse\":true");
        }
    }

    private static void DescribeInput(IGH_Param input, StringBuilder into)
    {
        into.Append("{\"name\":").Append(Json.Quote(input.Name));

        DescribeMapping(input, into);
        DescribeSources(input, into);

        if (input.SourceCount == 0)
        {
            DescribeValues(input, into);
        }

        into.Append('}');
    }

    /// <summary>Which wires feed this parameter, as the ids of their far ends.</summary>
    private static void DescribeSources(IGH_Param parameter, StringBuilder into)
    {
        if (parameter.SourceCount == 0)
        {
            return;
        }

        into.Append(",\"sources\":[");

        for (int i = 0; i < parameter.Sources.Count; i++)
        {
            if (i > 0)
            {
                into.Append(',');
            }

            IGH_Param source = parameter.Sources[i];
            IGH_DocumentObject owner = source.Attributes?.GetTopLevel?.DocObject ?? source;

            into.Append("{\"id\":").Append(Json.Quote(owner.InstanceGuid.ToString()));

            if (owner is IGH_Component component)
            {
                into.Append(",\"output\":").Append(Json.Number(component.Params.Output.IndexOf(source)));
            }

            into.Append('}');
        }

        into.Append(']');
    }

    /// <summary>What is typed or internalised in a parameter: a count and the first few items.</summary>
    private static void DescribeValues(IGH_Param parameter, StringBuilder into)
    {
        int count = parameter.VolatileDataCount;

        if (count == 0)
        {
            return;
        }

        into.Append(",\"values\":{\"count\":").Append(Json.Number(count)).Append(",\"first\":[");

        int written = 0;

        foreach (IGH_Goo goo in parameter.VolatileData.AllData(skipNulls: true))
        {
            if (written >= 5)
            {
                break;
            }

            if (written > 0)
            {
                into.Append(',');
            }

            written++;
            into.Append(Json.Quote(goo.ToString() ?? ""));
        }

        into.Append("]}");
    }

    /// <summary>
    /// The exact operation signature, when the components plugin is loaded and this is one of its components.
    /// </summary>
    private static string? PhenomeSignature(IGH_Component component)
    {
        Type type = component.GetType();

        if (type.FullName != "Phenome.Apps.Grasshopper.PhenomeComponent")
        {
            return null;
        }

        object? op = type.GetProperty("Op", BindingFlags.Public | BindingFlags.Instance)?.GetValue(component);

        return op?.GetType().GetProperty("Signature")?.GetValue(op) as string;
    }
}
