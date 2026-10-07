using System.Text;
using System.Text.Json;

namespace Phenome.Apps.RhinoLink;

/// <summary>
/// The verbs that use Rhino without a canvas: run a command, and report what the document holds.
/// </summary>
/// <remarks>
/// Both need the UI thread. Pulse and dismiss avoid it and can answer while it is held. An agent uses these
/// once pulse reports the thread is free.
/// <para>
/// The canvas link answers the same two verbs and keeps doing so. With this copy an agent can work in Rhino
/// without Grasshopper, beyond reporting on the process.
/// </para>
/// </remarks>
internal static class Commands
{
    /// <summary>Runs a Rhino command script and reports whether Rhino accepted it.</summary>
    internal static string Run(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new ArgumentException("command needs 'script'.");
        }

        using JsonDocument request = JsonDocument.Parse(payload);

        string script = request.RootElement.TryGetProperty("script", out JsonElement field)
                && field.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(field.GetString())
            ? field.GetString()!
            : throw new ArgumentException("command needs 'script'.");

        bool ran = Ui.On(() => Rhino.RhinoApp.RunScript(script, echo: true));

        return $"{{\"ok\":{(ran ? "true" : "false")}}}";
    }

    /// <summary>
    /// Deletes every object in the document, or every object on one layer and its sublayers, and reports the
    /// document afterwards.
    /// </summary>
    /// <remarks>
    /// Capture runs mixed models from different files. Baked objects landed on the current layer, the layer an
    /// agent cleared between runs was another one, and the next picture showed both models. Locked and hidden
    /// objects are deleted too, which <c>_SelAll _Delete</c> leaves behind. The deletion is one undo step.
    /// </remarks>
    internal static string Clear(string payload)
    {
        using JsonDocument request = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);

        bool all = request.RootElement.TryGetProperty("all", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
        string? layer = Json.Text(request, "layer");

        if (all == (layer is not null))
        {
            throw new ArgumentException("clear takes all:true for every object, or 'layer' for one layer and its sublayers.");
        }

        (int deleted, List<string> layers) = Ui.On(() =>
        {
            Rhino.RhinoDoc doc = Rhino.RhinoDoc.ActiveDoc
                ?? throw new InvalidOperationException("There is no Rhino document.");

            HashSet<int>? within = null;
            List<string> named = [];

            if (layer is not null)
            {
                int index = doc.Layers.FindByFullPath(layer, -1);

                if (index < 0)
                {
                    Rhino.DocObjects.Layer[] same = [.. doc.Layers.Where(one =>
                        !one.IsDeleted && string.Equals(one.Name, layer, StringComparison.OrdinalIgnoreCase))];

                    index = same.Length == 1
                        ? same[0].Index
                        : throw new KeyNotFoundException(same.Length == 0
                            ? $"There is no layer '{layer}'."
                            : $"'{layer}' names {same.Length} layers; give the full path: "
                                + string.Join(", ", same.Select(one => one.FullPath)) + ".");
                }

                Rhino.DocObjects.Layer top = doc.Layers[index];

                within = [top.Index];
                named.Add(top.FullPath);

                foreach (Rhino.DocObjects.Layer under in top.GetChildren(allChildren: true) ?? [])
                {
                    within.Add(under.Index);
                    named.Add(under.FullPath);
                }
            }

            Rhino.DocObjects.ObjectEnumeratorSettings every = new()
            {
                HiddenObjects = true,
                LockedObjects = true,
                NormalObjects = true,
                IncludeLights = true,
                ReferenceObjects = false,
                DeletedObjects = false,
            };

            Rhino.DocObjects.RhinoObject[] doomed = [.. doc.Objects.GetObjectList(every)
                .Where(thing => within is null || within.Contains(thing.Attributes.LayerIndex))];

            uint record = doc.BeginUndoRecord("Phenome Link: clear");
            int count = 0;

            try
            {
                foreach (Rhino.DocObjects.RhinoObject thing in doomed)
                {
                    if (doc.Objects.Delete(thing, quiet: true, ignoreModes: true))
                    {
                        count++;
                    }
                }
            }
            finally
            {
                doc.EndUndoRecord(record);
                doc.Views.Redraw();
            }

            return (count, named);
        });

        return "{\"ok\":true,\"deleted\":" + Json.Number(deleted)
            + (layer is null ? "" : ",\"layers\":[" + string.Join(",", layers.Select(Json.Quote)) + "]")
            + ",\"document\":" + Document() + "}";
    }

    /// <summary>Reports the document: name, object count, modified flag, layers and the active camera.</summary>
    internal static string Document() => Ui.On(() =>
    {
        Rhino.RhinoDoc doc = Rhino.RhinoDoc.ActiveDoc
            ?? throw new InvalidOperationException("There is no Rhino document.");

        StringBuilder json = new("{\"name\":");

        json.Append(Json.Quote(string.IsNullOrEmpty(doc.Name) ? "unsaved" : doc.Name));
        // ObjectTable.Count includes deleted objects, which the table keeps for undo: after a clear it still
        // reported every object that had been deleted.
        Rhino.DocObjects.ObjectEnumeratorSettings live = new()
        {
            HiddenObjects = true,
            LockedObjects = true,
            NormalObjects = true,
            IncludeLights = true,
            DeletedObjects = false,
        };

        json.Append(",\"objects\":").Append(Json.Number(doc.Objects.GetObjectList(live).Count()));
        json.Append(",\"modified\":").Append(doc.Modified ? "true" : "false");

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

        json.Append("]}");

        return json.ToString();
    });
}
