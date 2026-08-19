using System.Net;
using System.Text;
using System.Text.Json;

using Grasshopper.Kernel;

using Phenome.Apps.GrasshopperLink.Definition;

using static Phenome.Apps.GrasshopperLink.Bridge.Verbs.Plumbing;

namespace Phenome.Apps.GrasshopperLink.Bridge.Verbs;

/// <summary>What the canvas and Rhino say back when asked.</summary>
/// <remarks>
/// Read-only, all of it, which is why these are the verbs an agent may call freely: describing an
/// object, following its wires, peeking at the data on them, and naming what is installed.
/// </remarks>
internal static class Reading
{
    /// <summary>
    /// One object's parameters by name, so nobody has to search the catalogue for something already placed.
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

        // Why an object holds no data is the question describe is reached for, and until now it could not
        // answer it: a locked object has every wire it should and computes nothing, which looks from the
        // outside exactly like a solver that never ran. Hidden is here for the same reason - it explains an
        // absence in the viewport rather than in the data.
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
            // The component's own complaint, which is often the whole answer and was previously only
            // readable by looking at the canvas.
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

    /// <summary>Every wire in the document - the whole picture, which no per-input peek adds up to.</summary>
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

    /// <summary>The whole of one parameter's data, branch by branch - the numbers an assertion stands on.</summary>
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

            // A group is a function, so peeking at one answers with its type as it stands: every port, and
            // the shape of the data on each. The alternative was a verb of its own, but every tool costs
            // its description in every session whether or not anybody calls it - and this is the same
            // question, "what data is here", asked of a bigger thing.
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
    /// A group's current signature, measured: every inlet and outlet, with the branch and item counts that
    /// are the specification, and a few values off each outlet so a result can be recognised.
    /// </summary>
    /// <remarks>
    /// Counts rather than full data on purpose. Peeking at a group with six outlets of a thousand branches
    /// each would flood the very context this verb exists to protect - and the counts are what an assertion
    /// is written against anyway. Whoever needs the values takes the port's own id and peeks at that.
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

        // Said out loud rather than left to be inferred from two empty arrays: a group with no ports has
        // either not been signed yet or is not a function, and both are worth knowing before reading on.
        if (inlets.Count == 0 && outlets.Count == 0)
        {
            json.Append(",\"note\":\"no ports - this group has no signature yet; call signature first\"");
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

        // Where the human is looking, so an empty screenshot can be diagnosed rather than guessed at.
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

    internal static string Plugins()
    {
        // Reported, because it is the prefix the 'shipped' flag is decided against and a reader
        // wondering why something is or is not marked has no other way to see it. Empty means the
        // flag falls back to Grasshopper's own IsCoreLibrary alone.
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

            // Core libraries are the ones shipped with Grasshopper; saying so keeps a list of thirty from
            // reading as thirty things somebody installed.
            //
            // IsCoreLibrary alone is not enough: GhPython.gha lives under the Rhino installation and
            // reports false, so a reader trusting the flag would go looking for who installed a component
            // that came in the box. Anything under the Rhino directory is shipped whatever the flag says.
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

            // Only what is actually loaded: an installed-but-never-loaded plug-in cannot be the thing
            // writing to the console, and listing it would bury the ones that can.
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
    /// What is loaded: Grasshopper libraries and Rhino plug-ins, with where each came from.
    /// </summary>
    /// <remarks>
    /// Added because a message in Rhino's console named a plug-in and there was no way to ask which one that
    /// was, where it lived, or whether it was even still loaded - attributing it took starting a second Rhino
    /// and reproducing the fault. A component's own library shows up in the catalogue already; what was
    /// missing was the list of everything present, which is what you need when the suspect is a plug-in
    /// rather than a component.
    /// </remarks>
    /// <summary>
    /// Where Rhino itself is installed, or empty when it cannot be worked out.
    /// </summary>
    /// <remarks>
    /// Found by walking up from RhinoCommon's own location until a directory holds a <c>Plug-ins</c>
    /// folder, rather than by hardcoding a path with a version number in it or by counting levels.
    /// Counting was the first attempt and it was wrong: RhinoCommon sits in <c>System</c> for the .NET
    /// Framework load and in <c>System\netcore</c> for .NET 7, so one hop up landed on <c>System</c>
    /// and every plug-in path failed to match. A landmark does not care how deep it started.
    /// <para>
    /// Empty is a meaningful answer and callers must check for it: a blank prefix passes StartsWith for
    /// every path there is, which would label every library on the machine as shipped.
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
            // Reflection-only or single-file hosting can leave Location empty; the flag then falls
            // back to Grasshopper's own, which is the pre-existing behaviour rather than a regression.
        }

        return string.Empty;
    }
}
