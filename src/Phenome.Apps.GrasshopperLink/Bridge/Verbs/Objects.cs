using System.Net;
using System.Text;
using System.Text.Json;

using Grasshopper.Kernel;

using Phenome.Apps.GrasshopperLink.Definition;

using static Phenome.Apps.GrasshopperLink.Bridge.Verbs.Plumbing;

namespace Phenome.Apps.GrasshopperLink.Bridge.Verbs;

/// <summary>Objects, the wires between them, and the values on them.</summary>
/// <remarks>
/// These verbs modify the canvas. Only <c>place</c> rolls a partially applied request back; <c>wire</c> and
/// <c>set</c> apply their elements in order, and a later failure leaves the earlier elements in place.
/// </remarks>
internal static class Objects
{
    internal static string Add(JsonDocument request)
    {
        string author = Author(request);
        string? name = Field(request, "name");
        string? guid = Field(request, "guid");

        if (name is null && guid is null)
        {
            throw new ArgumentException("add needs 'name' or 'guid': which component to put down.");
        }

        Guid id = OnUi(() =>
        {
            IGH_ObjectProxy proxy = guid is not null
                ? global::Grasshopper.Instances.ComponentServer.EmitObjectProxy(Guid.Parse(guid))
                    ?? throw new KeyNotFoundException($"No component with guid {guid} is installed.")
                : global::Grasshopper.Instances.ComponentServer.ObjectProxies
                    .FirstOrDefault(candidate =>
                        !candidate.Obsolete
                        && string.Equals(candidate.Desc.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new KeyNotFoundException($"No component is called '{name}'.");

            IGH_DocumentObject thing = proxy.CreateInstance()
                ?? throw new InvalidOperationException($"{proxy.Desc.Name} would not instantiate.");

            if (Field(request, "nickname") is { } nickname)
            {
                thing.NickName = nickname;
            }

            // Create attributes only when the component constructor did not. Calling CreateAttributes again
            // replaces the component's attributes while leaving parameters parented to the old object. Their
            // wires then resolve selection through an unreachable orphan, which can remain visually selected.
            if (thing.Attributes is null)
            {
                thing.CreateAttributes();
            }

            GH_Document document = EnsureDocument();

            EnsureAutosave(document);

            // Use the requested pivot, or place the object where it will not overlap existing objects.
            // CreateAttributes defaults to the origin, and objects placed without coordinates would otherwise
            // stack there.
            thing.Attributes!.Pivot = request.RootElement.TryGetProperty("pivot", out JsonElement pivot)
                ? new System.Drawing.PointF((float)pivot[0].GetDouble(), (float)pivot[1].GetDouble())
                : FreeLane(document);

            document.AddObject(thing, update: false);
            document.UndoUtil.RecordAddObjectEvent("Phenome Link: add", thing);
            Solve(document);
            Changed(document);

            return thing.InstanceGuid;
        });

        Journal.Append(author, "add", $",\"id\":{Json.Quote(id.ToString())},\"name\":{Json.Quote(name ?? guid!)}");

        return $"{{\"ok\":true,\"id\":{Json.Quote(id.ToString())}}}";
    }

    /// <summary>
    /// One wire, or all of them: a 'wires' array is applied in one pass with a single solution at the end.
    /// </summary>
    /// <remarks>
    /// Batch input avoids repeated solutions: definitions contain many wires, and solving after each one creates
    /// unnecessary round trips and repeated canvas updates. A single wire without the array remains valid.
    /// </remarks>
    internal static string Wire(JsonDocument request)
    {
        string author = Author(request);

        List<JsonElement> asked = request.RootElement.TryGetProperty("wires", out JsonElement many)
            ? [.. many.EnumerateArray()]
            : [request.RootElement];

        int made = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            EnsureAutosave(document);

            int count = 0;

            foreach (JsonElement wire in asked)
            {
                IGH_Param source = End(document, wire, "from", outputSide: true);
                IGH_Param target = End(document, wire, "to", outputSide: false);

                document.UndoUtil.RecordWireEvent("Phenome Link: wire", target);

                if (wire.TryGetProperty("disconnect", out JsonElement take) && AsBool(take))
                {
                    target.RemoveSource(source);
                }
                else
                {
                    target.AddSource(source);
                }

                target.ExpireSolution(false);
                count++;
            }

            Solve(document);
            Changed(document);

            return count;
        });

        Journal.Append(author, "wire", $",\"wires\":{Json.Number(made)}");

        return $"{{\"ok\":true,\"wires\":{Json.Number(made)}}}";
    }

    /// <summary>One value, or all of them: a 'values' array is applied in one pass, one solution at the end.</summary>
    internal static string SetValue(JsonDocument request)
    {
        string author = Author(request);

        List<JsonElement> asked = request.RootElement.TryGetProperty("values", out JsonElement many)
            ? [.. many.EnumerateArray()]
            : [request.RootElement];

        int made = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            EnsureAutosave(document);

            int count = 0;

            foreach (JsonElement one in asked)
            {
                Apply(document, one);
                count++;
            }

            Solve(document);
            Changed(document);

            return count;
        });

        Journal.Append(author, "set", $",\"values\":{Json.Number(made)}");

        return $"{{\"ok\":true,\"values\":{Json.Number(made)}}}";
    }

    private static void Apply(GH_Document document, JsonElement request)
    {
        Guid id = Guid.Parse(Text(request, "id") ?? throw new ArgumentException("set needs 'id'."));

        bool valued = request.TryGetProperty("value", out JsonElement value);
        bool shaping = request.TryGetProperty("nickname", out _)
            || request.TryGetProperty("width", out _)
            || request.TryGetProperty("height", out _);

        if (!valued && !shaping)
        {
            throw new ArgumentException("set needs 'value', or 'nickname', 'width' or 'height'.");
        }

        {
            IGH_DocumentObject thing = document.FindObject(id, topLevelOnly: true)
                ?? throw new KeyNotFoundException($"No object {id} on the canvas.");

            document.UndoUtil.RecordGenericObjectEvent("Phenome Link: set", thing);

            if (shaping)
            {
                Shape(thing, request);
            }

            if (!valued)
            {
                return;
            }

            // With 'param', store the value in the named input. This avoids creating a separate parameter and
            // wire for a constant. The index may arrive as a JSON number or string.
            if (request.TryGetProperty("param", out JsonElement named)
                && named.ValueKind is JsonValueKind.String or JsonValueKind.Number
                && named.ToString() is { } which
                && thing is IGH_Component)
            {
                IGH_Param socket = LocateBy(thing, "input", which);

                Store(socket, value);
                socket.ExpireSolution(false);

                return;
            }

            switch (thing)
            {
                case Grasshopper.Kernel.Special.GH_NumberSlider slider:
                    // Apply bounds before the value so it is clamped against the new range. A string containing
                    // '<' uses Grasshopper's initialization notation, such as "0<50<100".
                    if (request.TryGetProperty("minimum", out JsonElement minimum))
                    {
                        slider.Slider.Minimum = (decimal)AsDouble(minimum);
                    }

                    if (request.TryGetProperty("maximum", out JsonElement maximum))
                    {
                        slider.Slider.Maximum = (decimal)AsDouble(maximum);
                    }

                    if (request.TryGetProperty("decimals", out JsonElement decimals))
                    {
                        slider.Slider.DecimalPlaces = (int)AsDouble(decimals);
                        slider.Slider.Type = (int)AsDouble(decimals) == 0
                            ? Grasshopper.GUI.Base.GH_SliderAccuracy.Integer
                            : Grasshopper.GUI.Base.GH_SliderAccuracy.Float;
                    }

                    // A string is treated as a domain only when it contains '<'. A numeric string such as "42"
                    // is treated as a value.
                    if (value.ValueKind == JsonValueKind.String && value.GetString()!.Contains('<'))
                    {
                        slider.SetInitCode(value.GetString());
                    }
                    else
                    {
                        slider.SetSliderValue((decimal)AsDouble(value));
                    }

                    break;

                case Grasshopper.Kernel.Special.GH_Panel panel:
                    Word(panel, value);
                    break;

                // Allow an existing note's wording to be replaced. Empty text is rejected because a blank note
                // is indistinguishable from one whose text failed to be assigned.
                case Grasshopper.Kernel.Special.GH_Scribble scribble:
                {
                    string said = value.ValueKind == JsonValueKind.String
                        ? value.GetString()!
                        : value.ToString();

                    if (string.IsNullOrWhiteSpace(said))
                    {
                        throw new ArgumentException(
                            "A note needs something to say, and the value was empty. Delete the note if it is " +
                            "no longer wanted; an empty one only looks like a mistake.");
                    }

                    scribble.Text = said;
                    break;
                }

                case Grasshopper.Kernel.Special.GH_BooleanToggle toggle:
                    toggle.Value = AsBool(value);
                    break;

                // A colour swatch stores its value in SwatchColour, not in parameter data, and is assigned
                // directly.
                case Grasshopper.Kernel.Special.GH_ColourSwatch swatch:
                    swatch.SwatchColour = AsColour(value);
                    break;

                case IGH_Param parameter:
                    Store(parameter, value);
                    break;

                default:
                    throw new ArgumentException($"{thing.Name} holds no value to set.");
            }

            thing.ExpireSolution(false);
        }
    }

    /// <summary>A parameter's name, and a panel's size: what <c>set</c> changes besides the value.</summary>
    /// <remarks>
    /// Without this, renaming a floating parameter or sizing a panel takes a script. A new panel uses a large
    /// default box, which makes label-heavy definitions hard to read. A floating parameter's
    /// nickname also becomes the group port name used by <c>signature</c>.
    /// <para>
    /// Only parameters can be renamed. Components retain their original names so readers can identify them.
    /// </para>
    /// </remarks>
    private static void Shape(IGH_DocumentObject thing, JsonElement request)
    {
        if (request.TryGetProperty("nickname", out JsonElement named))
        {
            if (thing is not IGH_Param parameter)
            {
                throw new ArgumentException(
                    $"{thing.Name} is a component, and a component keeps its name. 'nickname' renames a "
                    + "parameter standing on its own, such as a group's inlet or outlet, a slider or a panel.");
            }

            string name = named.ValueKind == JsonValueKind.String ? named.GetString()!.Trim() : "";

            if (name.Length == 0)
            {
                throw new ArgumentException("'nickname' was empty, and a parameter with no name reads as a mistake.");
            }

            parameter.NickName = name;
            parameter.Attributes?.ExpireLayout();
        }

        bool wide = request.TryGetProperty("width", out JsonElement width);
        bool tall = request.TryGetProperty("height", out JsonElement height);

        if (!wide && !tall)
        {
            return;
        }

        if (thing is not Grasshopper.Kernel.Special.GH_Panel panel)
        {
            throw new ArgumentException(
                $"{thing.Name} sizes itself to its content. 'width' and 'height' are for a Panel, the one "
                + "object a person resizes by hand.");
        }

        System.Drawing.RectangleF box = panel.Attributes.Bounds;
        float across = wide ? (float)AsDouble(width) : box.Width;
        float down = tall ? (float)AsDouble(height) : box.Height;

        // Below this a panel cannot show one line of its own text, and the box stops looking like a panel.
        const float Least = 20;

        if (across < Least || down < Least)
        {
            throw new ArgumentException(
                $"A panel {across} by {down} is too small to read; width and height start at {Least}.");
        }

        panel.Attributes.Bounds = new System.Drawing.RectangleF(box.X, box.Y, across, down);
        panel.Attributes.ExpireLayout();
    }

    internal static string Select(JsonDocument request)
    {
        string author = Author(request);
        bool add = request.RootElement.TryGetProperty("add", out JsonElement extend) && AsBool(extend);

        List<Guid> asked = request.RootElement.TryGetProperty("ids", out JsonElement ids)
            ? [.. ids.EnumerateArray().Select(id => Guid.Parse(id.GetString()!))]
            : throw new ArgumentException("select needs 'ids'.");

        OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            if (!add)
            {
                foreach (IGH_DocumentObject thing in document.Objects)
                {
                    if (thing.Attributes is { } attributes)
                    {
                        attributes.Selected = false;
                    }
                }
            }

            foreach (Guid id in asked)
            {
                if (document.FindObject(id, topLevelOnly: true) is { Attributes: { } attributes })
                {
                    attributes.Selected = true;
                }
            }

            global::Grasshopper.Instances.ActiveCanvas?.Refresh();

            return true;
        });

        Journal.Append(author, "select", $",\"count\":{Json.Number(asked.Count)}");

        return "{\"ok\":true}";
    }

    /// <summary>
    /// Removes objects, and refuses unless forced when that would cut a wire to something staying.
    /// </summary>
    /// <remarks>
    /// Deleting an object can sever wires to objects that remain. Potentially severed wires are identified and
    /// returned before any change is made. A bulk deletion can otherwise damage a definition while the targeted
    /// objects appear unused. <c>force:true</c> explicitly accepts those losses.
    /// </remarks>
    internal static string Delete(JsonDocument request)
    {
        string author = Author(request);
        bool force = request.RootElement.TryGetProperty("force", out JsonElement mean) && AsBool(mean);

        List<Guid> asked = request.RootElement.TryGetProperty("ids", out JsonElement ids)
            ? [.. ids.EnumerateArray().Select(id => Guid.Parse(id.GetString()!))]
            : throw new ArgumentException("delete needs 'ids'.");

        string answer = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            HashSet<Guid> going = [.. asked];
            List<string> severed = [];

            foreach (Guid id in asked)
            {
                if (document.FindObject(id, topLevelOnly: true) is not { } leaving)
                {
                    continue;
                }

                foreach (IGH_Param output in OutputsOf(leaving))
                {
                    foreach (IGH_Param reader in output.Recipients)
                    {
                        IGH_DocumentObject owner = reader.Attributes?.GetTopLevel?.DocObject ?? reader;

                        if (!going.Contains(owner.InstanceGuid))
                        {
                            severed.Add($"{Named(leaving)} → {Named(owner)}.{reader.Name}");
                        }
                    }
                }
            }

            if (severed.Count > 0 && !force)
            {
                StringBuilder cuts = new();

                foreach (string cut in severed.Take(20))
                {
                    cuts.Append(cuts.Length > 0 ? "," : "").Append(Json.Quote(cut));
                }

                return $"{{\"ok\":false,\"removed\":0,\"wouldSever\":{Json.Number(severed.Count)},"
                    + $"\"wires\":[{cuts}],\"error\":\"Deleting these would cut {severed.Count} wire(s) to "
                    + "objects that stay. Check the list, then pass force:true to delete them anyway.\"}";
            }

            EnsureAutosave(document);

            int gone = 0;

            foreach (Guid id in asked)
            {
                if (document.FindObject(id, topLevelOnly: true) is { } thing)
                {
                    document.UndoUtil.RecordRemoveObjectEvent("Phenome Link: delete", thing);
                    document.RemoveObject(thing, update: false);
                    gone++;
                }
            }

            Solve(document);
            Changed(document);

            return $"{{\"ok\":true,\"removed\":{Json.Number(gone)},\"severed\":{Json.Number(severed.Count)}}}";
        });

        Journal.Append(author, "delete", $",\"asked\":{Json.Number(asked.Count)}");

        return answer;
    }

    internal static string Place(JsonDocument request)
    {
        string author = Author(request);

        if (!request.RootElement.TryGetProperty("objects", out JsonElement objects))
        {
            throw new ArgumentException("place needs 'objects'.");
        }

        string mapping = OnUi(() =>
        {
            GH_Document document = EnsureDocument();

            EnsureAutosave(document);

            // Without an explicit pivot, place objects in the host group's layout lane or in a free area, where
            // generated objects stay legible while a build is in progress.
            Grasshopper.Kernel.Special.GH_Group? host =
                Field(request, "group") is { } intoGroup
                    ? document.FindObject(Guid.Parse(intoGroup), topLevelOnly: true)
                        as Grasshopper.Kernel.Special.GH_Group
                        ?? throw new KeyNotFoundException($"No group {intoGroup} on the canvas.")
                    : null;

            System.Drawing.PointF lane = host?.Attributes?.Bounds is { } frame
                ? new System.Drawing.PointF(frame.Left + 170, frame.Top + 10)
                : FreeLane(document);

            int laid = 0;

            // Resolve every proxy before placement; the recipe is then atomic. Report every resolution failure
            // in the first response, keyed by the caller's local id. The caller corrects the entire recipe and
            // resends it in one pass.
            List<(IGH_ObjectProxy Proxy, JsonElement Spec)> recipe = [];
            List<string> unresolved = [];

            foreach (JsonElement spec in objects.EnumerateArray())
            {
                try
                {
                    recipe.Add((Resolve(spec), spec));
                }
                catch (Exception refused) when (refused is KeyNotFoundException or ArgumentException)
                {
                    unresolved.Add($"{Which(spec)}: {refused.Message}");
                }
            }

            if (unresolved.Count > 0)
            {
                throw new ArgumentException(
                    $"{unresolved.Count} of {unresolved.Count + recipe.Count} entries could not be resolved, "
                    + "and nothing was placed; the canvas is untouched. Fix all of these and send the recipe "
                    + $"again: {string.Join(" || ", unresolved)}");
            }

            // First pass: every object is created and configured, and each local id is mapped to its real one.
            Dictionary<string, IGH_DocumentObject> made = [];

            // Proxy resolution covers unknown components, but wiring can still fail when a parameter name is
            // wrong. Roll back all objects added by this recipe so partial results never remain on the canvas.
            List<IGH_DocumentObject> added = [];

            try
            {
            foreach ((IGH_ObjectProxy proxy, JsonElement spec) in recipe)
            {
                IGH_DocumentObject thing = Instantiate(proxy, spec, new System.Drawing.PointF(
                    lane.X + (laid % 5 * 150),
                    lane.Y + (laid++ / 5 * 80)));

                document.AddObject(thing, update: false);
                added.Add(thing);
                document.UndoUtil.RecordAddObjectEvent("Phenome Link: place", thing);

                Configure(thing, spec);

                made[spec.TryGetProperty("id", out JsonElement local) && local.GetString() is { } key
                    ? key
                    : thing.InstanceGuid.ToString()] = thing;
            }

            // Second pass: the wires, now that both ends exist. A source id is looked up as a recipe-local key
            // first and as an existing canvas guid second; a recipe can graft onto what is already there.
            foreach (JsonElement spec in objects.EnumerateArray())
            {
                if (!spec.TryGetProperty("inputs", out JsonElement inputs))
                {
                    continue;
                }

                if (!spec.TryGetProperty("id", out JsonElement local) || local.GetString() is not { } key)
                {
                    throw new ArgumentException("an object with 'inputs' needs an 'id' to be found by.");
                }

                if (!made.TryGetValue(key, out IGH_DocumentObject? target))
                {
                    throw new KeyNotFoundException($"'{key}' has inputs but no object of that local id was placed.");
                }

                foreach (JsonElement input in inputs.EnumerateArray())
                {
                    string? which = input.TryGetProperty("param", out JsonElement named) ? named.ToString() : null;
                    IGH_Param sink = LocateBy(target, "input", which);

                    // Accept a constant value directly on an input as an alternative to sources. This is a
                    // documented input shape, and a missing value/source pair is reported clearly.
                    if (input.TryGetProperty("value", out JsonElement constant))
                    {
                        Store(sink, constant);
                        continue;
                    }

                    if (!input.TryGetProperty("sources", out JsonElement sources))
                    {
                        throw new ArgumentException(
                            $"input '{which ?? "0"}' of '{Named(target)}' needs either 'sources' (wires) or "
                            + "'value' (a constant typed into the socket).");
                    }

                    foreach (JsonElement source in sources.EnumerateArray())
                    {
                        if (!source.TryGetProperty("id", out JsonElement fromId))
                        {
                            throw new ArgumentException(
                                $"a source of '{Named(target)}' input '{which ?? "0"}' has no 'id'.");
                        }

                        string from = fromId.GetString()!;

                        IGH_DocumentObject owner = made.TryGetValue(from, out IGH_DocumentObject? fresh)
                            ? fresh
                            : document.FindObject(Guid.Parse(from), topLevelOnly: true)
                                ?? throw new KeyNotFoundException($"'{from}' is neither in the recipe nor on the canvas.");

                        sink.AddSource(LocateBy(
                            owner,
                            "output",
                            source.TryGetProperty("output", out JsonElement outputAt) ? outputAt.ToString() : null));
                    }
                }
            }

            // Add placed objects directly to the requested group. In a signature-first build, the implementation
            // belongs to the function it fills and should not appear as ungrouped.
            if (host is not null)
            {
                foreach (IGH_DocumentObject thing in made.Values)
                {
                    host.AddObject(thing.InstanceGuid);
                }

                host.ExpireCaches();
            }

            Solve(document);
            Changed(document);

            System.Text.StringBuilder json = new("{\"ok\":true,\"placed\":{");
            bool first = true;

            foreach ((string key, IGH_DocumentObject thing) in made)
            {
                if (!first)
                {
                    json.Append(',');
                }

                first = false;
                json.Append(Json.Quote(key)).Append(':').Append(Json.Quote(thing.InstanceGuid.ToString()));
            }

            return json.Append("}}").ToString();
            }
            catch (Exception)
            {
                // Remove objects in reverse creation order: consumers go before the sources they use.
                // Undo entries recorded during placement are left intact; unwinding them here could discard an
                // undo step made by the user.
                for (int i = added.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        document.RemoveObject(added[i], update: false);
                    }
                    catch (Exception)
                    {
                        // Continue removing the remaining objects even if one removal fails. The original
                        // exception is the one reported.
                    }
                }

                if (added.Count > 0)
                {
                    Solve(document);
                }

                throw;
            }
        });

        Journal.Append(author, "place", $",\"objects\":{Json.Number(objects.GetArrayLength())}");

        return mapping;
    }

    /// <summary>
    /// The proxy a recipe entry names, by guid or by a name that must mean exactly one thing.
    /// </summary>
    /// <remarks>
    /// Resolution happens before any object is added, and an unknown or ambiguous name fails without modifying
    /// the document. Ambiguous names return candidate guids instead of choosing one, because a display name may
    /// match multiple components with different parameter layouts.
    /// </remarks>
    private static IGH_ObjectProxy Resolve(JsonElement spec)
    {
        if (spec.TryGetProperty("guid", out JsonElement guid))
        {
            return global::Grasshopper.Instances.ComponentServer.EmitObjectProxy(Guid.Parse(guid.GetString()!))
                ?? throw new KeyNotFoundException($"No component with guid {guid.GetString()}.");
        }

        if (!spec.TryGetProperty("name", out JsonElement name))
        {
            throw new ArgumentException("each placed object needs 'name' or 'guid'.");
        }

        string asked = name.GetString()!;

        List<IGH_ObjectProxy> found = [.. global::Grasshopper.Instances.ComponentServer.ObjectProxies
            .Where(candidate =>
                !candidate.Obsolete
                && string.Equals(candidate.Desc.Name, asked, StringComparison.OrdinalIgnoreCase))];

        if (found.Count == 0)
        {
            throw new KeyNotFoundException($"No component is called '{asked}'.");
        }

        if (found.Count > 1)
        {
            // Return candidates as ready-to-copy JSON object literals. The guid is the discriminator because
            // category and display name can be identical; ComponentGuid is what a .gh file stores and is the
            // stable identifier.
            string candidates = string.Join(", ", found.Select(one =>
                $"{{\"name\":\"{one.Desc.Name}\",\"guid\":\"{one.Guid}\"}} in {one.Desc.Category} › "
                + $"{one.Desc.SubCategory}{Hint(one)}"));

            throw new ArgumentException(
                $"'{asked}' names {found.Count} different components. Copy the intended one, guid and all: "
                + candidates);
        }

        return found[0];
    }

    /// <summary>A candidate's own description, shortened, for the case where the category cannot separate two.</summary>
    private static string Hint(IGH_ObjectProxy candidate)
    {
        string said = (candidate.Desc.Description ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

        return said.Length == 0
            ? ""
            : $" (\"{(said.Length <= 60 ? said : said[..57] + "...")}\")";
    }

    /// <summary>
    /// Which entry of a recipe a complaint is about, said the way the caller wrote it.
    /// </summary>
    /// <remarks>
    /// Identify a recipe entry by its caller-supplied local id when present; otherwise use the requested component
    /// name and finally the entry position. A message such as '6 entries could not be resolved' is actionable only
    /// when each of the six entries is identified.
    /// </remarks>
    private static string Which(JsonElement spec)
    {
        if (spec.TryGetProperty("id", out JsonElement local) && local.GetString() is { Length: > 0 } key)
        {
            return $"'{key}'";
        }

        if (spec.TryGetProperty("name", out JsonElement name) && name.GetString() is { Length: > 0 } asked)
        {
            return $"the entry asking for '{asked}'";
        }

        return "an entry with neither id nor name";
    }

    /// <summary>
    /// Places an unpositioned object below the existing document bounds, clear of them.
    /// </summary>
    /// <remarks>
    /// Definitions are typically built before grouping and layout, so many objects arrive without a group or
    /// requested position. Placing each new object only a few pixels from the previous one causes dense overlap.
    /// A separate staging position keeps a build readable while work is in progress.
    /// <para>
    /// Objects are placed below existing content, not beside it, because dataflow usually advances left to
    /// right. <c>arrange</c> later determines final positions, and callers should not calculate coordinates.
    /// </para>
    /// </remarks>
    private static System.Drawing.PointF FreeLane(GH_Document document)
    {
        float bottom = 0;
        bool any = false;

        foreach (IGH_DocumentObject thing in document.Objects)
        {
            if (thing.Attributes is { } attributes)
            {
                bottom = any ? Math.Max(bottom, attributes.Bounds.Bottom) : attributes.Bounds.Bottom;
                any = true;
            }
        }

        // Leave a vertical gap so a new batch is visually distinct from existing objects.
        return any
            ? new System.Drawing.PointF(100, bottom + 120)
            : new System.Drawing.PointF(100, 100);
    }

    /// <summary>One recipe entry into a live object, from a proxy already resolved.</summary>
    private static IGH_DocumentObject Instantiate(
        IGH_ObjectProxy proxy,
        JsonElement spec,
        System.Drawing.PointF fallback)
    {
        IGH_DocumentObject thing = proxy.CreateInstance()
            ?? throw new InvalidOperationException($"{proxy.Desc.Name} would not instantiate.");

        if (spec.TryGetProperty("nickname", out JsonElement nickname))
        {
            thing.NickName = nickname.GetString() ?? thing.NickName;
        }

        // Create attributes only when absent. See the comment in `add` for why a second call orphans
        // parameter attribute parents and breaks selection handling.
        if (thing.Attributes is null)
        {
            thing.CreateAttributes();
        }

        thing.Attributes!.Pivot = spec.TryGetProperty("pivot", out JsonElement pivot)
            ? new System.Drawing.PointF((float)AsDouble(pivot[0]), (float)AsDouble(pivot[1]))
            : fallback;

        return thing;
    }

    /// <summary>The values a recipe entry carries: a slider's domain, a panel's text, a stored value.</summary>
    private static void Configure(IGH_DocumentObject thing, JsonElement spec)
    {
        if (thing is Grasshopper.Kernel.Special.GH_NumberSlider slider
            && spec.TryGetProperty("slider", out JsonElement domain))
        {
            if (domain.TryGetProperty("minimum", out JsonElement minimum))
            {
                slider.Slider.Minimum = (decimal)AsDouble(minimum);
            }

            if (domain.TryGetProperty("maximum", out JsonElement maximum))
            {
                slider.Slider.Maximum = (decimal)AsDouble(maximum);
            }

            if (domain.TryGetProperty("decimals", out JsonElement decimals))
            {
                slider.Slider.DecimalPlaces = (int)AsDouble(decimals);
                slider.Slider.Type = (int)AsDouble(decimals) == 0
                    ? Grasshopper.GUI.Base.GH_SliderAccuracy.Integer
                    : Grasshopper.GUI.Base.GH_SliderAccuracy.Float;
            }

            if (domain.TryGetProperty("value", out JsonElement at))
            {
                slider.SetSliderValue((decimal)AsDouble(at));
            }

            return;
        }

        if (thing is Grasshopper.Kernel.Special.GH_Panel panel
            && spec.TryGetProperty("text", out JsonElement text))
        {
            Word(panel, text);
            return;
        }

        // Scribbles use the same 'text' field as panels, applied explicitly here. Ignored, the field would leave
        // a note with its default text ("Doubleclick Me!") while the request reported success.
        if (thing is Grasshopper.Kernel.Special.GH_Scribble scribble
            && spec.TryGetProperty("text", out JsonElement wording))
        {
            string said = wording.GetString() ?? "";

            // Reject whitespace-only text because an empty note is indistinguishable from a failed assignment.
            if (string.IsNullOrWhiteSpace(said))
            {
                throw new ArgumentException(
                    "A note needs something to say, and 'text' was empty. An empty scribble looks exactly like " +
                    "one whose text was dropped, which is the fault this refusal exists to prevent.");
            }

            scribble.Text = said;
            return;
        }

        if (thing is IGH_Param parameter && spec.TryGetProperty("value", out JsonElement value))
        {
            Store(parameter, value);
        }
    }

    /// <summary>A panel's text: a string is written as it is, an array is one item per line.</summary>
    /// <remarks>
    /// A new panel has Multiline Data enabled by default, including panels added from the ribbon. With that flag
    /// enabled, a multi-line string such as "-3\n3\n3\n-3" is sent as one text item; feeding it to Construct Point
    /// then fails to parse numbers. An array disables the flag and writes one item per element. A string leaves the
    /// flag unchanged, and replacing text on a manually configured panel does not alter its output shape.
    /// </remarks>
    private static void Word(Grasshopper.Kernel.Special.GH_Panel panel, JsonElement text)
    {
        static string Line(JsonElement one) =>
            one.ValueKind == JsonValueKind.String ? one.GetString()! : one.ToString();

        if (text.ValueKind == JsonValueKind.Array)
        {
            panel.Properties.Multiline = false;
            panel.UserText = string.Join(Environment.NewLine, text.EnumerateArray().Select(Line));
            return;
        }

        panel.UserText = Line(text);
    }

    /// <summary>One end of a wire: the object, and when it is a component, which of its parameters.</summary>
    private static IGH_Param End(GH_Document document, JsonElement request, string which, bool outputSide)
    {
        if (!request.TryGetProperty(which, out JsonElement end))
        {
            throw new ArgumentException($"wire needs '{which}'.");
        }

        Guid id = Guid.Parse(end.GetProperty("id").GetString()!);

        IGH_DocumentObject thing = document.FindObject(id, topLevelOnly: true)
            ?? throw new KeyNotFoundException($"No object {id} on the canvas.");

        if (thing is IGH_Param loose)
        {
            return loose;
        }

        if (thing is not IGH_Component component)
        {
            throw new ArgumentException($"{thing.Name} has no parameters to wire.");
        }

        List<IGH_Param> side = outputSide ? component.Params.Output : component.Params.Input;

        if (!end.TryGetProperty("param", out JsonElement param))
        {
            return side.Count == 1
                ? side[0]
                : throw new ArgumentException(
                    $"{component.Name} has {side.Count} on that side; say which with 'param'.");
        }

        // "0" is an index whether the client sent a number or a string of one: MCP clients do both.
        string asked = param.ValueKind == JsonValueKind.Number
            ? param.GetRawText()
            : param.GetString()!;

        if (int.TryParse(asked, out int index))
        {
            return index >= 0 && index < side.Count
                ? side[index]
                : throw new ArgumentException($"{component.Name} has {side.Count} on that side; {index} is not one of them.");
        }

        return side.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, asked, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.NickName, asked, StringComparison.OrdinalIgnoreCase))
            ?? throw NoParameter(component.Name, asked);
    }

    /// <summary>
    /// A value into a parameter's own storage, replacing whatever was there. A null empties it.
    /// </summary>
    /// <remarks>
    /// Existing persistent data is cleared before assignment because <c>SetPersistentData</c> appends despite its
    /// name. Defaults are also persistent data. Without the clear, setting <c>0</c> on a socket whose default is
    /// already <c>0</c> leaves two zeroes, and a component emits two branches and duplicated geometry.
    /// <para>
    /// An array stores one item per element. A Point parameter can be supplied as <c>["0,0,0", "10.35,0,0"]</c>
    /// or <c>[[0,0,0], [10.35,0,0]]</c>, with no intermediate panel or script.
    /// </para>
    /// </remarks>
    private static void Store(IGH_Param parameter, JsonElement value)
    {
        // SetPersistentData(params object[]) lives on GH_PersistentParam<T>; reflection reaches it on the
        // concrete type. A parameter without it is refused by name instead of silently doing nothing.
        System.Reflection.MethodInfo? set = parameter.GetType().GetMethod("SetPersistentData", [typeof(object[])]);

        object[] raw = value.ValueKind switch
        {
            JsonValueKind.Null => [],
            JsonValueKind.Array => [.. value.EnumerateArray().Select(Item)],
            _ => [Item(value)],
        };

        if (set is null && raw.Length > 0)
        {
            throw new ArgumentException($"{parameter.Name} does not store values.");
        }

        // Grasshopper can silently ignore values that cannot be cast to the parameter type. Test assignment on a
        // temporary parameter first: an invalid array leaves the original socket unchanged and can be reported.
        if (raw.Length > 0 && Blank(parameter) is { } trial)
        {
            set!.Invoke(trial, [raw]);

            int read = trial.GetType().GetProperty("PersistentDataCount")?.GetValue(trial) as int? ?? raw.Length;

            if (read < raw.Length)
            {
                throw new ArgumentException(
                    $"{parameter.Name} reads {read} of {raw.Length} value(s) as {parameter.TypeName}, and nothing "
                    + "was stored." + (parameter.TypeName == "Point" ? " A point is \"x,y,z\" or [x,y,z]." : ""));
            }
        }

        parameter.GetType()
            .GetMethod("Script_ClearPersistentData", Type.EmptyTypes)
            ?.Invoke(parameter, null);

        // A null or empty array clears the parameter's stored value.
        if (raw.Length > 0)
        {
            set!.Invoke(parameter, [raw]);
        }

        // Parameters without a parameterless constructor, including some generated by script components, skip
        // validation on a temporary instance.
        static IGH_Param? Blank(IGH_Param like)
        {
            try
            {
                return Activator.CreateInstance(like.GetType()) as IGH_Param;
            }
            catch (MissingMethodException)
            {
                return null;
            }
        }

        static object Item(JsonElement one) => one.ValueKind switch
        {
            JsonValueKind.Number => one.GetDouble(),
            JsonValueKind.String => one.GetString()!,
            JsonValueKind.True => true,
            JsonValueKind.False => false,

            // Convert a two- or three-number array to a point. Grasshopper converts the point as needed for
            // vector and plane-origin parameters.
            JsonValueKind.Array when one.GetArrayLength() is 2 or 3
                && one.EnumerateArray().All(axis => axis.ValueKind == JsonValueKind.Number) =>
                new Rhino.Geometry.Point3d(
                    one[0].GetDouble(),
                    one[1].GetDouble(),
                    one.GetArrayLength() == 3 ? one[2].GetDouble() : 0),
            _ => throw new ArgumentException(
                "set takes a number, text, a flag, [x,y,z] for a point, an array of those for a list, or null "
                + "to empty the socket."),
        };
    }

    // ---- Plumbing --------------------------------------------------------------------------------------

    internal static string Mapping(JsonDocument request)
    {
        string author = Author(request);
        Guid id = Guid.Parse(Field(request, "id") ?? throw new ArgumentException("param needs 'id'."));

        bool changed = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            IGH_DocumentObject thing = document.FindObject(id, topLevelOnly: true)
                ?? throw new KeyNotFoundException($"No object {id} on the canvas.");

            IGH_Param parameter = Locate(thing, request);

            EnsureAutosave(document);
            document.UndoUtil.RecordGenericObjectEvent("Phenome Link: data mapping", thing);

            if (Field(request, "mapping") is { } mapping)
            {
                parameter.DataMapping = mapping switch
                {
                    "flatten" => GH_DataMapping.Flatten,
                    "graft" => GH_DataMapping.Graft,
                    "none" => GH_DataMapping.None,
                    _ => throw new ArgumentException($"mapping is 'none', 'flatten' or 'graft', not '{mapping}'."),
                };
            }

            if (request.RootElement.TryGetProperty("simplify", out JsonElement simplify))
            {
                parameter.Simplify = AsBool(simplify);
            }

            if (request.RootElement.TryGetProperty("reverse", out JsonElement reverse))
            {
                parameter.Reverse = AsBool(reverse);
            }

            // Expire both the parameter and its owning component. They handle different parts of mapping.
            //
            // Expiring only the parameter clears an output's data, but the owning component is not marked
            // stale, and a later solution can leave that output empty. A graft measured that way returned zero
            // items until the component itself was changed.
            //
            // Expiring only the owner preserves the input's existing volatile data. The new mapping is stored
            // but not applied: an input with graft enabled was measured to retain one branch of four items.
            //
            // Expiring the parameter makes it collect and map its data again, and expiring the owner makes the
            // component recompute with that data. A floating parameter is its own top-level object, and the
            // second call is skipped for it.
            parameter.ExpireSolution(false);

            if ((parameter.Attributes?.GetTopLevel?.DocObject ?? parameter) is IGH_ActiveObject owner
                && !ReferenceEquals(owner, parameter))
            {
                owner.ExpireSolution(false);
            }

            Solve(document);
            Changed(document);

            return true;
        });

        Journal.Append(author, "param", $",\"id\":{Json.Quote(id.ToString())}");

        return $"{{\"ok\":{(changed ? "true" : "false")}}}";
    }
}
