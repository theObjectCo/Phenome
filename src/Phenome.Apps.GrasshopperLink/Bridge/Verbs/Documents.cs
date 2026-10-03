using System.Net;
using System.Text;
using System.Text.Json;

using Grasshopper.Kernel;

using Phenome.Apps.GrasshopperLink.Definition;

using static Phenome.Apps.GrasshopperLink.Bridge.Verbs.Plumbing;

namespace Phenome.Apps.GrasshopperLink.Bridge.Verbs;

/// <summary>The document as a whole: opening one, saving one, stepping its history, solving, baking.</summary>
/// <remarks>
/// Unlike <see cref="Objects"/>, these verbs operate on the file, not on objects within it.
/// </remarks>
internal static class Documents
{
    internal static string NewDocument(JsonDocument request)
    {
        string author = Author(request);

        OnUi(() =>
        {
            GH_Document document = new();

            global::Grasshopper.Instances.DocumentServer.AddDocument(document);

            if (global::Grasshopper.Instances.ActiveCanvas is { } canvas)
            {
                canvas.Document = document;
            }

            return true;
        });

        Journal.Append(author, "documentNew");

        return "{\"ok\":true}";
    }

    internal static string Open(JsonDocument request)
    {
        string author = Author(request);
        string path = Field(request, "path") ?? throw new ArgumentException("open needs 'path'.");

        if (!File.Exists(path))
        {
            throw new KeyNotFoundException($"There is no file at {path}.");
        }

        OnUi(() =>
        {
            if (path.EndsWith(".3dm", StringComparison.OrdinalIgnoreCase))
            {
                Rhino.RhinoDoc.Open(path, out bool _);
                return true;
            }

            GH_DocumentIO reader = new();

            if (!reader.Open(path))
            {
                throw new InvalidOperationException($"Grasshopper could not open {path}.");
            }

            GH_Document document = reader.Document;

            global::Grasshopper.Instances.DocumentServer.AddDocument(document);

            if (global::Grasshopper.Instances.ActiveCanvas is { } canvas)
            {
                canvas.Document = document;
            }

            return true;
        });

        Journal.Append(author, "documentOpenAsked", $",\"path\":{Json.Quote(path)}");

        return "{\"ok\":true}";
    }

    /// <summary>
    /// Every document Grasshopper is holding open, and which one the canvas is showing.
    /// </summary>
    /// <remarks>
    /// The link creates documents faster than a person does. <c>new</c> and <c>open</c> both add to
    /// Grasshopper's document server and point the canvas at the new one. The previous document stays open with
    /// its unsaved edits, and no verb reaches it, because every verb targets the document the canvas shows.
    /// After a single <c>new</c> there are two documents, both modified, neither saved, and one of them
    /// unreachable.
    /// <para>
    /// Shaped like <c>sessions</c>, which has the same problem one level up: read to see what is open, pass
    /// 'use' to change which document later verbs target. The id is Grasshopper's own document id, stable for
    /// the document's lifetime; names are not, since most documents are called "unnamed".
    /// </para>
    /// </remarks>
    internal static string Opened() =>
        OnUi(() =>
        {
            GH_Document? shown = ActiveDocument();
            StringBuilder json = new("{\"ok\":true,\"documents\":[");
            bool first = true;

            foreach (GH_Document document in global::Grasshopper.Instances.DocumentServer)
            {
                if (!first)
                {
                    json.Append(',');
                }

                first = false;

                json.Append("{\"id\":").Append(Json.Quote(document.DocumentID.ToString()));
                json.Append(",\"name\":").Append(Json.Quote(document.DisplayName ?? "unsaved"));
                json.Append(",\"path\":").Append(Json.Quote(document.FilePath ?? ""));
                json.Append(",\"modified\":").Append(document.IsModified ? "true" : "false");
                json.Append(",\"objectCount\":").Append(Json.Number(document.ObjectCount));
                json.Append(",\"active\":").Append(ReferenceEquals(document, shown) ? "true" : "false");
                json.Append('}');
            }

            return json.Append("]}").ToString();
        });

    /// <summary>Points the canvas at one of the open documents; every later verb then works on that one.</summary>
    internal static string Use(JsonDocument request)
    {
        string author = Author(request);

        Guid id = Guid.Parse(Field(request, "use")
            ?? throw new ArgumentException(
                "documents needs 'use': the id of the document to show. GET /documents lists them."));

        string name = OnUi(() =>
        {
            GH_Document wanted = Find(id);

            if (global::Grasshopper.Instances.ActiveCanvas is { } canvas)
            {
                canvas.Document = wanted;
            }

            return wanted.DisplayName ?? "unsaved";
        });

        Journal.Append(author, "documentUse", $",\"name\":{Json.Quote(name)}");

        return $"{{\"ok\":true,\"name\":{Json.Quote(name)}}}";
    }

    /// <summary>
    /// Closes a document. Depending on which verb asked, what is unsaved is discarded or written first.
    /// </summary>
    /// <remarks>
    /// Two verbs instead of one with a flag: the destructive option must be named explicitly. A <c>close</c>
    /// with an optional 'save' leaves data loss one omitted field away, and an omitted flag is indistinguishable
    /// from one deliberately left out.
    /// <para>
    /// Grasshopper's <c>SafeRemoveDocument</c> is unsuitable for both: it prompts with a modal dialog, which
    /// blocks the UI thread every verb here runs on until an agent answers the dialog or the user clicks it.
    /// The verb choice already answers the unsaved question, and the direct removal is correct.
    /// </para>
    /// </remarks>
    internal static string Close(JsonDocument request, bool saveFirst)
    {
        string author = Author(request);
        string? which = Field(request, "id");
        string? asked = Field(request, "path");

        (string name, string? wrote, bool lost, string? showing) = OnUi(() =>
        {
            GH_Document document = which is null
                ? ActiveDocument() ?? throw new InvalidOperationException("There is no document to close.")
                : Find(Guid.Parse(which));

            string label = document.DisplayName ?? "unsaved";
            string? saved = null;

            if (saveFirst)
            {
                string target = asked
                    ?? document.FilePath
                    ?? throw new ArgumentException(
                        $"'{label}' has never been saved and there is nowhere to write it: say where with "
                        + "'path'. Use close instead to discard it.");

                WriteDocument(document, target);
                document.FilePath = target;
                document.IsModified = false;
                document.OnModifiedChanged();
                saved = target;
            }

            // Reported although discarding is the verb's purpose: a caller that closed the wrong document is
            // told that something was discarded and does not have to infer it from silence.
            bool discarded = !saveFirst && document.IsModified;

            // Decide the next document before removal: RemoveDocument disposes the document, and a canvas
            // pointing at a disposed one renders from freed memory. Null is a valid value: it is the start
            // screen Grasshopper shows, and the build verbs create a document when they need one.
            GH_Document? next = global::Grasshopper.Instances.DocumentServer
                .FirstOrDefault(other => !ReferenceEquals(other, document));

            if (global::Grasshopper.Instances.ActiveCanvas is { } canvas
                && ReferenceEquals(canvas.Document, document))
            {
                canvas.Document = next;
            }

            global::Grasshopper.Instances.DocumentServer.RemoveDocument(document);

            return (label, saved, discarded, next?.DisplayName);
        });

        Journal.Append(
            author,
            saveFirst ? "documentSavedAndClosed" : "documentClosed",
            $",\"name\":{Json.Quote(name)}" + (wrote is null ? "" : $",\"path\":{Json.Quote(wrote)}"));

        return "{\"ok\":true,\"closed\":" + Json.Quote(name)
            + (wrote is null ? "" : $",\"path\":{Json.Quote(wrote)}")
            + (lost ? ",\"discardedUnsavedChanges\":true" : "")
            + ",\"showing\":" + (showing is null ? "null" : Json.Quote(showing))
            + "}";
    }

    /// <summary>One open document by Grasshopper's own id, or a refusal that says how to find the right one.</summary>
    private static GH_Document Find(Guid id) =>
        global::Grasshopper.Instances.DocumentServer.FirstOrDefault(document => document.DocumentID == id)
        ?? throw new KeyNotFoundException($"No open document has the id {id}. GET /documents lists what is open.");

    internal static string Save(JsonDocument request)
    {
        string author = Author(request);
        string? asked = Field(request, "path");

        string path = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document to save.");

            string target = asked
                ?? document.FilePath
                ?? throw new ArgumentException("The document was never saved; say where with 'path'.");

            WriteDocument(document, target);
            document.FilePath = target;

            // Clear the flag here. This writes the archive directly and does not call Grasshopper's Save,
            // deliberately: saving a copy elsewhere does not repoint the document. Mutating verbs set the flag,
            // and a save that left it set would make Rhino offer to save a document just saved. People learn
            // to dismiss that prompt without reading it.
            document.IsModified = false;

            // The Grasshopper window title stays "unnamed" after saving a new document because
            // GH_DocumentEditor caches its caption and rebuilds it from only five places: its Save and Save As
            // menu handlers, a canvas document swap, opening through script access, and the canvas handler for
            // the modified flag changing. Saving here matches none of the first four, and the fifth does not
            // fire when the flag is not touched. DisplayName is then correct, but the title bar never re-reads
            // it.
            //
            // OnModifiedChanged is public API for this and is called unconditionally. The assignment above
            // raises the notification only when the value changes; saving a document with no edits would
            // otherwise leave the stale title.
            document.OnModifiedChanged();

            return target;
        });

        Journal.Append(author, "save", $",\"path\":{Json.Quote(path)}");

        return $"{{\"ok\":true,\"path\":{Json.Quote(path)}}}";
    }

    /// <summary>One step back or forward on Grasshopper's own undo stack, which every verb records into.</summary>
    internal static string Undo(JsonDocument request, bool forward)
    {
        string author = Author(request);

        // Return the resulting object counts as well as the step name. Undoing a delete restores objects: a
        // caller watching only the count sees it rise and may conclude undo failed when it worked.
        (string what, int before, int after) = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            int had = document.ObjectCount;
            string name = forward ? document.UndoServer.FirstRedoName : document.UndoServer.FirstUndoName;
            int waiting = forward ? document.UndoServer.RedoCount : document.UndoServer.UndoCount;

            if (waiting == 0)
            {
                throw new InvalidOperationException(forward
                    ? "There is nothing to redo."
                    : "There is nothing to undo.");
            }

            if (forward)
            {
                document.UndoServer.PerformRedo();
            }
            else
            {
                document.UndoServer.PerformUndo();
            }

            Solve(document);
            Changed(document);

            global::Grasshopper.Instances.ActiveCanvas?.Refresh();

            return (name, had, document.ObjectCount);
        });

        Journal.Append(author, forward ? "redo" : "undo", $",\"step\":{Json.Quote(what)}");

        return $"{{\"ok\":true,\"step\":{Json.Quote(what)},\"objectsBefore\":{Json.Number(before)},"
            + $"\"objectsAfter\":{Json.Number(after)},\"remaining\":{Json.Number(Steps(forward))}}}";
    }

    /// <summary>How many steps are left on that side of the stack.</summary>
    private static int Steps(bool forward) => OnUi(() =>
    {
        GH_Document? document = ActiveDocument();

        return document is null
            ? 0
            : forward ? document.UndoServer.RedoCount : document.UndoServer.UndoCount;
    });

    internal static string Solver(JsonDocument request)
    {
        string author = Author(request);
        bool enabled = request.RootElement.TryGetProperty("enabled", out JsonElement flag) && AsBool(flag);

        OnUi(() =>
        {
            // Not marked as a document change. GH_Document.EnableSolutions is a static on the type and belongs
            // to the application. It is not written to a .gh file and resets on Rhino restart; closing the
            // document cannot lose it.
            GH_Document.EnableSolutions = enabled;

            if (enabled)
            {
                if (ActiveDocument() is { } shown)
                {
                    Solve(shown);
                }
            }

            return true;
        });

        Journal.Append(author, "solver", $",\"enabled\":{(enabled ? "true" : "false")}");

        return "{\"ok\":true}";
    }

    internal static string Bake(JsonDocument request)
    {
        string author = Author(request);

        if (!request.RootElement.TryGetProperty("ids", out JsonElement ids))
        {
            throw new ArgumentException("bake needs 'ids': which objects to bake.");
        }

        List<Guid> asked = [.. ids.EnumerateArray().Select(id => Guid.Parse(id.GetString()!))];

        // Report each skipped object. A bare "ok" after baking nothing cannot tell apart an id that is not on
        // the canvas, an object that is not bake-aware and empty geometry: three failures with three fixes
        // reported as one success.
        (int Baked, List<string> Skipped) result = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document to bake from.");

            Rhino.RhinoDoc rhino = Rhino.RhinoDoc.ActiveDoc
                ?? throw new InvalidOperationException("There is no Rhino document to bake into.");

            List<Guid> born = [];
            List<string> skipped = [];

            foreach (Guid id in asked)
            {
                IGH_DocumentObject? thing = document.FindObject(id, topLevelOnly: true);

                if (thing is null)
                {
                    skipped.Add($"{id} is not on the canvas");
                    continue;
                }

                if (thing is not IGH_BakeAwareObject bakeable)
                {
                    skipped.Add($"{thing.NickName} ({id}) holds nothing that can be baked");
                    continue;
                }

                if (!bakeable.IsBakeCapable)
                {
                    // The usual reason is an empty or unsolved output, not a wrong kind of object. The
                    // message says where to look as well as what was refused.
                    skipped.Add(
                        $"{thing.NickName} ({id}) has nothing to bake right now: it is empty, hidden or unsolved");
                    continue;
                }

                int before = born.Count;
                bakeable.BakeGeometry(rhino, born);

                if (born.Count == before)
                {
                    skipped.Add($"{thing.NickName} ({id}) produced no objects");
                }
            }

            rhino.Views.Redraw();

            return (born.Count, skipped);
        });

        Journal.Append(
            author,
            "bake",
            $",\"objects\":{Json.Number(asked.Count)},\"baked\":{Json.Number(result.Baked)}");

        string skippedJson = string.Join(",", result.Skipped.Select(Json.Quote));

        return $"{{\"ok\":true,\"baked\":{Json.Number(result.Baked)},\"skipped\":[{skippedJson}]}}";
    }

    internal static string RunScript(JsonDocument request)
    {
        string author = Author(request);
        string script = Field(request, "script") ?? throw new ArgumentException("rhino needs 'script'.");

        bool ran = OnUi(() => Rhino.RhinoApp.RunScript(script, echo: true));

        Journal.Append(author, "rhino", $",\"script\":{Json.Quote(script)},\"ok\":{(ran ? "true" : "false")}");

        if (ran)
        {
            return "{\"ok\":true}";
        }

        // Rhino returns only false. The answer lists the usual causes and where to find the real reason; a
        // bare flag gives the caller no next step and makes a wrong command name and a cancelled command look
        // identical.
        return "{\"ok\":false,\"error\":" + Json.Quote(
            "Rhino did not run the script to completion. Common causes: a command name it does not know, "
                + "an option spelled differently in the scripting dialect, a command that needs a pick and "
                + "was cancelled, or one still waiting for input. Read /console for what Rhino said, and "
                + "/pulse for whether it is still waiting; if it is, /escape cancels it.")
            + ",\"script\":" + Json.Quote(script) + "}";
    }

    internal static string WriteScript(JsonDocument request)
    {
        string author = Author(request);
        Guid id = Guid.Parse(Field(request, "id") ?? throw new ArgumentException("script needs 'id'."));
        string source = Field(request, "source") ?? throw new ArgumentException("script needs 'source'.");

        string answer = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            EnsureAutosave(document);

            string written = Scripts.Write(document, id, source);

            Changed(document);

            return written;
        });

        Journal.Append(author, "script", $",\"id\":{Json.Quote(id.ToString())}");

        return answer;
    }
}
