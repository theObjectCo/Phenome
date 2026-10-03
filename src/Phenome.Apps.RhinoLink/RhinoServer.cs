using System.Net;
using System.Text;
using System.Text.Json;

namespace Phenome.Apps.RhinoLink;

/// <summary>
/// The loopback interface to the Rhino process: its state, and how to answer whatever is blocking it.
/// </summary>
/// <remarks>
/// Follows the conventions of the canvas link (plain HTTP on 127.0.0.1, one JSON per response, an ephemeral port
/// in a discovery file) with a discovery file of its own, because the two report on different things. The
/// canvas link reports on a document. This server reports on a process and answers when the document link
/// cannot.
/// <para>
/// <c>/pulse</c>, <c>/dialog</c>, <c>/dismiss</c>, <c>/escape</c> and <c>/console</c> never touch the Rhino UI thread,
/// and that is a requirement: they must work while that thread is held, which is the situation this server
/// exists for. <c>/command</c>, <c>/doc</c>, <c>/plugins</c>, <c>/load</c>, <c>/screenshot</c> and <c>/camera</c>
/// run on the UI thread through <see cref="Ui.On"/> and wait while it is held.
/// </para>
/// </remarks>
internal static class RhinoServer
{
    private static HttpListener? listener;

    internal static int Port { get; private set; }

    private const string Description = """
        {
          "phenome": "rhino-link",
          "version": "0.1",
          "protocol": {
            "GET /": "this description",
            "GET /pulse": "whether Rhino is idle, busy or blocked. Answered off the UI thread; it answers when nothing else does. 'busy' names the running command and how long it has run: wait. 'blocked' names the open dialog and lists its buttons: nothing will answer until it is clicked",
            "POST /dismiss": "SUPERSEDED by /dialog, and kept working. {button?, key?, expect?} - press a button by name, type a key, or close it when neither is given. When /pulse says clickable:false the dialog draws its own buttons and only a key reaches it",
            "POST /dialog": "{button?, key?, close?, expect?} - answer the open dialog. Nothing is assumed: with no answer given this refuses and lists the buttons, because a decline by omission cannot be told from a decline by decision. 'close' declines explicitly. No verb here guesses which button means yes: on a save prompt the affirmative is whichever of Save and Don't Save the caller means",
            "POST /escape": "{times?} - post Escape to Rhino, cancelling whatever it is waiting for. Use it where /dismiss cannot answer: a command waiting on a pick is not a dialog. Nothing is disabled and there is no window to click, yet the UI thread is held and every other verb reports 'busy' as though waiting would help. Scripting an interactive command is the ordinary way to get here. 'times' cancels that many levels, one by default",
            "POST /command": "{script} - run a Rhino command script. The canvas link has this verb too; it is also here because Rhino runs commands and Grasshopper need not be open for it",
            "GET /doc": "the Rhino document: name, layers, object count",
            "GET /console": "?tail=50 - the tail of Rhino's command line, which is where Rhino writes its output. There is one capture per Rhino and this is it; the canvas link reads from here",
            "GET /plugins": "?all=false - every plug-in Rhino has a record of, with the runtime it would load into: loaded, dotnet, loadProtected, the path Rhino has recorded and the registry key. Use it to answer 'why is the plug-in not loading' without manual registry checks. Shipped plug-ins are left out unless all=true, because there are a hundred of them",
            "POST /load": "{id?, path?} - load a plug-in explicitly, with no confirmation dialog, and load it again even after a previous attempt failed. Rhino does not retry a plug-in whose load previously failed, and without this the ordinary build-and-load loop appears to do nothing the second time round. Answers with Rhino's resulting record state instead of a single failure word",
            "GET /screenshot": "?width=640&zoomExtents=true - the active viewport as PNG (base64), framed on the geometry for the capture and the camera put back where the user left it",
            "GET /camera": "where the active viewport is looking: projection, location, target, up, 35mm lens length and the viewport's pixel size",
            "POST /camera": "{location?, target?, up?, lens?, projection?} - aim the active viewport; only the fields passed change. Use it to frame a view deliberately: Rhino's Zoom is interactive, and a scripted one waits for a pick that never comes, holding the UI thread and blocking every other verb"
          },
          "why": "Grasshopper's link exists only once Grasshopper has been started and cannot report on anything that happens before that, including a dialog on startup, when nothing else can answer.",
          "discovery": "%TEMP%/phenome-rhino-<rhino pid>.port holds this port"
        }
        """;

    internal static void Start()
    {
        Pulse.Start();
        CommandLine.Start();

        // Bind first, and record the port only once bound. Two Rhinos starting together can race for the same
        // ephemeral port, and recording first would leave the loser with a discovery file for a port nothing
        // listens on.
        listener = Loopback.Listen(out int port);
        Port = port;

        WritePortFile();

        Task.Run(async () =>
        {
            while (listener is not null && listener.IsListening)
            {
                try
                {
                    HttpListenerContext context = await listener.GetContextAsync();
                    Answer(context);
                }
                catch (Exception) when (listener is null || !listener.IsListening)
                {
                    // The listener was shut down mid-await, which is expected.
                }
                catch (Exception)
                {
                    // Nowhere to report it: writing to the command line needs the UI thread, and this server
                    // exists for exactly the times that thread is unavailable.
                }
            }
        });
    }

    internal static void Stop()
    {
        try
        {
            listener?.Stop();
            listener?.Close();
        }
        catch (Exception)
        {
            // Shutting down; nothing left to tell.
        }
        finally
        {
            listener = null;
            DeletePortFile();
        }
    }

    private static void Answer(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
        string method = context.Request.HttpMethod;

        // A page the user visits can reach loopback, and binding to 127.0.0.1 does not protect against it (see
        // Browser.Refuse). This half reports on the process and can type at the command line, and it needs the
        // same guard as the canvas half.
        if (Browser.Refuse(context.Request, Port) is { } refused)
        {
            Respond(context.Response, 403, $"{{\"ok\":false,\"error\":{Json.Quote(refused)}}}");
            return;
        }

        // A withdrawn version is refused here too, except for the empty-path greeting. The greeting still
        // answers, and a client sees the session instead of a dead port. The refusal reason comes from the same
        // state as the canvas half's, because a version is either withdrawn or it is not.
        if (Advisory.Withdrawn is { } notice && path.Length != 0)
        {
            Respond(context.Response, 403, $"{{\"ok\":false,\"error\":{Json.Quote(notice.Sentence)}}}");
            return;
        }

        string payload = method == "POST" ? ReadBody(context.Request) : "";

        try
        {
            string body = (method, path) switch
            {
                ("GET", "") => Description,
                ("GET", "/pulse") => Pulse.Report(),
                ("POST", "/dismiss") => Dismissed(payload),
                ("POST", "/dialog") => AnswerDialog(payload),
                ("POST", "/escape") => Escaped(payload),
                ("POST", "/command") => Commands.Run(payload),
                ("GET", "/doc") => Commands.Document(),
                ("GET", "/plugins") => Plugins.List(
                    string.Equals(context.Request.QueryString["all"], "true", StringComparison.OrdinalIgnoreCase)),
                ("POST", "/load") => Plugins.Load(payload),
                ("GET", "/screenshot") => View.Screenshot(context.Request),
                ("GET", "/camera") => View.ReadCamera(),
                ("POST", "/camera") => View.AimCamera(payload),
                ("GET", "/console") => CommandLine.Tail(
                    int.TryParse(context.Request.QueryString["tail"], out int back) ? Math.Clamp(back, 1, 500) : 50),
                _ => throw new KeyNotFoundException($"There is no {method} {path}. GET / describes what there is."),
            };

            Respond(context.Response, 200, body);
        }
        catch (KeyNotFoundException missing)
        {
            Respond(context.Response, 404, $"{{\"ok\":false,\"error\":{Json.Quote(missing.Message)}}}");
        }
        catch (Exception failure)
        {
            Respond(context.Response, 500, $"{{\"ok\":false,\"error\":{Json.Quote(failure.Message)}}}");
        }
    }

    private static string Dismissed(string payload)
    {
        string? button = null;
        string? expect = null;
        string? key = null;

        if (!string.IsNullOrWhiteSpace(payload))
        {
            using JsonDocument request = JsonDocument.Parse(payload);
            button = Json.Text(request, "button");
            expect = Json.Text(request, "expect");

            // The protocol text above and the MCP schema both offer 'key'. Rhino 8's own dialogs have no
            // clickable buttons, and a key is the only way to answer them, which are exactly the dialogs this
            // half exists for.
            key = Json.Text(request, "key");
        }

        return Pulse.Dismiss(button, expect, key);
    }

    /// <summary>Answers the open dialog; takes no action when nothing was given.</summary>
    private static string AnswerDialog(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Pulse.Answer(null, null, close: false, null);
        }

        using JsonDocument request = JsonDocument.Parse(payload);

        return Pulse.Answer(
            Json.Text(request, "button"),
            Json.Text(request, "key"),
            request.RootElement.TryGetProperty("close", out JsonElement asked)
                && asked.ValueKind == JsonValueKind.True,
            Json.Text(request, "expect"));
    }

    private static string Escaped(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Pulse.Escape(1);
        }

        using JsonDocument request = JsonDocument.Parse(payload);

        return Pulse.Escape(Json.Int(request, "times", 1));
    }

    private static string ReadBody(HttpListenerRequest request)
    {
        using StreamReader reader = new(request.InputStream, request.ContentEncoding);
        return reader.ReadToEnd();
    }

    private static void Respond(HttpListenerResponse response, int status, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);

        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.OutputStream.Close();
    }

    /// <summary>
    /// The port file, named by Rhino's process id, from which a client finds the specific Rhino instance it wants.
    /// </summary>
    private static string PortFile =>
        Path.Combine(Path.GetTempPath(), $"phenome-rhino-{Environment.ProcessId}.port");

    private static void WritePortFile()
    {
        try
        {
            File.WriteAllText(PortFile, Port.ToString());
        }
        catch (Exception)
        {
            // Without the file a client has to be told the port, which is worse but not fatal.
        }
    }

    private static void DeletePortFile()
    {
        try
        {
            if (File.Exists(PortFile)) File.Delete(PortFile);
        }
        catch (Exception)
        {
            // A stale port file answers nothing and is replaced on the next start.
        }
    }
}
