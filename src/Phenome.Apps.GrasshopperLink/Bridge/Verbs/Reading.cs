using System.Net;
using System.Text;
using System.Text.Json;

using Grasshopper.Kernel;

using Phenome.Apps.GrasshopperLink.Definition;

using static Phenome.Apps.GrasshopperLink.Bridge.Verbs.Plumbing;

namespace Phenome.Apps.GrasshopperLink.Bridge.Verbs;

/// <summary>Read-only queries: what the canvas and Rhino report back.</summary>
/// <remarks>
/// None of these changes anything, and an agent may call them freely. They describe an object, follow its
/// wires, peek at the data on them and list what is installed.
/// </remarks>
internal static class Reading
{
    /// <summary>
    /// An object's parameters by name. A placed object then needs no second lookup in the catalogue.
    /// </summary>
    internal static string Describe(Guid id)
    {
        GH_Document document = ActiveDocument()
            ?? throw new InvalidOperationException("There is no document.");

        IGH_DocumentObject thing = document.FindObject(id, topLevelOnly: true)
            ?? throw new KeyNotFoundException($"No object {id} on the canvas.");

        StringBuilder json = new("{\"id\":");

        json.Append(Json.Quote(id.ToString()));
        json.Append(",\"name\":").Append(Json.Quote(thing.Name));
        json.Append(",\"nickname\":").Append(Json.Quote(thing.NickName));

        // Report enabled and drawing because they explain why an object holds no data: a locked object has its
        // wires but computes nothing, which looks like a solver that never ran. Hidden explains an absence in the
        // viewport, not in the data.
        if (thing is IGH_ActiveObject active)
        {
            json.Append(",\"enabled\":").Append(active.Locked ? "false" : "true");
        }

        if (thing is IGH_PreviewObject { IsPreviewCapable: true } previewable)
        {
            json.Append(",\"drawing\":").Append(previewable.Hidden ? "false" : "true");
        }

        if (thing is IGH_ActiveObject { RuntimeMessageLevel: not GH_RuntimeMessageLevel.Blank } said)
        {
            // The component's runtime messages, which are often the whole answer.
            json.Append(",\"messages\":[");

            bool firstMessage = true;

            foreach (GH_RuntimeMessageLevel level in new[]
            {
                GH_RuntimeMessageLevel.Error,
                GH_RuntimeMessageLevel.Warning,
                GH_RuntimeMessageLevel.Remark,
            })
            {
                foreach (string message in said.RuntimeMessages(level))
                {
                    if (!firstMessage)
                    {
                        json.Append(',');
                    }

                    firstMessage = false;
                    json.Append("{\"level\":").Append(Json.Quote(level.ToString().ToLowerInvariant()));
                    json.Append(",\"text\":").Append(Json.Quote(message)).Append('}');
                }
            }

            json.Append(']');
        }

        // A note has no ports: describing its ports returns {inputs:[],outputs:[]} and gives nothing to aim at.
        // Report its wording and position instead, which is what goes wrong with notes.
        if (thing is Grasshopper.Kernel.Special.GH_Scribble note)
        {
            json.Append(",\"annotation\":{\"kind\":\"scribble\",\"text\":").Append(Json.Quote(note.Text));
            Placement(note, json);
            json.Append('}');
        }
        else if (thing is Grasshopper.Kernel.Special.GH_Panel panel)
        {
            // A panel is both a parameter with ports and readable text. Its typed text is not reachable through
            // the ports and is reported the same way.
            json.Append(",\"annotation\":{\"kind\":\"panel\",\"text\":").Append(Json.Quote(panel.UserText));
            Placement(panel, json);
            json.Append('}');
        }

        Ports("inputs", Arrange.InputsOf(thing), json);
        Ports("outputs", OutputsOf(thing), json);

        return json.Append('}').ToString();

        static void Ports(string side, IEnumerable<IGH_Param> these, StringBuilder into)
        {
            into.Append($",\"{side}\":[");

            bool first = true;
            int index = 0;

            foreach (IGH_Param param in these)
            {
                if (!first)
                {
                    into.Append(',');
                }

                first = false;

                into.Append("{\"index\":").Append(Json.Number(index++));
                into.Append(",\"name\":").Append(Json.Quote(param.Name));
                into.Append(",\"nickname\":").Append(Json.Quote(param.NickName));
                into.Append(",\"type\":").Append(Json.Quote(param.TypeName));
                into.Append(",\"access\":").Append(Json.Quote(param.Access.ToString().ToLowerInvariant()));
                into.Append(",\"wired\":").Append(Json.Number(param.SourceCount));
                into.Append(",\"holds\":").Append(Json.Number(param.VolatileDataCount));

                if (param.Optional)
                {
                    into.Append(",\"optional\":true");
                }

                into.Append('}');
            }

            into.Append(']');
        }
    }

    /// <summary>
    /// Where an annotation sits and what it covers, plus the group it belongs to if any.
    /// </summary>
    /// <remarks>
    /// A pivot alone cannot answer whether a note overlaps a group, and overlap is the common note defect: notes
    /// are placed after the dataflow layout, and one can land on a group's sliders and go unnoticed until seen
    /// on screen. A bounding box can be checked without an image.
    /// </remarks>
    private static void Placement(IGH_DocumentObject note, StringBuilder json)
    {
        if (note.Attributes is not { } attributes)
        {
            return;
        }

        System.Drawing.RectangleF bounds = attributes.Bounds;

        json.Append(",\"at\":[")
            .Append(Json.Number((long)attributes.Pivot.X)).Append(',')
            .Append(Json.Number((long)attributes.Pivot.Y)).Append(']');

        json.Append(",\"box\":[")
            .Append(Json.Number((long)bounds.X)).Append(',')
            .Append(Json.Number((long)bounds.Y)).Append(',')
            .Append(Json.Number((long)bounds.Width)).Append(',')
            .Append(Json.Number((long)bounds.Height)).Append(']');

        // Report the group it belongs to, if any: a note explaining a function belongs to that function, and the
        // caller can then query the group's other members.
        if (note.OnPingDocument() is not { } document)
        {
            return;
        }

        foreach (Grasshopper.Kernel.Special.GH_Group group in
            document.Objects.OfType<Grasshopper.Kernel.Special.GH_Group>())
        {
            if (group.ObjectIDs.Contains(note.InstanceGuid))
            {
                json.Append(",\"group\":").Append(Json.Quote(group.InstanceGuid.ToString()));
                json.Append(",\"groupName\":").Append(Json.Quote(group.NickName ?? ""));
                return;
            }
        }
    }

    /// <summary>Every wire in the document, which per-input peeks do not add up to.</summary>
    internal static string Wires()
    {
        GH_Document document = ActiveDocument()
            ?? throw new InvalidOperationException("There is no document.");

        StringBuilder json = new("{\"wires\":[");
        bool first = true;

        foreach (IGH_DocumentObject thing in document.Objects)
        {
            foreach (IGH_Param input in Arrange.InputsOf(thing))
            {
                foreach (IGH_Param source in input.Sources)
                {
                    IGH_DocumentObject from = source.Attributes?.GetTopLevel?.DocObject ?? source;

                    if (!first)
                    {
                        json.Append(',');
                    }

                    first = false;

                    json.Append("{\"from\":{\"id\":").Append(Json.Quote(from.InstanceGuid.ToString()));
                    json.Append(",\"name\":").Append(Json.Quote(Named(from)));

                    if (from is IGH_Component component)
                    {
                        json.Append(",\"param\":").Append(Json.Quote(source.Name));
                    }

                    json.Append("},\"to\":{\"id\":").Append(Json.Quote(thing.InstanceGuid.ToString()));
                    json.Append(",\"name\":").Append(Json.Quote(Named(thing)));
                    json.Append(",\"param\":").Append(Json.Quote(input.Name)).Append("}}");
                }
            }
        }

        return json.Append("]}").ToString();
    }

    /// <summary>One parameter's full data, branch by branch, for asserting against.</summary>
    internal static string Peek(HttpListenerRequest request)
    {
        Guid id = Guid.Parse(request.QueryString["id"] ?? throw new ArgumentException("peek needs ?id=guid."));
        string? side = request.QueryString["side"];
        string? param = request.QueryString["param"];

        return OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            IGH_DocumentObject thing = document.FindObject(id, topLevelOnly: true)
                ?? throw new KeyNotFoundException($"No object {id} on the canvas.");

            // A group is a function, and peeking at it reports its current type: every port and the shape of
            // the data on each. This answers the same question, "what data is here", at group scope. A separate
            // verb would cost a description in every session whether it was used or not.
            if (thing is Grasshopper.Kernel.Special.GH_Group group)
            {
                return PeekGroup(group, document);
            }

            IGH_Param parameter = LocateBy(thing, side, param);

            System.Text.StringBuilder json = new("{\"ok\":true,\"count\":");

            json.Append(Json.Number(parameter.VolatileDataCount)).Append(",\"branches\":[");

            const int Kept = 500;
            int written = 0;
            bool firstBranch = true;

            foreach (Grasshopper.Kernel.Data.GH_Path path in parameter.VolatileData.Paths)
            {
                if (!firstBranch)
                {
                    json.Append(',');
                }

                firstBranch = false;
                json.Append("{\"path\":").Append(Json.Quote(path.ToString())).Append(",\"values\":[");

                System.Collections.IList branch = parameter.VolatileData.get_Branch(path);
                bool firstValue = true;

                foreach (object? item in branch)
                {
                    if (written >= Kept)
                    {
                        break;
                    }

                    if (!firstValue)
                    {
                        json.Append(',');
                    }

                    firstValue = false;
                    written++;
                    json.Append(Json.Quote((item as Grasshopper.Kernel.Types.IGH_Goo)?.ToString() ?? item?.ToString() ?? "null"));
                }

                json.Append("]}");

                if (written >= Kept)
                {
                    break;
                }
            }

            json.Append(']');

            if (written >= Kept)
            {
                json.Append(",\"truncated\":true");
            }

            return json.Append('}').ToString();
        });
    }

    /// <summary>
    /// A group's current signature, measured: every port with its branch and item counts, plus a few values from
    /// each outlet to recognise the result.
    /// </summary>
    /// <remarks>
    /// Counts, not full data: a group with six thousand-branch outlets would flood the context this verb
    /// protects, and assertions are written against counts anyway. Use a port's own id with peek for full values.
    /// </remarks>
    private static string PeekGroup(Grasshopper.Kernel.Special.GH_Group group, GH_Document document)
    {
        (List<IGH_Param> inlets, List<IGH_Param> outlets) = Signature.Ports(document, group);

        System.Text.StringBuilder json = new("{\"ok\":true,\"group\":");

        json.Append(Json.Quote(string.IsNullOrWhiteSpace(group.NickName) ? "(unnamed)" : group.NickName));

        void Side(string name, List<IGH_Param> ports, bool withSample)
        {
            json.Append(",\"").Append(name).Append("\":[");

            for (int at = 0; at < ports.Count; at++)
            {
                IGH_Param port = ports[at];

                if (at > 0)
                {
                    json.Append(',');
                }

                json.Append("{\"name\":").Append(Json.Quote(
                    string.IsNullOrWhiteSpace(port.NickName) ? port.Name : port.NickName));
                json.Append(",\"id\":").Append(Json.Quote(port.InstanceGuid.ToString()));
                json.Append(",\"type\":").Append(Json.Quote(port.TypeName));
                json.Append(",\"count\":").Append(Json.Number(port.VolatileDataCount));
                json.Append(",\"branches\":").Append(Json.Number(port.VolatileData.PathCount));

                if (withSample)
                {
                    json.Append(",\"sample\":[");

                    int taken = 0;

                    foreach (Grasshopper.Kernel.Data.GH_Path path in port.VolatileData.Paths)
                    {
                        foreach (object? item in port.VolatileData.get_Branch(path))
                        {
                            if (taken >= 3)
                            {
                                break;
                            }

                            if (taken > 0)
                            {
                                json.Append(',');
                            }

                            taken++;
                            json.Append(Json.Quote(
                                (item as Grasshopper.Kernel.Types.IGH_Goo)?.ToString() ?? item?.ToString() ?? "null"));
                        }

                        if (taken >= 3)
                        {
                            break;
                        }
                    }

                    json.Append(']');
                }

                json.Append('}');
            }

            json.Append(']');
        }

        Side("inlets", inlets, withSample: false);
        Side("outlets", outlets, withSample: true);

        // State it explicitly instead of leaving two empty arrays to interpret: a portless group is either
        // unsigned or not a function, and the caller should know which before continuing.
        if (inlets.Count == 0 && outlets.Count == 0)
        {
            json.Append(",\"note\":\"no ports: this group has no signature yet; call signature first\"");
        }

        return json.Append('}').ToString();
    }

    internal static string RhinoSummary()
    {
        Rhino.RhinoDoc doc = Rhino.RhinoDoc.ActiveDoc
            ?? throw new InvalidOperationException("There is no Rhino document.");

        System.Text.StringBuilder json = new("{\"name\":");

        json.Append(Json.Quote(string.IsNullOrEmpty(doc.Name) ? "unsaved" : doc.Name));
        json.Append(",\"objects\":").Append(Json.Number(doc.Objects.Count));

        // Report the active camera, which explains an empty screenshot without guessing.
        if (doc.Views.ActiveView is { } view)
        {
            Rhino.Geometry.Point3d eye = view.ActiveViewport.CameraLocation;
            Rhino.Geometry.Point3d at = view.ActiveViewport.CameraTarget;

            json.Append(",\"camera\":{\"name\":").Append(Json.Quote(view.ActiveViewport.Name ?? ""));
            json.Append(",\"eye\":[").Append(Json.Number((long)eye.X)).Append(',')
                .Append(Json.Number((long)eye.Y)).Append(',').Append(Json.Number((long)eye.Z)).Append(']');
            json.Append(",\"target\":[").Append(Json.Number((long)at.X)).Append(',')
                .Append(Json.Number((long)at.Y)).Append(',').Append(Json.Number((long)at.Z)).Append("]}");
        }

        json.Append(",\"layers\":[");

        bool first = true;

        foreach (Rhino.DocObjects.Layer layer in doc.Layers)
        {
            if (layer.IsDeleted)
            {
                continue;
            }

            if (!first)
            {
                json.Append(',');
            }

            first = false;
            json.Append("{\"path\":").Append(Json.Quote(layer.FullPath));

            if (!layer.IsVisible)
            {
                json.Append(",\"visible\":false");
            }

            if (layer.IsLocked)
            {
                json.Append(",\"locked\":true");
            }

            json.Append('}');
        }

        return json.Append("]}").ToString();
    }

    /// <summary>
    /// What is loaded: Grasshopper libraries and Rhino plug-ins, with where each came from.
    /// </summary>
    /// <remarks>
    /// Without this list, a console message naming a plug-in cannot be attributed without starting a second
    /// Rhino and reproducing the fault. A component's library is already visible in the catalogue; this lists
    /// everything present, for when the suspect is a plug-in and not a component.
    /// </remarks>
    internal static string Plugins()
    {
        // rhinoRoot is the prefix the 'shipped' flag is tested against; without it a caller cannot tell why a
        // library is or is not marked. Empty means the flag falls back to IsCoreLibrary alone.
        StringBuilder json = new("{\"ok\":true,\"rhinoRoot\":");
        json.Append(Json.Quote(RhinoRoot)).Append(",\"grasshopper\":[");

        bool first = true;

        foreach (GH_AssemblyInfo library in Grasshopper.Instances.ComponentServer.Libraries
            .OrderBy(library => library.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!first)
            {
                json.Append(',');
            }

            first = false;

            json.Append("{\"name\":").Append(Json.Quote(library.Name ?? ""));
            json.Append(",\"version\":").Append(Json.Quote(library.Version ?? ""));
            json.Append(",\"author\":").Append(Json.Quote(library.AuthorName ?? ""));
            json.Append(",\"path\":").Append(Json.Quote(library.Location ?? ""));

            // Mark core (shipped) libraries; unmarked, a list of thirty reads as thirty installs.
            //
            // IsCoreLibrary alone is insufficient: GhPython.gha lives under the Rhino installation but reports
            // false, and a reader would go looking for an install of a component that came in the box. Anything
            // under the Rhino directory is shipped regardless of the flag.
            bool shipped = library.IsCoreLibrary
                || (RhinoRoot.Length > 0
                    && library.Location is { Length: > 0 } where
                    && where.StartsWith(RhinoRoot, StringComparison.OrdinalIgnoreCase));

            json.Append(",\"shipped\":").Append(shipped ? "true" : "false").Append('}');
        }

        json.Append("],\"rhino\":[");
        first = true;

        foreach (Guid id in Rhino.PlugIns.PlugIn.GetInstalledPlugIns().Keys)
        {
            if (Rhino.PlugIns.PlugIn.GetPlugInInfo(id) is not { } info)
            {
                continue;
            }

            // List only loaded plug-ins: an installed-but-unloaded plug-in cannot write to the console and would
            // bury the ones that can.
            if (!info.IsLoaded)
            {
                continue;
            }

            if (!first)
            {
                json.Append(',');
            }

            first = false;

            json.Append("{\"name\":").Append(Json.Quote(info.Name ?? ""));
            json.Append(",\"version\":").Append(Json.Quote(info.Version ?? ""));
            json.Append(",\"path\":").Append(Json.Quote(info.FileName ?? "")).Append('}');
        }

        return json.Append("]}").ToString();
    }

    /// <summary>
    /// Where Rhino itself is installed, or empty when it cannot be determined.
    /// </summary>
    /// <remarks>
    /// Found by walking up from RhinoCommon's location until a directory contains a <c>Plug-ins</c> folder. The
    /// search neither hardcodes a versioned path nor counts levels. Counting fails because RhinoCommon sits in
    /// <c>System</c> for the .NET Framework load and in <c>System\netcore</c> for .NET 7: a fixed number of hops
    /// lands on <c>System</c>, and every plug-in path fails to match. A landmark does not depend on the starting
    /// depth.
    /// <para>
    /// Empty is a meaningful value callers must check: a blank prefix passes StartsWith for every path, which would
    /// label every library on the machine as shipped.
    /// </para>
    /// </remarks>
    private static readonly string RhinoRoot = ResolveRhinoRoot();

    private static string ResolveRhinoRoot()
    {
        try
        {
            DirectoryInfo? at = new FileInfo(typeof(Rhino.RhinoApp).Assembly.Location).Directory;

            for (int up = 0; up < 6 && at is not null; up++, at = at.Parent)
            {
                if (Directory.Exists(Path.Combine(at.FullName, "Plug-ins")))
                {
                    return at.FullName;
                }
            }
        }
        catch (Exception)
        {
            // Reflection-only or single-file hosting can leave Location empty; the flag then falls back to
            // IsCoreLibrary alone.
        }

        return string.Empty;
    }
}
