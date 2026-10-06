using System.Text;
using System.Text.Json;

namespace Phenome.Apps.RhinoLink;

/// <summary>
/// Runs Python in Rhino and answers with what it printed, what it raised and what it added, in the same call.
/// </summary>
/// <remarks>
/// Before this verb an agent ran Python through <c>command</c> as <c>-_RunPythonScript (...)</c>. That call
/// answered <c>ok:true</c> whatever the script did, the printed output arrived only through <c>console</c> once
/// the UI thread was free, and an uncaught exception in the inline form opened Rhino's modal "Exception
/// Occured" box. The box held the UI thread, and its message could be read only through UI Automation.
/// <para>
/// The code here runs inside a small runner written next to it, which Rhino starts with
/// <c>_-RunPythonScript</c> as a file. The runner catches everything the code raises, so nothing reaches Rhino's
/// exception handler and no box opens. It captures standard output and error, and writes them with the error,
/// the traceback and the value of a top-level <c>result</c> into a JSON file, which is read back here once the
/// command returns. The file form also runs the CPython 3 that Rhino 8 runs for its own scripts.
/// </para>
/// <para>
/// Parameters arrive as <c>globals</c>, a JSON object whose keys become variables before the code runs. Callers
/// no longer build <c>exec(open(path).read())</c> lines with values pasted into a string.
/// </para>
/// <para>
/// With <c>layer</c> named, every object the code added ends on that layer, made if missing. The objects are
/// found by comparing the document before and after, which leaves out objects the code only changed.
/// </para>
/// </remarks>
internal static class Python
{
    /// <summary>How long a script may run when the caller does not say.</summary>
    private const int DefaultSeconds = 120;

    /// <summary>
    /// The longest wait allowed. Node's fetch gives up on an answer after 300 seconds, and the MCP server
    /// would report a dropped connection instead of this verb's own timeout.
    /// </summary>
    private const int MostSeconds = 280;

    /// <summary>How many ids of added objects the answer lists before it gives only the count.</summary>
    private const int ListedIds = 1000;

    internal static string Run(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new ArgumentException("python needs 'code' or 'path'.");
        }

        using JsonDocument request = JsonDocument.Parse(payload);

        string? code = Json.Text(request, "code");
        string? path = Json.Text(request, "path");
        string? layer = Json.Text(request, "layer");
        int seconds = Math.Clamp(Json.Int(request, "timeout", DefaultSeconds), 1, MostSeconds);

        if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("python needs 'code' (the source) or 'path' (a .py file).");
        }

        if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("python takes 'code' or 'path', not both.");
        }

        string globals = "{}";

        if (request.RootElement.TryGetProperty("globals", out JsonElement given))
        {
            globals = given.ValueKind == JsonValueKind.Object
                ? given.GetRawText()
                : throw new ArgumentException("'globals' has to be a JSON object; its keys become variables.");
        }

        if (path is not null)
        {
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));

            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"There is no file at {path}.");
            }
        }

        string folder = Path.Combine(Path.GetTempPath(), $"phenome-python-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        string source = path ?? Path.Combine(folder, "code.py");
        string values = Path.Combine(folder, "globals.json");
        string answer = Path.Combine(folder, "answer.json");
        string runner = Path.Combine(folder, "runner.py");

        if (code is not null)
        {
            File.WriteAllText(source, code, new UTF8Encoding(false));
        }

        File.WriteAllText(values, globals, new UTF8Encoding(false));
        File.WriteAllText(runner, Runner(source, path ?? "<python>", values, answer, path is null ? "" : Path.GetDirectoryName(path)!), new UTF8Encoding(false));

        bool finished = false;

        try
        {
            string reply = Ui.On(
                () =>
                {
                    Rhino.RhinoDoc doc = Rhino.RhinoDoc.ActiveDoc
                        ?? throw new InvalidOperationException("There is no Rhino document.");

                    HashSet<Guid> before = Present(doc);

                    bool ran = Rhino.RhinoApp.RunScript($"_-RunPythonScript \"{runner}\"", echo: false);

                    List<Guid> added = [.. Present(doc).Where(id => !before.Contains(id))];

                    if (layer is not null && added.Count > 0)
                    {
                        Move(doc, added, Layers.Ensure(doc, layer));
                    }

                    doc.Views.Redraw();

                    return Answer(ran, answer, added, layer);
                },
                TimeSpan.FromSeconds(seconds));

            finished = true;

            return reply;
        }
        catch (TimeoutException waited)
        {
            throw new TimeoutException(
                $"The script did not finish within {seconds} s and is still running; its output will not come "
                + $"back here. {waited.Message}");
        }
        finally
        {
            // A script that is still running reads its files and writes its answer later; they stay.
            if (finished)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception)
                {
                    // A temporary folder left behind costs nothing.
                }
            }
        }
    }

    /// <summary>The ids of every object in the document, hidden and locked ones included.</summary>
    private static HashSet<Guid> Present(Rhino.RhinoDoc doc)
    {
        Rhino.DocObjects.ObjectEnumeratorSettings every = new()
        {
            HiddenObjects = true,
            LockedObjects = true,
            NormalObjects = true,
            IncludeLights = true,
            ReferenceObjects = true,
            DeletedObjects = false,
        };

        return [.. doc.Objects.GetObjectList(every).Select(thing => thing.Id)];
    }

    private static void Move(Rhino.RhinoDoc doc, List<Guid> added, int layer)
    {
        foreach (Guid id in added)
        {
            if (doc.Objects.FindId(id) is { } thing && thing.Attributes.LayerIndex != layer)
            {
                thing.Attributes.LayerIndex = layer;
                thing.CommitChanges();
            }
        }
    }

    /// <summary>What the runner wrote, with what this side saw added.</summary>
    private static string Answer(bool ran, string file, List<Guid> added, string? layer)
    {
        StringBuilder json = new("{");

        if (!File.Exists(file))
        {
            // The runner writes its answer whatever the code does. No file means Python never started.
            json.Append("\"ok\":false,\"error\":").Append(Json.Quote(
                "Rhino did not run the script" + (ran ? "" : " (RunPythonScript was refused)")
                + ". Read console for what Rhino said."));
        }
        else
        {
            using JsonDocument written = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
            JsonElement root = written.RootElement;

            string? error = Json.Text(root, "error");

            json.Append("\"ok\":").Append(error is null ? "true" : "false");
            json.Append(",\"stdout\":").Append(Json.Quote(Json.Text(root, "stdout") ?? ""));

            if (Json.Text(root, "stderr") is { Length: > 0 } stderr)
            {
                json.Append(",\"stderr\":").Append(Json.Quote(stderr));
            }

            if (error is not null)
            {
                json.Append(",\"error\":").Append(Json.Quote(error));
                json.Append(",\"traceback\":").Append(Json.Quote(Json.Text(root, "traceback") ?? ""));
            }

            if (root.TryGetProperty("result", out JsonElement result))
            {
                json.Append(",\"result\":").Append(result.GetRawText());
            }
        }

        json.Append(",\"added\":").Append(Json.Number(added.Count));

        if (added.Count is > 0 and <= ListedIds)
        {
            json.Append(",\"ids\":[").Append(string.Join(",", added.Select(id => Json.Quote(id.ToString())))).Append(']');
        }

        if (layer is not null)
        {
            json.Append(",\"layer\":").Append(Json.Quote(layer));
        }

        return json.Append('}').ToString();
    }

    /// <summary>
    /// The runner: reads the code and the globals, runs the code, and writes everything it learned.
    /// </summary>
    /// <remarks>
    /// Paths reach Python as JSON strings, which are valid Python string literals. The traceback leaves out the
    /// runner's own frame, so its first line is the caller's code. Names start with <c>_phenome</c> to stay out
    /// of the way of the code's own. <paramref name="home"/> is the folder of a file run by path, and empty for
    /// code sent inline.
    /// </remarks>
    private static string Runner(string source, string shown, string values, string answer, string home) => $$"""
        #! python3
        import contextlib, io, json, sys, traceback

        _phenome_out = io.StringIO()
        _phenome_err = io.StringIO()
        _phenome_answer = {}

        try:
            with open({{Json.Quote(values)}}, encoding="utf-8") as _phenome_file:
                _phenome_scope = json.load(_phenome_file)

            _phenome_scope["__name__"] = "__main__"
            _phenome_scope["__file__"] = {{Json.Quote(shown)}}

            # A file run by path imports its neighbours, as it would when Rhino runs it directly.
            if {{Json.Quote(home)}} and {{Json.Quote(home)}} not in sys.path:
                sys.path.insert(0, {{Json.Quote(home)}})

            with open({{Json.Quote(source)}}, encoding="utf-8-sig") as _phenome_file:
                _phenome_source = _phenome_file.read()

            with contextlib.redirect_stdout(_phenome_out), contextlib.redirect_stderr(_phenome_err):
                try:
                    exec(compile(_phenome_source, {{Json.Quote(shown)}}, "exec"), _phenome_scope)
                except BaseException as _phenome_raised:
                    _phenome_answer["error"] = type(_phenome_raised).__name__ + ": " + str(_phenome_raised)
                    _phenome_answer["traceback"] = "".join(traceback.format_exception(
                        type(_phenome_raised), _phenome_raised, _phenome_raised.__traceback__.tb_next))

            if "result" in _phenome_scope:
                try:
                    json.dumps(_phenome_scope["result"])
                    _phenome_answer["result"] = _phenome_scope["result"]
                except BaseException:
                    _phenome_answer["result"] = repr(_phenome_scope["result"])
        except BaseException as _phenome_raised:
            _phenome_answer["error"] = type(_phenome_raised).__name__ + ": " + str(_phenome_raised)
            _phenome_answer["traceback"] = traceback.format_exc()
        finally:
            _phenome_answer["stdout"] = _phenome_out.getvalue()
            _phenome_answer["stderr"] = _phenome_err.getvalue()

            try:
                with open({{Json.Quote(answer)}}, "w", encoding="utf-8") as _phenome_file:
                    json.dump(_phenome_answer, _phenome_file, default=repr)
            except BaseException:
                pass
        """;
}
