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

    /// <summary>How long to wait for queued work to *start* before giving up on it and saying so.</summary>
    static readonly TimeSpan ToStart = TimeSpan.FromSeconds(15);

    /// <summary>
    /// And how long to wait for work that has started. Longer, because it is going to finish either way and
    /// the only question is whether anybody is still listening when it does.
    /// </summary>
    static readonly TimeSpan ToFinish = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Runs work on the Rhino UI thread and waits for the answer - the document belongs to that thread,
    /// and the listener lives on none in particular.
    /// </summary>
    /// <remarks>
    /// The waiting is a three-state handshake rather than a timeout, and that is the whole point of it.
    /// <para>
    /// <c>InvokeOnUiThread</c> <em>queues</em> a delegate. Waiting fifteen seconds and then throwing does not
    /// unqueue it: the work still runs, minutes later, while the caller has been told it failed. An agent that
    /// retries a <c>wire</c> or a <c>set</c> on that answer applies it twice. Reported from the field in
    /// exactly those words - "returned Rhino is busy while having in fact been applied" - and two agents on one
    /// canvas make it routine rather than rare, because the thread they share is what runs out of time.
    /// </para>
    /// <para>
    /// So the timeout does not abandon anything it cannot prove has not started. Pending to abandoned is one
    /// atomic move; if the work won the race and is already running, the waiter cannot abandon it and waits for
    /// it instead. The caller therefore learns one of three true things: it ran, or it never started, or it
    /// started and is still going. Never "it failed" about work that happened.
    /// </para>
    /// </remarks>
    internal static T OnUi<T>(Func<T> work)
    {
        const int Pending = 0;
        const int Running = 1;
        const int Abandoned = 2;

        int state = Pending;
        T result = default!;
        Exception? failure = null;

        using SemaphoreSlim done = new(0, 1);

        Rhino.RhinoApp.InvokeOnUiThread(() =>
        {
            // Nobody is listening any more, and nothing has been touched yet: the honest thing is to do
            // nothing at all, because the caller has already been told this did not happen.
            if (Interlocked.CompareExchange(ref state, Running, Pending) != Pending)
            {
                return;
            }

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

        if (!done.Wait(ToStart))
        {
            // Only abandonable while still pending. Losing this race means it is running, and running work
            // finishes - so the wait continues rather than the caller being lied to.
            if (Interlocked.CompareExchange(ref state, Abandoned, Pending) == Pending)
            {
                // Not "it did not answer" - that sentence is true of a long solve and of a modal alike, and
                // those want opposite responses. Pulse can tell them apart without the thread this is waiting
                // for, so the refusal says which one happened.
                throw new TimeoutException(Pulse.Sentence());
            }

            if (!done.Wait(ToFinish))
            {
                // The one case where the caller genuinely cannot be told whether it worked. Say that, rather
                // than something that sounds like a refusal, and point at the record that does know.
                throw new TimeoutException(
                    $"This started and has not finished after {ToFinish.TotalMinutes:0} minutes. It was not " +
                    "cancelled and may still be running - do not send it again. Read /events for an entry " +
                    "under your own author name to see whether it landed, and /pulse for what Rhino is doing.");
            }
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
            ?? throw NoParameter(component.Name, param);
    }

    /// <summary>
    /// No parameter by that name - and, where the name says what was really wanted, which verb has it.
    /// </summary>
    /// <remarks>
    /// Reported from the field: an author with 23,040 preview point markers standing over the building tried
    /// <c>set</c> with <c>param: "preview"</c>, was told a Construct Point has no such parameter, and
    /// concluded from that and from reading <c>param</c> and <c>canvas</c> that nothing could turn a preview
    /// off. The <c>preview</c> verb had done exactly that for several releases. The refusal was accurate and
    /// still left the reader worse informed than the server was, which is the whole fault: a name that says
    /// plainly what somebody was reaching for is an opportunity to point at the thing that does it.
    /// <para>
    /// Kept to words that could only mean drawing. A guess here is cheap to be wrong about - it adds a
    /// sentence to a refusal that is a refusal either way - but a wrong guess sends a reader somewhere else
    /// to be confused, which is worse than saying nothing.
    /// </para>
    /// </remarks>
    internal static Exception NoParameter(string owner, string asked)
    {
        string[] drawing = ["preview", "previews", "hidden", "hide", "visible", "visibility", "show", "drawing"];

        string hint = drawing.Contains(asked.Trim().ToLowerInvariant())
            ? " Drawing is not a parameter: the 'preview' verb turns it off, and takes a group id or an object"
                + " id - or 'ids' for several at once - with on:true to bring it back."
            : "";

        return new KeyNotFoundException($"{owner} has no parameter '{asked}'.{hint}");
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

    /// <summary>
    /// Says the document has changed, so Rhino offers to save it before closing.
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="EnsureAutosave"/>: that one runs before an edit, this one after. Without
    /// it the link had a data-loss path of its own making - measured on 2026-08-19, a slider changed through
    /// <c>/set</c> left <c>IsModified</c> false and no asterisk on the title, so Rhino would close the
    /// document without offering to save and the human lost an agent's work with no prompt at all. An edit is
    /// an edit whoever made it, and the flag is how Rhino is told.
    /// <para>
    /// Called from the verbs rather than from the router, and only where a change is known to have happened.
    /// The router cannot do it: several verbs answer <c>200</c> with <c>ok:false</c> in the body - a
    /// <c>delete</c> that would sever live wires is the common one - so from outside there is no way to tell a
    /// refusal from a change, and marking on arrival would prompt for a save after a verb that did nothing.
    /// </para>
    /// <para>
    /// Deliberately not called by <c>select</c> or <c>zoom</c>, which are ways of looking rather than changes;
    /// by <c>new</c> or <c>open</c>, where there is nothing yet to lose; by <c>save</c>, which clears the flag
    /// by definition; by <c>bake</c>, <c>rhino</c> and <c>camera</c>, which change the Rhino document and not
    /// this one; or by <c>solver</c>, which looks like a document setting and is not - it assigns the static
    /// <c>GH_Document.EnableSolutions</c>, which belongs to the application, is never written into a file, and
    /// is gone at the next restart.
    /// </para>
    /// <para>
    /// Three verbs mark conditionally, because for them doing nothing is a normal outcome rather than a
    /// failure: <c>arrange</c> when something moved, <c>signature</c> when a port was actually planted, and
    /// <c>preview</c> when a flag actually flipped. All three are finishing moves people run more than once,
    /// and a save prompt for having run one twice would teach callers to distrust the prompt.
    /// </para>
    /// </remarks>
    internal static void Changed(GH_Document document) => document.Modified();

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
