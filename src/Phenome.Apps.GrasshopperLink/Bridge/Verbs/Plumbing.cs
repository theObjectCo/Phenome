using System.Net;
using System.Text;
using System.Text.Json;

using Grasshopper.Kernel;

using Phenome.Apps.GrasshopperLink.Definition;

namespace Phenome.Apps.GrasshopperLink.Bridge.Verbs;

/// <summary>
/// What every verb needs and no verb is about.
/// </summary>
/// <remarks>
/// Reading a request, coercing a field that may be absent or the wrong kind, getting onto the one
/// thread that owns the document, and making sure an autosave exists before anything is changed.
/// <para>
/// Imported with <c>using static</c> by every verb class, which is the point: these were private
/// members of one enormous partial class, and the whole file could reach them because it was all one
/// class. Splitting the verbs apart would have turned every one of those calls into a prefix, so
/// instead the sharing is stated once at the top of each file and the call sites are untouched.
/// </para>
/// </remarks>
internal static class Plumbing
{
    internal static JsonDocument Read(string payload) => JsonDocument.Parse(
        string.IsNullOrWhiteSpace(payload) ? "{}" : payload);

    internal static string ReadBody(HttpListenerRequest request)
    {
        using StreamReader reader = new(request.InputStream, Encoding.UTF8);

        return reader.ReadToEnd();
    }

    internal static string Author(JsonDocument request) => Field(request, "author") ?? "unnamed";

    internal static string? Field(JsonDocument request, string name) =>
        request.RootElement.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;

    /// <summary>A text field of one object, whether it is a whole request or one entry of a batch.</summary>
    internal static string? Text(JsonElement request, string name) =>
        request.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;

    /// <summary>
    /// A flag, however the client spelt it. MCP clients routinely serialise scalars as strings, so a
    /// server that insists on JSON booleans punishes the wrong party.
    /// </summary>
    internal static bool AsBool(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.GetDouble() != 0,
        JsonValueKind.String => value.GetString()!.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => throw new ArgumentException($"'{value.GetString()}' is not a flag."),
        },
        _ => throw new ArgumentException("Expected a flag."),
    };

    /// <summary>A colour, however the client spelt it: [r,g,b], "255,60,60", or "#ff3c3c".</summary>
    internal static System.Drawing.Color AsColour(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= 3)
        {
            return System.Drawing.Color.FromArgb(
                (int)AsDouble(value[0]),
                (int)AsDouble(value[1]),
                (int)AsDouble(value[2]));
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("a colour is [r,g,b], \"r,g,b\" or \"#rrggbb\".");
        }

        string said = value.GetString()!.Trim();

        if (said.StartsWith('#'))
        {
            return System.Drawing.ColorTranslator.FromHtml(said);
        }

        string[] parts = said.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 3
            ? System.Drawing.Color.FromArgb(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]))
            : System.Drawing.ColorTranslator.FromHtml(said);
    }

    /// <summary>A number, however the client spelt it - same story as <see cref="AsBool"/>.</summary>
    internal static double AsDouble(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String when double.TryParse(
            value.GetString(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double parsed) => parsed,
        _ => throw new ArgumentException("Expected a number."),
    };

    internal static long Since(HttpListenerRequest request) =>
        long.TryParse(request.QueryString["since"], out long since) ? since : 0;

    /// <summary>
    /// Runs work on the Rhino UI thread and waits for the answer - the document belongs to that thread,
    /// and the listener lives on none in particular.
    /// </summary>
    internal static T OnUi<T>(Func<T> work)
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
            // Not "it did not answer" - that sentence is true of a long solve and of a modal alike, and
            // those want opposite responses. Pulse can tell them apart without the thread this is waiting
            // for, so the refusal says which one happened.
            throw new TimeoutException(Pulse.Sentence());
        }

        return failure is null ? result : throw failure;
    }

    // The canvas first, the server second: headless Rhino has documents but no canvas to call active.
    internal static GH_Document? ActiveDocument() =>
        global::Grasshopper.Instances.ActiveCanvas?.Document
        ?? global::Grasshopper.Instances.DocumentServer.FirstOrDefault();

    internal static string Named(IGH_DocumentObject thing) =>
        string.IsNullOrWhiteSpace(thing.NickName) ? thing.Name : thing.NickName;

    internal static IEnumerable<IGH_Param> OutputsOf(IGH_DocumentObject thing) => thing switch
    {
        IGH_Component component => component.Params.Output,
        IGH_Param parameter => [parameter],
        _ => [],
    };

    /// <summary>The parameter a request points at: the object itself, or one of a component's by side and name/index.</summary>
    internal static IGH_Param Locate(IGH_DocumentObject thing, JsonDocument request) =>
        LocateBy(thing, Field(request, "side"), Field(request, "param"));

    /// <summary>The same aim without a request: side is input unless said, no name means the only one.</summary>
    internal static IGH_Param LocateBy(IGH_DocumentObject thing, string? whichSide, string? param)
    {
        if (thing is IGH_Param loose)
        {
            return loose;
        }

        if (thing is not IGH_Component component)
        {
            throw new ArgumentException($"{thing.Name} has no parameters.");
        }

        List<IGH_Param> side = whichSide == "output"
            ? component.Params.Output
            : component.Params.Input;

        if (param is null)
        {
            return side.Count == 1
                ? side[0]
                : throw new ArgumentException($"{component.Name} has {side.Count} on that side - say which with 'param'.");
        }

        if (int.TryParse(param, out int index))
        {
            if (index < 0 || index >= side.Count)
            {
                throw new ArgumentException($"{component.Name} has {side.Count} on that side; {index} is not one of them.");
            }

            return side[index];
        }

        return side.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, param, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.NickName, param, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"{component.Name} has no parameter '{param}'.");
    }

    private static readonly HashSet<Guid> Autosaved = [];

    /// <summary>
    /// A copy into %TEMP% before an agent's first edit of a document - the net under the undo stack.
    /// </summary>
    internal static void EnsureAutosave(GH_Document document)
    {
        if (!Autosaved.Add(document.DocumentID))
        {
            return;
        }

        try
        {
            WriteDocument(document, Path.Combine(
                Path.GetTempPath(),
                $"phenome-autosave-{document.DocumentID:N}.gh"));
        }
        catch (Exception failure)
        {
            LinkLog.Say($"Phenome Link: autosave failed ({failure.Message}); carrying on.");
        }
    }

    /// <summary>Serialised via the archive, which unlike a Save never touches the document's own path.</summary>
    internal static void WriteDocument(GH_Document document, string path)
    {
        GH_IO.Serialization.GH_Archive archive = new();

        if (!archive.AppendObject(document, "Definition"))
        {
            throw new InvalidOperationException("The document would not serialise.");
        }

        if (!archive.WriteToFile(path, true, false))
        {
            throw new InvalidOperationException($"Could not write {path}.");
        }
    }
}
