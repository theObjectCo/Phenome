using System.Net;
using System.Text;
using System.Text.Json;

using Grasshopper.Kernel;

using Phenome.Apps.GrasshopperLink.Definition;

using static Phenome.Apps.GrasshopperLink.Bridge.Verbs.Plumbing;

namespace Phenome.Apps.GrasshopperLink.Bridge.Verbs;

/// <summary>Objects, the wires between them, and the values on them.</summary>
/// <remarks>
/// The write half of the protocol: this is where a request turns into something on the canvas, so this
/// is where atomicity lives - a verb that adds several objects and then finds the seventh wire misspelt
/// has to leave nothing behind.
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
            throw new ArgumentException("add needs 'name' or 'guid' - which component to put down.");
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

            thing.CreateAttributes();

            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document to add to.");

            EnsureAutosave(document);

            // A pivot if one was asked for, and otherwise clear of what is already there. CreateAttributes
            // leaves an object on the origin, so `add` without a pivot used to stack every component on the
            // same spot - which is one half of the pile reported from the field, `place` without a group being
            // the other. Neither caller should have to know a coordinate to avoid it.
            thing.Attributes.Pivot = request.RootElement.TryGetProperty("pivot", out JsonElement pivot)
                ? new System.Drawing.PointF((float)pivot[0].GetDouble(), (float)pivot[1].GetDouble())
                : FreeLane(document);

            document.AddObject(thing, update: false);
            document.UndoUtil.RecordAddObjectEvent("Phenome Link: add", thing);
            document.NewSolution(false);
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
    /// The batch is the point rather than a convenience. A definition is mostly wires, and one call per
    /// wire means one round trip, one permission thought and one solution each - the agent spends its
    /// afternoon on plumbing, and the human watches the canvas flicker forty times.
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

            document.NewSolution(false);
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

            document.NewSolution(false);
            Changed(document);

            return count;
        });

        Journal.Append(author, "set", $",\"values\":{Json.Number(made)}");

        return $"{{\"ok\":true,\"values\":{Json.Number(made)}}}";
    }

    private static void Apply(GH_Document document, JsonElement request)
    {
        Guid id = Guid.Parse(Text(request, "id") ?? throw new ArgumentException("set needs 'id'."));

        if (!request.TryGetProperty("value", out JsonElement value))
        {
            throw new ArgumentException("set needs 'value'.");
        }

        {
            IGH_DocumentObject thing = document.FindObject(id, topLevelOnly: true)
                ?? throw new KeyNotFoundException($"No object {id} on the canvas.");

            document.UndoUtil.RecordGenericObjectEvent("Phenome Link: set", thing);

            // With 'param', the value goes into a component's own input - the way a human types a constant
            // straight into a socket instead of standing up a parameter and a wire for the number two.
            if (Text(request, "param") is { } which && thing is IGH_Component)
            {
                IGH_Param socket = LocateBy(thing, "input", which);

                Store(socket, value);
                socket.ExpireSolution(false);

                return;
            }

            switch (thing)
            {
                case Grasshopper.Kernel.Special.GH_NumberSlider slider:
                    // Bounds before value, so the value is clamped against where the slider is going, not
                    // where it was. A string value is GH's own init notation - "0<50<100" says all three.
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

                    // Only a domain expression goes through the init code; "42" spelt as a string is a
                    // number a client was too casual about, not a domain.
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
                    panel.UserText = value.ValueKind == JsonValueKind.String
                        ? value.GetString()!
                        : value.ToString();
                    break;

                // Rewording a note that is already on the canvas, which is the repair path when the first
                // wording was wrong - and there was none: this answered "Scribble holds no value to set", so a
                // note could be created and never corrected. An empty string is refused for the same reason it
                // is on create: a blank scribble is indistinguishable from a dropped one.
                case Grasshopper.Kernel.Special.GH_Scribble scribble:
                {
                    string said = value.ValueKind == JsonValueKind.String
                        ? value.GetString()!
                        : value.ToString();

                    if (string.IsNullOrWhiteSpace(said))
                    {
                        throw new ArgumentException(
                            "A note needs something to say - the value was empty. Delete the note if it is " +
                            "no longer wanted; an empty one only looks like a mistake.");
                    }

                    scribble.Text = said;
                    break;
                }

                case Grasshopper.Kernel.Special.GH_BooleanToggle toggle:
                    toggle.Value = AsBool(value);
                    break;

                // A swatch keeps its colour in a property of its own rather than as parameter data, so the
                // generic path refused it - and a definition whose whole point is four coloured shelf edges
                // could not be coloured.
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
    /// Removes objects - and refuses, unless forced, when that would cut a wire to something staying.
    /// </summary>
    /// <remarks>
    /// A bulk delete of "unused" objects severed a live definition in the field: the objects looked idle
    /// but fed things that stayed, and the damage arrived all at once with nothing to point at. So the
    /// wires that would be cut are counted first and named back to the caller; force says you meant it.
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
                    + "objects that stay. Check the list, then pass force:true if you mean it.\"}";
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

            document.NewSolution(false);
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
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document to place into.");

            EnsureAutosave(document);

            // Where a body goes when the recipe does not say: inside its own group's lane, in rows. Without
            // this every object lands on the origin in one unreadable pile, which is no use to a human
            // watching the build and hoping to intervene before it is finished.
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

            // Every proxy resolved before a single object is added: a recipe either lands whole or leaves
            // the canvas exactly as it was.
            //
            // Every entry resolved before the first refusal is reported, too, which is a different promise
            // and the one that costs a caller real work. Throwing on the first bad name told an author one
            // thing about a recipe that had six things wrong with it, so a thirteen-object block came back
            // six times, each time for one more collision - reported in those words. Atomicity is right and
            // stays: what was wrong was refusing with less than the server already knew. Now one answer
            // carries every entry that could not be resolved, keyed by the recipe's own local id, so the
            // whole batch is fixable in one pass and resent once.
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
                    + "so nothing was placed and the canvas is untouched. Fix all of these and send the recipe "
                    + $"again -- {string.Join(" || ", unresolved)}");
            }

            // First pass: everything stands, configured, and the recipe's local ids learn their real ones.
            Dictionary<string, IGH_DocumentObject> made = [];

            // Resolving the proxies up front makes an unknown *component* atomic, but not an unknown
            // parameter name: that is discovered in the wiring pass, by which time every object has been
            // added. A misspelt input therefore used to leave the whole recipe standing on the canvas,
            // unwired and unnamed - seven orphans, in the case that prompted this - for the caller to find
            // and delete by hand. Rolling back covers the wiring pass and every other way a recipe can fail
            // partway, which pre-validating names alone would not.
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

            // Second pass: the wires, now that both ends exist. A source id is a recipe-local key first
            // and an existing canvas guid second, so a recipe can graft onto what is already there.
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

                    // A constant typed straight into the socket, which is what a caller means by a value
                    // on an input and what this refused - with a dictionary's error message, no less,
                    // because a missing 'sources' was read as a missing key rather than a shape it knows.
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

            // Placed straight into the group that asked for them: in a signature-first build, the body
            // belongs to the function whose signature it fills, and saying so here saves an extra call
            // and the "ungrouped objects" the review would otherwise, rightly, complain about.
            if (host is not null)
            {
                foreach (IGH_DocumentObject thing in made.Values)
                {
                    host.AddObject(thing.InstanceGuid);
                }

                host.ExpireCaches();
            }

            document.NewSolution(false);
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
                // Taken back in reverse, so a source is never removed before the object that cites it. The
                // undo entries recorded on the way in are left behind: they refer to objects that no longer
                // exist, which Grasshopper tolerates, and the alternative - unwinding the undo stack from
                // here - risks eating a step the human put there.
                for (int i = added.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        document.RemoveObject(added[i], update: false);
                    }
                    catch (Exception)
                    {
                        // Best effort: one object that will not come off must not stop the rest coming off,
                        // and the exception being reported is the one worth reporting.
                    }
                }

                if (added.Count > 0)
                {
                    document.NewSolution(false);
                }

                throw;
            }
        });

        Journal.Append(author, "place", $",\"objects\":{Json.Number(objects.GetArrayLength())}");

        return mapping;
    }

    /// <summary>
    /// The proxy a recipe entry names - by guid, or by a name that must mean exactly one thing.
    /// </summary>
    /// <remarks>
    /// Resolved for the whole recipe before anything is added to the document, so a name nobody recognises
    /// fails on an untouched canvas instead of leaving twenty objects standing and the twenty-first missing.
    /// An ambiguous name is refused with the candidates rather than silently picking one: "Merge" is two
    /// different components with different parameter names, and guessing between them is not this server's
    /// business.
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
            // Handed back as something to paste rather than something to transcribe. The candidates were
            // already listed here, as prose - "Merge [Sets › Tree] 3cadddef-..." - and an author reading that
            // still had to take the guid out of the sentence and build the object literal itself. Worse, the
            // category is not always the discriminator: both Merges live in Sets › Tree and differ only by
            // guid, so a reader picking by the label alone cannot tell them apart at all. The literal carries
            // the one key that is guaranteed stable - ComponentGuid is what a .gh file stores to find a
            // component again, so it cannot drift the way a display name, a nickname or a ribbon category can.
            string candidates = string.Join(", ", found.Select(one =>
                $"{{\"name\":\"{one.Desc.Name}\",\"guid\":\"{one.Guid}\"}} in {one.Desc.Category} › "
                + $"{one.Desc.SubCategory}{Hint(one)}"));

            throw new ArgumentException(
                $"'{asked}' names {found.Count} different components - copy the one you meant, guid and all: "
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
    /// The recipe's own local id if it has one, because that is the handle the caller is holding and the one
    /// it will edit; the name it asked for otherwise, and the position as a last resort. Naming the entry is
    /// most of the value of reporting several at once - "6 entries could not be resolved" is only actionable
    /// if the reader can tell which six.
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
    /// Somewhere to put objects that nobody positioned: clear of everything already on the canvas.
    /// </summary>
    /// <remarks>
    /// A definition is built before it is grouped - components first, groups after, wires after that, and
    /// <c>arrange</c> last - so most of what is placed arrives with no group to belong to and no position
    /// asked for. The old answer stepped down by four pixels per object already on the canvas, which for
    /// objects fifty pixels tall means each batch lands almost exactly on the last one. That is the pile
    /// reported from the field, and it is at its worst in the case that matters: a human watching an agent
    /// work, wanting to read the canvas while there is still time to intervene.
    /// <para>
    /// Below everything, not beside it, because a definition grows left to right: below leaves the dataflow
    /// direction free for <c>arrange</c> to use, and a new batch never has to be hunted for - it is at the
    /// bottom. This is a staging area and nothing more; <c>arrange</c> is what decides where things end up,
    /// which is why no caller should be computing coordinates of its own.
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

        // A clear gap, so the eye reads the new batch as a new batch rather than as part of what was there.
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

        thing.CreateAttributes();

        thing.Attributes.Pivot = spec.TryGetProperty("pivot", out JsonElement pivot)
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
            panel.UserText = text.GetString() ?? "";
            return;
        }

        // A scribble takes text too, and used not to: the field was read for a panel and ignored for a
        // scribble, so `place` answered ok and the note on the canvas said "Doubleclick Me!". Reported from the
        // field by an agent who found out only because a human sent it a screenshot - silent success on a
        // dropped field is the worst of the available outcomes, worse than refusing.
        if (thing is Grasshopper.Kernel.Special.GH_Scribble scribble
            && spec.TryGetProperty("text", out JsonElement wording))
        {
            string said = wording.GetString() ?? "";

            // Whitespace is refused rather than written, because an empty scribble is indistinguishable on
            // the canvas from one that was never given its text - which is the confusion being fixed here.
            if (string.IsNullOrWhiteSpace(said))
            {
                throw new ArgumentException(
                    "A note needs something to say - 'text' was empty. An empty scribble looks exactly like " +
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
                    $"{component.Name} has {side.Count} on that side - say which with 'param'.");
        }

        // "0" is an index whether the client sent a number or a string of one - MCP clients do both.
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
    /// Cleared first, because <c>SetPersistentData</c> appends despite its name - and Grasshopper's own
    /// defaults are persistent data too. Setting 0 on a socket that already defaulted to 0 therefore left
    /// two zeroes, and a component fed two values emits two branches: a definition silently doubled its
    /// geometry. Found in the field by an agent, which is what the friction log is for.
    /// </remarks>
    private static void Store(IGH_Param parameter, JsonElement value)
    {
        parameter.GetType()
            .GetMethod("Script_ClearPersistentData", Type.EmptyTypes)
            ?.Invoke(parameter, null);

        if (value.ValueKind == JsonValueKind.Null)
        {
            // A null is how a caller empties a socket - the only way back to "nothing stored here".
            return;
        }

        object raw = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException("set takes a number, text, a flag, or null to empty the socket."),
        };

        // SetPersistentData(params object[]) lives on GH_PersistentParam<T>; reflection reaches it on the
        // concrete type, and refusal by name beats silently doing nothing.
        System.Reflection.MethodInfo? set = parameter.GetType().GetMethod("SetPersistentData", [typeof(object[])]);

        if (set is null)
        {
            throw new ArgumentException($"{parameter.Name} does not store values.");
        }

        set.Invoke(parameter, [new[] { raw }]);
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

            // Both the parameter and the object that owns it, because a data mapping needs two different
            // things to happen and each side of a component needs a different one.
            //
            // Expiring only the parameter was what shipped, and it left an output mapping dead: clearing an
            // output's data does not make its component look out of date, so the next solution finds nothing
            // to do and the output stays empty for good. Measured - after a graft, peek answered count 0
            // twice running, and only a later edit to the component's own input brought the grafted tree
            // through.
            //
            // Expiring only the owner fixes that and breaks the other side. An input keeps the volatile data
            // it already collected, so the mapping is stored and never applied: the component recomputes over
            // the ungrafted tree it is still holding. Measured too - the input answered one branch of four
            // items with graft set on it.
            //
            // So: the parameter, so it collects again and applies the mapping on the way in, and the owner, so
            // it computes again over what arrived. A floating parameter is its own top-level object and the
            // second call is skipped.
            parameter.ExpireSolution(false);

            if ((parameter.Attributes?.GetTopLevel?.DocObject ?? parameter) is IGH_ActiveObject owner
                && !ReferenceEquals(owner, parameter))
            {
                owner.ExpireSolution(false);
            }

            document.NewSolution(false);
            Changed(document);

            return true;
        });

        Journal.Append(author, "param", $",\"id\":{Json.Quote(id.ToString())}");

        return $"{{\"ok\":{(changed ? "true" : "false")}}}";
    }
}
