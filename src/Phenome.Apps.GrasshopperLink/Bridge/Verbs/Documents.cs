using System.Net;
using System.Text;
using System.Text.Json;

using Grasshopper.Kernel;

using Phenome.Apps.GrasshopperLink.Definition;

using static Phenome.Apps.GrasshopperLink.Bridge.Verbs.Plumbing;

namespace Phenome.Apps.GrasshopperLink.Bridge.Verbs;

/// <summary>The document as a whole: opening one, saving one, stepping its history, solving, baking.</summary>
/// <remarks>
/// Distinguished from <see cref="Objects"/> by what a mistake costs - these verbs act on somebody's file
/// rather than on something inside it.
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
    /// This exists because the link makes documents faster than a human does and, until the verbs below,
    /// never closed one. <c>new</c> and <c>open</c> both add to Grasshopper's document server and point the
    /// canvas at the newcomer; whatever was there stays open, keeps its unsaved edits, and becomes
    /// unreachable - every verb speaks to the one the canvas shows. Measured after a single <c>new</c>: two
    /// documents, both modified, both never saved, one of them addressable by nothing at all. The journal
    /// had been saying so all along - two <c>documentOpened</c> entries and no <c>documentClosed</c>.
    /// <para>
    /// Shaped like <c>sessions</c>, which has the same problem one level up: read it to see what there is,
    /// pass 'use' to change which one the later verbs mean. The id is Grasshopper's own document id, which
    /// is stable for the life of the document, unlike a name - "unnamed" is what most of them are called.
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

    /// <summary>Points the canvas at one of the open documents, so every later verb means that one.</summary>
    internal static string Use(JsonDocument request)
    {
        string author = Author(request);

        Guid id = Guid.Parse(Field(request, "use")
            ?? throw new ArgumentException(
                "documents needs 'use' - the id of the document to show. GET /documents lists them."));

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
    /// Closes a document - discarding what is unsaved, or writing it first, depending on which verb asked.
    /// </summary>
    /// <remarks>
    /// Two verbs rather than one with a flag, for the reason <c>dismiss</c> defaults to declining: the
    /// destructive reading has to be the one somebody named. A <c>close</c> with an optional 'save' would
    /// put losing an afternoon's work one forgotten field away, and a flag left out looks exactly like a
    /// flag considered.
    /// <para>
    /// Grasshopper's own <c>SafeRemoveDocument</c> is the wrong tool for either: it answers the unsaved
    /// question with a modal prompt, and a modal dialog holds the UI thread that every verb in this server
    /// runs on - the link would then answer nothing at all until somebody walked over to the machine, which
    /// is the exact failure <c>pulse</c> and <c>dismiss</c> exist to dig out of. The question it would ask
    /// has already been answered by the choice of verb, so the blunt removal is the right one here.
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
                        $"'{label}' has never been saved, so there is nowhere to write it - say where with "
                        + "'path'. Use close instead if you meant to discard it.");

                WriteDocument(document, target);
                document.FilePath = target;
                document.IsModified = false;
                document.OnModifiedChanged();
                saved = target;
            }

            // Reported, not hidden: discarding is what this verb is for, but a caller that closed the wrong
            // document deserves to read that something was thrown away rather than infer it from silence.
            bool discarded = !saveFirst && document.IsModified;

            // Where the canvas looks next, decided before the removal: RemoveDocument disposes the document,
            // and a canvas still pointing at a disposed one paints from freed state. Null is a real answer -
            // it is the start screen Grasshopper itself opens on, and the build verbs make a document when
            // they need one.
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
                ?? throw new ArgumentException("The document was never saved - say where with 'path'.");

            WriteDocument(document, target);
            document.FilePath = target;

            // The flag has to be cleared here, because this does not go through Grasshopper's own Save - it
            // writes the archive itself, deliberately, so that saving a copy somewhere does not silently
            // repoint the document. Nothing noticed while the flag was never set in the first place; now that
            // the mutating verbs set it, a save that left it standing would mean Rhino still offers to save a
            // document you just saved, which is how people learn to dismiss that prompt without reading it.
            document.IsModified = false;

            // And this is why the Grasshopper window kept saying "unnamed" after saving a new document.
            // GH_DocumentEditor caches its caption and rebuilds it from five places only: its own Save and
            // Save As menu handlers, a canvas document swap, opening through script access, and the canvas's
            // handler for the modified flag changing. Saving through here is none of the first four, and the
            // fifth never fired because nothing here used to touch the flag - so DisplayName was correct all
            // along and the title bar simply never asked it again.
            //
            // Said unconditionally rather than leaning on the assignment above, which only raises the
            // notification when the value actually changes: saving a document that had no edits would
            // otherwise leave the stale title exactly as it was. OnModifiedChanged is public API for this.
            document.OnModifiedChanged();

            return target;
        });

        Journal.Append(author, "save", $",\"path\":{Json.Quote(path)}");

        return $"{{\"ok\":true,\"path\":{Json.Quote(path)}}}";
    }

    /// <summary>One step back, or forward - Grasshopper's own undo stack, which every verb records into.</summary>
    internal static string Undo(JsonDocument request, bool forward)
    {
        string author = Author(request);

        // Answered with what the document looks like afterwards, because a step's name alone reads as
        // nonsense: undoing a delete puts objects back, so a caller watching only the count sees it grow
        // and concludes undo is broken. It was not; it was working.
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

            document.NewSolution(false);
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
            // Not marked as a document change, and worth saying why, because it looks like one: this is
            // GH_Document.EnableSolutions, a static on the type rather than a property of any document. It
            // belongs to the application, is not written into a .gh file, and is gone when Rhino restarts -
            // so there is nothing here that closing the document could lose.
            GH_Document.EnableSolutions = enabled;

            if (enabled)
            {
                ActiveDocument()?.NewSolution(false);
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
            throw new ArgumentException("bake needs 'ids' - which objects to bake.");
        }

        List<Guid> asked = [.. ids.EnumerateArray().Select(id => Guid.Parse(id.GetString()!))];

        // A silent no-op was the worst answer this could give. Baking nothing and saying "ok" left no way to
        // tell an id that is not on the canvas from an object that cannot be baked from geometry that was
        // simply empty -- three different mistakes with three different fixes, reported as one success.
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
                    // The usual reason is an empty or unsolved output rather than a wrong kind of object,
                    // so the message says where to look instead of only what was refused.
                    skipped.Add(
                        $"{thing.NickName} ({id}) has nothing to bake right now - it is empty, hidden or unsolved");
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

        // Rhino hands back a bare false, so there is nothing to pass on but the reasons it is usually false
        // and where the actual answer will be. Returning the bare flag left the caller with no next move,
        // which is how a wrong command name and a cancelled command came to look identical.
        return "{\"ok\":false,\"error\":" + Json.Quote(
            "Rhino did not run the script to completion. Common causes: a command name it does not know, "
                + "an option spelled differently in the scripting dialect, a command that needs a pick and "
                + "was cancelled, or one still waiting for input. Read /console for what Rhino said, and "
                + "/pulse for whether it is still waiting - if it is, /escape cancels it.")
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
