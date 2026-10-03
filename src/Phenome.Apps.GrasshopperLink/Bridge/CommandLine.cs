using System.Collections.Concurrent;
using System.Text;

namespace Phenome.Apps.GrasshopperLink.Bridge;

/// <summary>
/// The tail of Rhino's command line, kept so an agent can read what Rhino said.
/// </summary>
/// <remarks>
/// This plugin writes a line to the command line on every request, and the user watching Rhino sees what the
/// agent is doing. Nothing comes back that way by itself; Rhino's answers are read here. "56 curves added to
/// selection" answers a selection, a script's print answers a script, and a warning about what a command is
/// about to do explains why a command did something surprising. Without this class an agent sees none of it
/// and has to route every fact it needs some other way, usually through a file.
/// <para>
/// Rhino captures what goes through Write and WriteLine when asked. The buffer is drained on idle into a ring
/// here, because <c>CapturedCommandWindowStrings</c> clears as it reads: two readers of the same buffer take
/// lines from each other. There is exactly one reader, and everyone else reads the ring.
/// </para>
/// <para>
/// This cannot read the command line <em>while</em> the UI thread is blocked, because the drain runs on that
/// thread. A long script's output arrives in one piece when the script ends. In the meantime <c>pulse</c>
/// says whether there will be an end.
/// </para>
/// </remarks>
internal static class CommandLine
{
    /// <summary>500 lines hold what a command said and stay cheap to keep. The oldest lines fall off.</summary>
    private const int Kept = 500;

    private static readonly Queue<string> lines = new();

    /// <summary>
    /// The link's own lines, kept in a ring of their own.
    /// </summary>
    /// <remarks>
    /// The plugin's own lines are filtered out of the console: an agent must not read its own requests back as
    /// though Rhino had said them. Discarded entirely, they would leave the link's own faults unreadable through
    /// the link, when they are needed most. When a second Rhino complains at startup, these lines tell whether
    /// the complaint came from the link. They are kept in their own ring and served on request.
    /// </remarks>
    private static readonly Queue<string> mine = new();

    private static readonly object gate = new();

    /// <summary>Lines this plugin itself wrote, which the drain leaves out of the console.</summary>
    private static readonly ConcurrentDictionary<string, byte> ours = new();

    private static long dropped;

    /// <summary>
    /// One client for the whole process instead of one per call.
    /// </summary>
    /// <remarks>
    /// A fresh <see cref="HttpClient"/> per request leaves its socket in TIME_WAIT after disposal. A verb
    /// polled in a loop, as reading the console is, eventually exhausts the ephemeral port range and starts
    /// failing for a reason unrelated to either end. One long-lived client is the documented pattern, and
    /// nothing here needs per-call configuration.
    /// </remarks>
    private static readonly HttpClient Loopback = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>
    /// True when the Rhino-side plugin owns the capture. This plugin then reads the lines from that plugin.
    /// </summary>
    private static bool borrowed;

    internal static void Start()
    {
        Rhino.RhinoApp.InvokeOnUiThread(() =>
        {
            // Capture already on means the Rhino plugin is loaded and draining. The buffer clears as it is
            // read, and whichever idle handler runs first takes that instalment: with a second drain each
            // reader would get about half the lines.
            if (Rhino.RhinoApp.CommandWindowCaptureEnabled)
            {
                borrowed = true;
                return;
            }

            Rhino.RhinoApp.CommandWindowCaptureEnabled = true;
            Rhino.RhinoApp.Idle += (_, _) => Drain();
        });
    }

    /// <summary>Remembers a line this plugin is about to write. The drain leaves it out.</summary>
    internal static void Ours(string line) => ours[line.TrimEnd()] = 0;

    /// <summary>
    /// Whether a captured line was written by this plugin and not by Rhino.
    /// </summary>
    /// <remarks>
    /// There are three tests, because no single one catches every line. The exact line is claimed before it is
    /// written, which catches it when capture returns it whole. Anything the plugin announces about itself
    /// starts with its own name. The request echo has a fixed shape, a bracketed clock at the start of the
    /// line, and is matched directly: an agent must not read its own requests back as Rhino's answers.
    /// <para>
    /// This check and <see cref="LinkServer.Echo"/> are one decision, written in the only two places it can be
    /// written: capture returns a string with nothing attached to say who wrote it. Changing the echo's shape
    /// means changing this too; the symptom of forgetting is <c>/console</c> answering with the link's own
    /// lines in it.
    /// </para>
    /// </remarks>
    private static bool IsOurs(string line)
    {
        if (ours.TryRemove(line.TrimEnd(), out _))
        {
            return true;
        }

        if (line.StartsWith("Phenome Link:", StringComparison.Ordinal))
        {
            return true;
        }

        // [hh:mm:ss]: the opening bracket, six digits and two colons in fixed places, then the close.
        return line.Length > 10
            && line[0] == '['
            && line[3] == ':'
            && line[6] == ':'
            && line[9] == ']'
            && char.IsDigit(line[1])
            && char.IsDigit(line[2]);
    }

    private static void Drain()
    {
        string[]? captured;

        try
        {
            captured = Rhino.RhinoApp.CapturedCommandWindowStrings(clearBuffer: true);
        }
        catch (Exception)
        {
            return;
        }

        if (captured is null || captured.Length == 0)
        {
            return;
        }

        lock (gate)
        {
            foreach (string raw in captured)
            {
                // Rhino writes partial lines too (a prompt, then its answer), and an entry is not always one
                // line. Blank entries are the newlines between them.
                string line = raw.TrimEnd('\r', '\n');
                if (line.Length == 0)
                {
                    continue;
                }

                if (IsOurs(line))
                {
                    // The request echo is dropped; the plugin's own notices go to their own ring.
                    if (line.StartsWith("Phenome Link:", StringComparison.Ordinal))
                    {
                        mine.Enqueue(line);

                        while (mine.Count > Kept)
                        {
                            mine.Dequeue();
                        }
                    }

                    continue;
                }

                lines.Enqueue(line);

                while (lines.Count > Kept)
                {
                    lines.Dequeue();
                    dropped++;
                }
            }
        }
    }

    /// <summary>The last lines, newest last.</summary>
    /// <param name="tail">How many lines back to return.</param>
    /// <param name="ours">
    /// True for the link's own lines instead of Rhino's. They are the plugin's account of itself, to be read
    /// when the bridge is suspected and Rhino is not.
    /// </param>
    internal static string Tail(int tail, bool ours = false)
    {
        // The link's own lines are never borrowed: the Rhino half keeps its own account, and this ring holds
        // what this assembly wrote.
        if (!ours && borrowed && FromRhinoLink(tail) is { } answer)
        {
            return answer;
        }

        string[] recent;
        long lost;

        lock (gate)
        {
            Queue<string> source = ours ? mine : lines;

            recent = source.Skip(Math.Max(0, source.Count - tail)).ToArray();
            lost = ours ? 0 : dropped;
        }

        StringBuilder json = new();
        json.Append("{\"ok\":true,\"lines\":[");

        for (int i = 0; i < recent.Length; i++)
        {
            if (i > 0)
            {
                json.Append(',');
            }

            json.Append(Json.Quote(recent[i]));
        }

        json.Append(']');
        json.Append(",\"kept\":").Append(recent.Length);
        json.Append(",\"dropped\":").Append(lost);
        json.Append(",\"note\":").Append(Json.Quote(ours
            ? "The link's own lines, which /console leaves out so an agent does not read its requests back as Rhino's answers."
            : "Drained when the UI thread is idle; a long command's output arrives when it ends. Ask /pulse for what is happening now."));
        json.Append('}');

        return json.ToString();
    }

    /// <summary>
    /// The same tail, read from the Rhino-side link in this process.
    /// </summary>
    /// <remarks>
    /// Read over loopback, without a method call. The two plugins are separate assemblies with no reference to
    /// each other by design: the Rhino one must not need Grasshopper. The port file is named by process id, and
    /// the process is this one; there is no discovery step to get wrong. Returns null when the Rhino-side link
    /// cannot be reached. The caller then falls back to its own ring, which for a session that started this way
    /// is empty but accurate.
    /// </remarks>
    private static string? FromRhinoLink(int tail)
    {
        try
        {
            string file = Path.Combine(
                Path.GetTempPath(),
                $"phenome-rhino-{Environment.ProcessId}.port");

            if (!File.Exists(file))
            {
                return null;
            }

            string port = File.ReadAllText(file).Trim();

            return Loopback.GetStringAsync($"http://127.0.0.1:{port}/console?tail={tail}")
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
