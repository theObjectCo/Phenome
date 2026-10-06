using System.Net;
using System.Text;
using System.Text.Json;

using Grasshopper.Kernel;

using Phenome.Apps.GrasshopperLink.Definition;

namespace Phenome.Apps.GrasshopperLink.Bridge.Verbs;

/// <summary>Shared helpers that no single verb is about.</summary>
/// <remarks>
/// They read a request, coerce a field that may be absent or the wrong kind, get onto the thread that owns the
/// document, and ensure an autosave exists before any change.
/// <para>
/// Imported with <c>using static</c> by every verb class. The sharing is declared once at the top of each file,
/// and call sites need no prefix.
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
    /// A flag in any client spelling. MCP clients often serialise scalars as strings, and the server accepts
    /// strings as well as JSON booleans.
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

    /// <summary>
    /// A colour in the spellings Grasshopper accepts: [r,g,b] or [r,g,b,a], "255,60,60", "255,60,60,128",
    /// "#ff3c3c", "#80ff3c3c" or a name.
    /// </summary>
    /// <remarks>
    /// Text goes through Grasshopper's own parser and means what it would mean typed into a Panel wired to a
    /// colour input. That parser reads four numbers as r,g,b,a with the alpha <em>last</em>; an array of four is
    /// read in the same order. Any other count is refused: dropping the fourth number silently would make
    /// "120,215,205,190" an opaque colour with no warning.
    /// </remarks>
    internal static System.Drawing.Color AsColour(JsonElement value)
    {
        const string Spelling = "a colour is [r,g,b] or [r,g,b,a], or text: \"r,g,b\", \"r,g,b,a\" with the "
            + "alpha last, \"#rrggbb\", \"#aarrggbb\" or a colour name.";

        if (value.ValueKind == JsonValueKind.Array)
        {
            int[] channels = [.. value.EnumerateArray().Select(channel => (int)AsDouble(channel))];

            return channels.Length switch
            {
                3 => System.Drawing.Color.FromArgb(channels[0], channels[1], channels[2]),
                4 => System.Drawing.Color.FromArgb(channels[3], channels[0], channels[1], channels[2]),
                _ => throw new ArgumentException($"{channels.Length} numbers are not a colour: {Spelling}"),
            };
        }

        // The parser falls back to Color.FromName, which answers any word with a "named" transparent black, and
        // "not a colour" would parse. Only a name the system actually knows passes.
        if (value.ValueKind == JsonValueKind.String
            && GH_Convert.ToColor(value.GetString()!.Trim(), out System.Drawing.Color colour, GH_Conversion.Secondary)
            && (!colour.IsNamedColor || colour.IsKnownColor))
        {
            return colour;
        }

        throw new ArgumentException($"'{value}' is not a colour: {Spelling}");
    }

    /// <summary>A number in any client spelling, as with <see cref="AsBool"/>.</summary>
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

    /// <summary>How long to wait for queued work to start before giving up and reporting it.</summary>
    static readonly TimeSpan ToStart = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long to wait for work that has started. It is longer: the work will finish regardless, and the only
    /// question is whether the caller is still listening.
    /// </summary>
    static readonly TimeSpan ToFinish = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Runs work on the Rhino UI thread and waits for the result. The document belongs to that thread; the
    /// listener runs on no particular thread.
    /// </summary>
    /// <remarks>
    /// The wait is a three-state handshake, not a plain timeout.
    /// <para>
    /// <c>InvokeOnUiThread</c> <em>queues</em> a delegate. Timing out and throwing does not unqueue it: the work
    /// still runs later, after the caller has been told it failed, and retrying a <c>wire</c> or <c>set</c> then
    /// applies it twice. A caller waiting on a busy response must not be told "failed" for work that in fact ran;
    /// with two agents sharing one canvas this is common, because the shared thread is what runs out of time.
    /// </para>
    /// <para>
    /// The timeout therefore never abandons work it cannot prove has not started. The pending-to-abandoned
    /// transition is one atomic move; if the work already started, the waiter cannot abandon it and waits
    /// instead. The caller learns one of three true facts (it ran, it never started, or it started and is still
    /// going) and is never told "failed" about work that happened.
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
            // The caller has already been told this did not happen and that nothing was touched; it must not run.
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
            // Abandon only while still pending. Losing this race means the work is running and will finish; the
            // wait continues and returns no false failure.
            if (Interlocked.CompareExchange(ref state, Abandoned, Pending) == Pending)
            {
                // "Did not answer" fits both a long solve and a modal, which need opposite responses. Pulse
                // tells them apart without the blocked thread, and the message names which one occurred.
                throw new TimeoutException(Pulse.Sentence());
            }

            if (!done.Wait(ToFinish))
            {
                // The one case where the caller cannot be told whether it worked. State that plainly and point
                // at the record that does know.
                throw new TimeoutException(
                    $"This started and has not finished after {ToFinish.TotalMinutes:0} minutes. It was not " +
                    "cancelled and may still be running: do not send it again. Read /events for an entry " +
                    "under the same author name to see whether it landed, and /pulse for what Rhino is doing.");
            }
        }

        return failure is null ? result : throw failure;
    }

    // Canvas first, then the document server: headless Rhino has documents but no active canvas.
    internal static GH_Document? ActiveDocument() =>
        global::Grasshopper.Instances.ActiveCanvas?.Document
        ?? global::Grasshopper.Instances.DocumentServer.FirstOrDefault();

    /// <summary>
    /// The document to put new objects into, made if Grasshopper has not made one.
    /// </summary>
    /// <remarks>
    /// A freshly opened Grasshopper has no document: it shows a start screen and creates a document only when a
    /// component is dropped. A canvas the user calls empty is one the link correctly reports as absent. Without
    /// this method the first verb of a build fails with "There is no document" right after <c>launch</c>.
    /// <para>
    /// This creates a document, as Grasshopper does when the first component is dropped. Only verbs that add new
    /// objects call it. Reads still report that nothing is there. Verbs that edit an existing object still
    /// refuse, and correctly: a newly created empty document cannot hold the object named.
    /// </para>
    /// </remarks>
    internal static GH_Document EnsureDocument()
    {
        if (ActiveDocument() is { } already)
        {
            return already;
        }

        GH_Document made = new();
        global::Grasshopper.Instances.DocumentServer.AddDocument(made);

        if (global::Grasshopper.Instances.ActiveCanvas is { } canvas)
        {
            canvas.Document = made;
        }

        return made;
    }

    /// <summary>Run a new solution, re-enabling a document that Grasshopper may have disabled.</summary>
    /// <remarks>
    /// Hiding the editor (<c>-_Grasshopper _Window _Hide</c>) disables the document it was showing. Without this
    /// method, edits to it are accepted and nothing computes: <c>place</c> returns ok, the wires exist, and
    /// <c>peek</c> finds no data. Showing the editor re-enables the document but does not solve what was placed meanwhile. An edit
    /// must be solved, and the document is re-enabled first.
    /// </remarks>
    internal static void Solve(GH_Document document)
    {
        if (!document.Enabled)
        {
            document.Enabled = true;
        }

        document.NewSolution(false);
    }

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

    /// <summary>The same aim without a request: side is input by default; no name means the only one.</summary>
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
                : throw new ArgumentException($"{component.Name} has {side.Count} on that side; say which with 'param'.");
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
    /// The error for an unknown parameter name, plus the verb that handles it when the name implies drawing.
    /// </summary>
    /// <remarks>
    /// A caller that sets <c>param: "preview"</c> on a Construct Point is told the object has no such parameter,
    /// and may conclude that no verb controls previews. The <c>preview</c> verb does, and the error names it.
    /// <para>
    /// The keyword list is restricted to terms that unambiguously refer to drawing. An incorrect redirect is worse
    /// than no redirect because it sends the caller to an unrelated verb.
    /// </para>
    /// </remarks>
    internal static Exception NoParameter(string owner, string asked)
    {
        string[] drawing = ["preview", "previews", "hidden", "hide", "visible", "visibility", "show", "drawing"];

        string hint = drawing.Contains(asked.Trim().ToLowerInvariant())
            ? " Drawing is not a parameter: the 'preview' verb turns it off, and takes a group id, an object"
                + " id or 'ids' for several at once, with on:true to bring it back."
            : "";

        return new KeyNotFoundException($"{owner} has no parameter '{asked}'.{hint}");
    }

    private static readonly HashSet<Guid> Autosaved = [];

    /// <summary>A copy written to %TEMP% before the first edit of a document, backing the undo stack.</summary>
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
    /// Marks the document as changed so Rhino offers to save before closing.
    /// </summary>
    /// <remarks>
    /// The counterpart of <see cref="EnsureAutosave"/>: that runs before an edit, this after. Without it a
    /// slider change through <c>/set</c> leaves <c>IsModified</c> false with no title asterisk, and Rhino closes
    /// without offering to save. An edit must set the flag however it was made.
    /// <para>
    /// Called from verbs, not the router, and only where a change actually happened. The router cannot tell them
    /// apart: several verbs return <c>200</c> with <c>ok:false</c> (a <c>delete</c> that would sever live wires is
    /// the common case), and marking on arrival would prompt for a save after a verb that did nothing.
    /// </para>
    /// <para>
    /// Not called by <c>select</c> or <c>zoom</c> (they only view), <c>new</c> or <c>open</c> (nothing to lose
    /// yet), <c>save</c> (clears the flag), <c>bake</c>, <c>rhino</c> or <c>camera</c> (they change the Rhino
    /// document), or <c>solver</c>. That one looks like a document setting but assigns the static
    /// <c>GH_Document.EnableSolutions</c>, an application-level value never written to a file.
    /// </para>
    /// <para>
    /// Three verbs mark conditionally because doing nothing is a normal outcome for them: <c>arrange</c> when
    /// something moved, <c>signature</c> when a port was planted, <c>preview</c> when a flag flipped. These are
    /// finishing moves run more than once, and a save prompt for running one twice would erode trust in the prompt.
    /// </para>
    /// </remarks>
    internal static void Changed(GH_Document document) => document.Modified();

    /// <summary>Serialised via the archive, which unlike a Save never touches the document's own path.</summary>
    /// <remarks>
    /// The archive is turned into bytes here and the file is written here, not by the archive's WriteToFile.
    /// WriteToFile reports a failure in a modal "File Saving Error" box, and a modal holds Rhino's UI thread
    /// until someone clicks it: the save verb timed out, every verb after it with it, and an agent in Claude
    /// Desktop has no way to click. A missing folder was the usual cause, so the folders are created. Anything
    /// else that stops the write comes back as a refusal naming the cause.
    /// <para>
    /// The file is written beside its target first and then moved over it, so a write that fails halfway leaves
    /// the previous file as it was.
    /// </para>
    /// </remarks>
    internal static void WriteDocument(GH_Document document, string path)
    {
        GH_IO.Serialization.GH_Archive archive = new();

        if (!archive.AppendObject(document, "Definition"))
        {
            throw new InvalidOperationException("The document would not serialise.");
        }

        string target = Path.GetFullPath(path);

        // .ghx is the XML form of the same archive; anything else gets the binary form Grasshopper uses for .gh.
        byte[] bytes = string.Equals(Path.GetExtension(target), ".ghx", StringComparison.OrdinalIgnoreCase)
            ? System.Text.Encoding.UTF8.GetBytes(archive.Serialize_Xml())
            : archive.Serialize_Binary();

        string temporary = target + ".writing";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception)
            {
                // Nothing was written, or what was cannot be removed either; the refusal below says why.
            }

            throw new InvalidOperationException($"Could not write {target}: {failed.Message}");
        }
    }
}
