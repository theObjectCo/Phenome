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
