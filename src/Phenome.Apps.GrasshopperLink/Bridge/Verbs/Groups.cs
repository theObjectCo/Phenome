using System.Net;
using System.Text;
using System.Text.Json;

using Grasshopper.Kernel;

using Phenome.Apps.GrasshopperLink.Definition;

using static Phenome.Apps.GrasshopperLink.Bridge.Verbs.Plumbing;

namespace Phenome.Apps.GrasshopperLink.Bridge.Verbs;

/// <summary>A group is a function. These verbs define one.</summary>
/// <remarks>
/// They declare a group's inlets and outlets, take it apart and lay the finished blocks out. They are kept
/// apart from <see cref="Objects"/> because the unit differs: those act on a single object, these on a boundary
/// around several.
/// </remarks>
internal static class Groups
{
    internal static string Group(JsonDocument request)
    {
        string author = Author(request);
        string name = Field(request, "name") ?? throw new ArgumentException("group needs 'name'.");

        // Absence of members is not an error: a signature-first group has none yet, by design.
        List<Guid> asked = request.RootElement.TryGetProperty("ids", out JsonElement ids)
            ? [.. ids.EnumerateArray().Select(id => Guid.Parse(id.GetString()!))]
            : [];

        // Ports are declared before the body exists, and a definition is built like code: signature first, body
        // after. They are returned as a name-to-id map that the body wires onto directly.
        List<(string Name, string? Type)> inlets = Names(request, "inlets");
        List<(string Name, string? Type)> outlets = Names(request, "outlets");
        Dictionary<string, Guid> made = [];

        Guid born = OnUi(() =>
        {
            GH_Document document = EnsureDocument();

            EnsureAutosave(document);

            // With an id this renames and recolours an existing group. Ungrouping and regrouping instead would
            // leave duplicates if the operation failed partway.
            if (Field(request, "id") is { } existing)
            {
                if (document.FindObject(Guid.Parse(existing), topLevelOnly: true)
                    is not Grasshopper.Kernel.Special.GH_Group already)
                {
                    throw new KeyNotFoundException($"No group {existing} on the canvas.");
                }

                document.UndoUtil.RecordGenericObjectEvent("Phenome Link: group", already);

                already.NickName = name;

                if (request.RootElement.TryGetProperty("colour", out JsonElement recolour))
                {
                    already.Colour = System.Drawing.Color.FromArgb(
                        64,
                        recolour[0].GetInt32(),
                        recolour[1].GetInt32(),
                        recolour[2].GetInt32());
                }

                foreach (Guid id in asked)
                {
                    already.AddObject(id);
                }

                // Ports may be declared against an existing group. Adding a port to a live group must actually
                // add it; silently returning ok with no port is the same fault as dropping a note's text. A
                // group's signature is the part most often edited after the body is written, and this case must
                // be supported.
                //
                // Add only missing ports, matched by nickname: repeating the same declaration adds nothing, and
                // an existing port keeps its wires.
                HashSet<string> already_there = [.. Signature.Members(document, already)
                    .Select(id => document.FindObject(id, topLevelOnly: true))
                    .OfType<IGH_Param>()
                    .Select(port => port.NickName ?? "")];

                System.Drawing.RectangleF frame = already.Attributes?.Bounds
                    ?? new System.Drawing.RectangleF(100, 100, 400, 200);

                foreach ((string side, List<(string Name, string? Type)> these, float column) in
                    new[] { ("inlet", inlets, frame.Left - 90f), ("outlet", outlets, frame.Right + 40f) })
                {
                    float at = frame.Top + 10;

                    foreach ((string what, string? type) in these)
                    {
                        if (already_there.Contains(what))
                        {
                            // Return the id either way: the caller gets the same name-to-id map whether the
                            // port was just created or already existed.
                            IGH_Param? standing = Signature.Members(document, already)
                                .Select(id => document.FindObject(id, topLevelOnly: true))
                                .OfType<IGH_Param>()
                                .FirstOrDefault(port => port.NickName == what);

                            if (standing is not null)
                            {
                                made[what] = standing.InstanceGuid;
                            }

                            continue;
                        }

                        IGH_Param port = PortFor(type);

                        port.NickName = what;
                        Signature.MarkAsPort(port, "group", side);
                        // Create attributes only when the constructor left none; a second CreateAttributes causes
                        // the unclearable wire selection described on `add`.
                        if (port.Attributes is null)
                        {
                            port.CreateAttributes();
                        }

                        port.Attributes!.Pivot = new System.Drawing.PointF(column, at);
                        at += 32;

                        document.AddObject(port, update: false);
                        document.UndoUtil.RecordAddObjectEvent($"Phenome Link: {side}", port);
                        already.AddObject(port.InstanceGuid);

                        made[what] = port.InstanceGuid;
                    }
                }

                already.ExpireCaches();
                global::Grasshopper.Instances.ActiveCanvas?.Refresh();
                Changed(document);

                return already.InstanceGuid;
            }

            // Place the ports first, then draw the group around them: an empty group filled later needs its
            // frame recomputed anyway, and adding an object to a group that does not yet know its bounds puts
            // frames in the wrong place.
            //
            // Give each group its own lane down the Y axis, spaced apart. arrange lays the whole document out at
            // the end, but a user reading the canvas during the build needs it legible while it is built.
            float x = 100;
            float y = 100 + (document.Objects.OfType<Grasshopper.Kernel.Special.GH_Group>().Count() * 260);

            foreach ((string side, List<(string Name, string? Type)> these, float offset) in
                new[] { ("inlet", inlets, 0f), ("outlet", outlets, 900f) })
            {
                float at = y;

                foreach ((string what, string? type) in these)
                {
                    IGH_Param port = PortFor(type);

                    port.NickName = what;

                    // Mark with the same marker signature uses: both are a group's edge. An unmarked port here
                    // is recognised only when a wire happens to cross it. signature can then plant a duplicate,
                    // and a declared outlet at the end is not counted.
                    Signature.MarkAsPort(port, "group", side);

                    // Create attributes only when the constructor left none, as everywhere an object is created.
                    if (port.Attributes is null)
                    {
                        port.CreateAttributes();
                    }

                    port.Attributes!.Pivot = new System.Drawing.PointF(x + offset, at);
                    at += 32;

                    document.AddObject(port, update: false);
                    document.UndoUtil.RecordAddObjectEvent($"Phenome Link: {side}", port);

                    made[what] = port.InstanceGuid;
                }
            }

            Grasshopper.Kernel.Special.GH_Group group = new()
            {
                NickName = name,
            };

            if (request.RootElement.TryGetProperty("colour", out JsonElement colour))
            {
                // Quarter opacity, matching the reference definitions: the colour marks the role while the wires
                // below stay readable.
                group.Colour = System.Drawing.Color.FromArgb(
                    64,
                    colour[0].GetInt32(),
                    colour[1].GetInt32(),
                    colour[2].GetInt32());
            }

            document.AddObject(group, update: false);
            document.UndoUtil.RecordAddObjectEvent("Phenome Link: group", group);

            foreach (Guid id in asked)
            {
                group.AddObject(id);
            }

            foreach (Guid port in made.Values)
            {
                group.AddObject(port);
            }

            group.ExpireCaches();

            // Move to the back of the draw order: a group drawn around existing groups is the mother and must
            // render behind its children, or it hides them.
            document.ArrangeObject(group, GH_Arrange.MoveToBack);

            global::Grasshopper.Instances.ActiveCanvas?.Refresh();
            Changed(document);

            return group.InstanceGuid;
        });

        Journal.Append(author, "group", $",\"id\":{Json.Quote(born.ToString())},\"name\":{Json.Quote(name)}");

        StringBuilder ports = new();

        foreach ((string what, Guid id) in made)
        {
            ports.Append(ports.Length > 0 ? "," : "").Append(Json.Quote(what)).Append(':').Append(Json.Quote(id.ToString()));
        }

        return $"{{\"ok\":true,\"id\":{Json.Quote(born.ToString())},\"ports\":{{{ports}}}}}";
    }

    internal static string Ungroup(JsonDocument request)
    {
        string author = Author(request);
        Guid id = Guid.Parse(Field(request, "id") ?? throw new ArgumentException("ungroup needs 'id'."));

        OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            if (document.FindObject(id, topLevelOnly: true) is not Grasshopper.Kernel.Special.GH_Group group)
            {
                throw new KeyNotFoundException($"No group {id} on the canvas.");
            }

            EnsureAutosave(document);
            document.UndoUtil.RecordRemoveObjectEvent("Phenome Link: ungroup", group);
            document.RemoveObject(group, update: false);
            global::Grasshopper.Instances.ActiveCanvas?.Refresh();
            Changed(document);

            return true;
        });

        Journal.Append(author, "ungroup", $",\"id\":{Json.Quote(id.ToString())}");

        return "{\"ok\":true}";
    }

    /// <summary>
    /// The ports a request lists for one side of a group's signature: a bare name, or a name with a type.
    /// </summary>
    private static List<(string Name, string? Type)> Names(JsonDocument request, string side) =>
        request.RootElement.TryGetProperty(side, out JsonElement these)
            ? [.. these.EnumerateArray().Select(one => one.ValueKind == JsonValueKind.String
                ? (one.GetString()!, (string?)null)
                : (one.GetProperty("name").GetString()!, Text(one, "type")))]
            : [];

    /// <summary>
    /// The parameter placed at a group's edge. Typed when specified, generic otherwise; a generic port carries
    /// any type, which is the right default for an unfinished signature.
    /// </summary>
    private static IGH_Param PortFor(string? type) => (type ?? "").ToLowerInvariant() switch
    {
        "number" or "double" => new Grasshopper.Kernel.Parameters.Param_Number(),
        "integer" or "int" => new Grasshopper.Kernel.Parameters.Param_Integer(),
        "text" or "string" => new Grasshopper.Kernel.Parameters.Param_String(),
        "boolean" or "bool" => new Grasshopper.Kernel.Parameters.Param_Boolean(),
        "point" => new Grasshopper.Kernel.Parameters.Param_Point(),
        "vector" => new Grasshopper.Kernel.Parameters.Param_Vector(),
        "plane" => new Grasshopper.Kernel.Parameters.Param_Plane(),
        "line" => new Grasshopper.Kernel.Parameters.Param_Line(),
        "curve" => new Grasshopper.Kernel.Parameters.Param_Curve(),
        "surface" => new Grasshopper.Kernel.Parameters.Param_Surface(),
        "brep" => new Grasshopper.Kernel.Parameters.Param_Brep(),
        "mesh" => new Grasshopper.Kernel.Parameters.Param_Mesh(),
        "geometry" => new Grasshopper.Kernel.Parameters.Param_Geometry(),
        "interval" or "domain" => new Grasshopper.Kernel.Parameters.Param_Interval(),
        "colour" or "color" => new Grasshopper.Kernel.Parameters.Param_Colour(),
        "transform" => new Grasshopper.Kernel.Parameters.Param_Transform(),
        _ => new Grasshopper.Kernel.Parameters.Param_GenericObject(),
    };

    internal static string DoArrange(JsonDocument request)
    {
        string author = Author(request);

        int moved = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document to arrange.");

            EnsureAutosave(document);

            int count = Arrange.Whole(document);

            global::Grasshopper.Instances.ActiveCanvas?.Refresh();

            // Mark only when something moved. Arranging a settled document is normal and changes nothing;
            // marking unconditionally would raise a save prompt for a no-op.
            if (count > 0)
            {
                Changed(document);
            }

            return count;
        });

        Journal.Append(author, "arrange", $",\"moved\":{Json.Number(moved)}");

        return $"{{\"ok\":true,\"moved\":{Json.Number(moved)}}}";
    }

    internal static string DoSignature(JsonDocument request)
    {
        string author = Author(request);
        Guid? only = Field(request, "id") is { } id ? Guid.Parse(id) : null;

        string answer = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            EnsureAutosave(document);

            // Measure by object count, not by the answer. signature is safe to run twice and plants nothing
            // when ports are already settled; marking unconditionally would always raise a save prompt. Ports
            // are added as objects, and the object count is an accurate measure that needs no new API.
            int before = document.ObjectCount;

            string made = Signature.Apply(document, only);

            global::Grasshopper.Instances.ActiveCanvas?.Refresh();

            if (document.ObjectCount != before)
            {
                Changed(document);
            }

            return made;
        });

        Journal.Append(author, "signature");

        return answer;
    }
}
