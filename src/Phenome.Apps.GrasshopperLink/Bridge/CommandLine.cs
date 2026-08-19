using System.Collections.Concurrent;
using System.Text;

namespace Phenome.Apps.GrasshopperLink.Bridge;

/// <summary>
/// The tail of Rhino's command line, kept so an agent can read what Rhino said.
/// </summary>
/// <remarks>
/// The command line has been one-way until now: this plugin writes a line into it on every request, so
/// the human watching Rhino can see an agent's hands move, and nothing comes back the other way. But
/// that is where Rhino answers. "56 curves added to selection" is the answer to a selection; a script's
/// print is the answer to a script; a warning about what a command is about to do is the reason a
/// command did something surprising. An agent working through this link could see none of it, and had
/// to arrange for every fact it needed to come back some other way - usually by writing a file.
/// <para>
/// Rhino will capture what goes through Write and WriteLine if asked. The buffer is drained on idle
/// into a ring here, because <c>CapturedCommandWindowStrings</c> clears as it reads: two readers of the
/// same buffer steal from each other, so there is exactly one, and everyone else reads the ring.
/// </para>
/// <para>
/// What this cannot do: show you the command line <em>while</em> the UI thread is blocked, because the
/// drain runs on that thread. A long script's output arrives in one piece when the script ends. Pulse
/// is the verb for the meantime - it says whether there will be an end.
/// </para>
/// </remarks>
internal static class CommandLine
{
    /// <summary>Long enough to hold what a command said, short enough to stay cheap. Oldest lines fall off.</summary>
    private const int Kept = 500;

    private static readonly Queue<string> lines = new();

    /// <summary>
    /// The link's own lines, kept separately rather than thrown away.
    /// </summary>
    /// <remarks>
    /// Filtering the plugin's own voice out of the console is right: an agent reading its own requests back
    /// as though Rhino had said them is the one thing this must not do. But discarding them made the link's
    /// own faults unreadable *through the link*, which is exactly when they are wanted -- diagnosing why a
    /// second Rhino complained at startup meant no way to see whether the complaint was even ours. Kept in
    /// their own ring and served on request.
    /// </remarks>
    private static readonly Queue<string> mine = new();

    private static readonly object gate = new();

    /// <summary>Lines this plugin itself wrote, so the echo of a request is not read back as news.</summary>
    private static readonly ConcurrentDictionary<string, byte> ours = new();

    private static long dropped;

    /// <summary>
    /// One client for the whole process, rather than one per call.
    /// </summary>
    /// <remarks>
    /// A fresh <see cref="HttpClient"/> per request leaves its socket in TIME_WAIT after disposal, so a verb
    /// polled in a loop -- which reading the console is -- eventually runs the ephemeral port range down and
    /// starts failing for a reason that has nothing to do with either end. One long-lived client is the
    /// documented shape and there is nothing here that needs per-call configuration.
    /// </remarks>
    private static readonly HttpClient Loopback = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>
    /// True when the Rhino-side plugin got here first and owns the capture, so this one reads from it.
    /// </summary>
    private static bool borrowed;

    internal static void Start()
    {
        Rhino.RhinoApp.InvokeOnUiThread(() =>
        {
            // Capture already on means the Rhino plugin is loaded and draining. Starting a second drain
            // would not double the lines - it would halve them, because the buffer clears as it is read
            // and whichever idle handler ran first would take that instalment for itself.
            if (Rhino.RhinoApp.CommandWindowCaptureEnabled)
            {
                borrowed = true;
                return;
            }

            Rhino.RhinoApp.CommandWindowCaptureEnabled = true;
            Rhino.RhinoApp.Idle += (_, _) => Drain();
        });
    }

    /// <summary>Remembers a line this plugin is about to write, so the drain can leave it out.</summary>
    internal static void Ours(string line) => ours[line.TrimEnd()] = 0;

    /// <summary>
    /// Whether a captured line is this plugin's own voice rather than Rhino's.
    /// </summary>
    /// <remarks>
    /// Three ways, because one is not enough. The exact line is claimed before it is written, which
    /// catches it when the capture hands it back whole. Anything this plugin announces about itself
    /// starts with its own name. And the request echo has a fixed shape - two spaces, a clock, a verb -
    /// which is worth matching directly, since an agent reading its own requests back as if Rhino had
    /// said them is the one thing this must not do.
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

        return line.Length > 12
            && line[0] == ' '
            && line[1] == ' '
            && line[4] == ':'
            && line[7] == ':'
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
                // Rhino writes partial lines too - a prompt, then its answer - so what arrives is not
                // always one line per entry. Blank entries are the newlines between them.
                string line = raw.TrimEnd('\r', '\n');
                if (line.Length == 0)
                {
                    continue;
                }

                if (IsOurs(line))
                {
                    // The request echo is noise even to us; anything the plugin says about itself is not.
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
    /// True for the link's own lines instead of Rhino's - the plugin's account of itself, which is what to
    /// read when the suspicion is that the bridge rather than Rhino is at fault.
    /// </param>
    internal static string Tail(int tail, bool ours = false)
    {
        // Not borrowed for our own lines: the Rhino half keeps its own account, and this ring holds what this
        // assembly said.
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
            : "Drained when the UI thread breathes, so a long command's output arrives when it ends. Ask /pulse for what is happening now."));
        json.Append('}');

        return json.ToString();
    }

    /// <summary>
    /// The same tail, read from the Rhino-side link in this very process.
    /// </summary>
    /// <remarks>
    /// Loopback rather than a method call, because the two plugins are separate assemblies that know
    /// nothing of each other by design - the Rhino one must not need Grasshopper, and this is the seam
    /// that keeps it that way. The port file is named by process id, and the process is this one, so
    /// there is no discovery to get wrong. Null when it cannot be reached, and the caller falls back to
    /// its own ring, which for a session that started this way is empty but honest.
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
