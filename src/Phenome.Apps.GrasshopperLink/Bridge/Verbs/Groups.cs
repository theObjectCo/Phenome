using System.Net;
using System.Text;
using System.Text.Json;

using Grasshopper.Kernel;

using Phenome.Apps.GrasshopperLink.Definition;

using static Phenome.Apps.GrasshopperLink.Bridge.Verbs.Plumbing;

namespace Phenome.Apps.GrasshopperLink.Bridge.Verbs;

/// <summary>A group is a function; these are the verbs that define one.</summary>
/// <remarks>
/// Declaring its inlets and outlets, taking it apart again, and laying the finished blocks out. Apart
/// from <see cref="Objects"/> because the unit is different - those verbs act on one object, these on a
/// boundary drawn round several.
/// </remarks>
internal static class Groups
{
    internal static string Group(JsonDocument request)
    {
        string author = Author(request);
        string name = Field(request, "name") ?? throw new ArgumentException("group needs 'name'.");

        // No members is not an error: a group declared signature-first has none yet, which is the point.
        List<Guid> asked = request.RootElement.TryGetProperty("ids", out JsonElement ids)
            ? [.. ids.EnumerateArray().Select(id => Guid.Parse(id.GetString()!))]
            : [];

        // Declared up front, before there is a body: this is what lets a definition be built the way code
        // is written - the signature first, the innards after. Answered as a name-to-id map, so the body
        // can wire straight onto them.
        List<(string Name, string? Type)> inlets = Names(request, "inlets");
        List<(string Name, string? Type)> outlets = Names(request, "outlets");
        Dictionary<string, Guid> made = [];

        Guid born = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            EnsureAutosave(document);

            // With an id, this is a rename and recolour of a group that exists - the alternative was an
            // ungroup-and-regroup dance that leaves duplicates behind if anything goes wrong halfway.
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

                already.ExpireCaches();
                global::Grasshopper.Instances.ActiveCanvas?.Refresh();

                return already.InstanceGuid;
            }

            // The ports go down first and the group is drawn around them: a group created empty and then
            // filled has to have its frame recomputed anyway, and an object added to a group that does not
            // yet know its own bounds is how frames end up in the wrong place.
            //
            // A lane per group, stacked down the Y axis and far enough apart to stay apart. arrange will
            // lay the whole thing out properly at the end, but a human watching an agent work needs to
            // read the canvas *while* it is being built - and everything landing in one pile is unreadable
            // exactly when intervening would help most.
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

                    // Marked with the same mark signature uses, because it is the same thing: a group's edge.
                    // Unmarked, a port declared here was recognised only while a wire happened to cross the
                    // boundary at it - so signature could plant a duplicate in front of one, and a declared
                    // outlet at the end of a definition was not counted as an outlet at all.
                    Signature.MarkAsPort(port, "group");

                    port.CreateAttributes();
                    port.Attributes.Pivot = new System.Drawing.PointF(x + offset, at);
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
                // A quarter opacity, the way the reference definitions paint them: the colour names the
                // role, the wires underneath stay readable.
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

            // To the very back of the draw order: a group made around existing groups is the mother, and
            // the mother is painted behind her children or she hides them.
            document.ArrangeObject(group, GH_Arrange.MoveToBack);

            global::Grasshopper.Instances.ActiveCanvas?.Refresh();

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
    /// A parameter to stand at a group's edge. Typed when the caller says so, generic otherwise - and a
    /// generic port carries anything, which is the right default for a signature still being sketched.
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

            string made = Signature.Apply(document, only);

            global::Grasshopper.Instances.ActiveCanvas?.Refresh();

            return made;
        });

        Journal.Append(author, "signature");

        return answer;
    }
}
