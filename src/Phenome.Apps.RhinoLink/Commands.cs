using System.Text;
using System.Text.Json;

namespace Phenome.Apps.RhinoLink;

/// <summary>
/// The two things an agent asks of Rhino when no canvas is involved: run a command, and say what the
/// document holds.
/// </summary>
/// <remarks>
/// Both need the UI thread, which makes them the opposite of everything else in this plugin - pulse and
/// dismiss exist precisely because they do not. That is not a contradiction: a held thread is why pulse
/// answers, and these are what an agent runs once pulse says the thread is free.
/// <para>
/// The canvas link answers the same two verbs, and will go on doing so. This copy is here so that Rhino
/// without Grasshopper is a session an agent can actually work in, rather than one that can only report
/// on itself.
/// </para>
/// </remarks>
internal static class Commands
{
    /// <summary>Runs a Rhino command script and says whether Rhino accepted it.</summary>
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

        bool ran = OnUi(() => Rhino.RhinoApp.RunScript(script, echo: true));

        return $"{{\"ok\":{(ran ? "true" : "false")}}}";
    }

    /// <summary>The document: what it is called, what it holds, and where the human is looking.</summary>
    internal static string Document() => OnUi(() =>
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

    /// <summary>
    /// Runs work on the Rhino UI thread and waits for it, or says why the wait ended instead.
    /// </summary>
    /// <remarks>
    /// A timeout here is never simply "no answer": a long command and an open dialog both look like
    /// silence from the outside and want opposite responses from the caller. Pulse can tell them apart
    /// without the thread this is waiting for, so the refusal borrows its sentence.
    /// </remarks>
    private static T OnUi<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        using SemaphoreSlim done = new(0, 1);

        Rhino.RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception thrown)
            {
                failure = thrown;
            }
            finally
            {
                done.Release();
            }
        });

        if (!done.Wait(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException(Pulse.Sentence());
        }

        return failure is null ? result : throw failure;
    }
}
