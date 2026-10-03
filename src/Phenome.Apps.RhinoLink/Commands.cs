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

    /// <summary>Reports the document: name, object count, modified flag, layers and the active camera.</summary>
    internal static string Document() => Ui.On(() =>
    {
        Rhino.RhinoDoc doc = Rhino.RhinoDoc.ActiveDoc
            ?? throw new InvalidOperationException("There is no Rhino document.");

        StringBuilder json = new("{\"name\":");

        json.Append(Json.Quote(string.IsNullOrEmpty(doc.Name) ? "unsaved" : doc.Name));
        json.Append(",\"objects\":").Append(Json.Number(doc.Objects.Count));
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
