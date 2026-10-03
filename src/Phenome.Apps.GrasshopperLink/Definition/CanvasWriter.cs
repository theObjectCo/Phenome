using System.Reflection;
using System.Text;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;

namespace Phenome.Apps.GrasshopperLink.Definition;

/// <summary>
/// Serializes a document as JSON, including both its structure and interactive state.
/// </summary>
/// <remarks>
/// The structural data includes objects, wires, and typed values. State data includes selection, enablement,
/// preview visibility, data mapping, and whether the solver is enabled. The structure records what is built, and
/// the state records the current canvas view.
/// <para>
/// This plugin does not reference the Phenome components library. When that library is loaded, reflection detects
/// its components and adds exact operation signatures. Without it, the output is complete but less specific.
/// </para>
/// </remarks>
internal static class CanvasWriter
{
    /// <summary>Writes the whole document as one JSON object.</summary>
    internal static string Write(GH_Document? document)
    {
        if (document is null)
        {
            return "{\"document\":null,\"objects\":[]}";
        }

        StringBuilder json = new("{\"document\":{");

        json.Append("\"name\":").Append(Json.Quote(document.DisplayName ?? "unsaved"));

        // Expose IsModified directly. Grasshopper adds an asterisk to DisplayName only for some edits, and
        // modifying a slider through /set does not add one. Matching the name for an asterisk can give the wrong
        // answer, and a caller deciding whether closing is safe needs the actual flag.
        json.Append(",\"modified\":").Append(document.IsModified ? "true" : "false");

        // Include the path for a caller that saves after determining that closing is safe. An unsaved document has
        // no target path.
        json.Append(",\"path\":").Append(Json.Quote(document.FilePath ?? ""));

        json.Append(",\"solverEnabled\":").Append(GH_Document.EnableSolutions ? "true" : "false");

        // Report the document's own enablement separately. Grasshopper disables documents it is not displaying, and
        // a disabled document accepts edits without solving them.
        json.Append(",\"enabled\":").Append(document.Enabled ? "true" : "false");
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
                    // A group's name and membership are the primary information needed to understand its role.
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

                // Include the note's text, its position and its bounding box. The caption pass places notes, and a
                // note can overlap other objects; the box makes that visible to the caller.
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
    /// Represents the document as a Mermaid flowchart, which is far smaller than the full JSON state.
    /// </summary>
    /// <remarks>
    /// This is a topology view: each group becomes a subgraph with its objects drawn as nodes inside it. A caller can review the
    /// structure without receiving the full state. Data counts are left out on purpose: a topology diagram has no place for them, and they are
    /// read with <c>peek</c>.
    /// <para>
    /// Node ids are short. The map in <c>ids</c> gives the full guid for each one, which any later operation needs
    /// to address the object.
    /// </para>
    /// </remarks>
    internal static string Mermaid(GH_Document? document)
    {
        if (document is null)
        {
            return "{\"mermaid\":\"flowchart LR\",\"ids\":{}}";
        }

        // Include notes. A note has no parameters and is not an active object: it has no wires and is never marked
        // broken. Its group membership identifies what it explains.
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

        // Label the target input socket when the component has more than one. Source output indices are omitted
        // because the consuming socket is usually the ambiguous end.
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

        // Mark components with runtime errors so failure locations are visible in the diagram.
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

        // Render a note by its text, which is the useful information. Its component type is left out.
        if (thing is Grasshopper.Kernel.Special.GH_Scribble note)
        {
            return $"[/{Label(note.Text, "an empty note")}/]";
        }

        // An unwired panel is treated as an annotation and rendered from its text, the same way as a note.
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

        // Report the exact Phenome operation signature when available; otherwise report the component library.
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

    /// <summary>A parameter standing on its own, such as a slider, a panel or a relay holding geometry.</summary>
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

                // Report the Multiline Data flag because it determines whether the text is emitted as one item or
                // one item per line.
                into.Append(",\"multiline\":").Append(panel.Properties.Multiline ? "true" : "false");

                // A wired panel's runtime value differs from its typed text. Report both.
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
    /// The object's canvas pivot. With group bounds, this allows layout analysis without an image.
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
    /// The object's bounding box, which is required to detect note overlap.
    /// </summary>
    /// <remarks>
    /// A pivot alone does not give coverage or overlap. The box is emitted as <c>[x, y, w, h]</c>, from which
    /// overlap is calculated directly.
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
    /// Writes selection, enablement and preview state. Selection and enablement appear only when they differ
    /// from the default; <c>previewOn</c> appears on every object that can draw.
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

    /// <summary>Data mapping flags: flatten, graft, simplify, and reverse. Only non-default values are emitted.</summary>
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
