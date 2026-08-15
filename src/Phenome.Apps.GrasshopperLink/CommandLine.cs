using System.Collections.Concurrent;
using System.Text;

namespace Phenome.Apps.GrasshopperLink;

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
    private static readonly object gate = new();

    /// <summary>Lines this plugin itself wrote, so the echo of a request is not read back as news.</summary>
    private static readonly ConcurrentDictionary<string, byte> ours = new();

    private static long dropped;

    internal static void Start()
    {
        Rhino.RhinoApp.InvokeOnUiThread(() =>
        {
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

    /// <summary>The last <paramref name="tail"/> lines, newest last.</summary>
    internal static string Tail(int tail)
    {
        string[] recent;
        long lost;

        lock (gate)
        {
            recent = lines.Skip(Math.Max(0, lines.Count - tail)).ToArray();
            lost = dropped;
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
        json.Append(",\"note\":").Append(Json.Quote(
            "Drained when the UI thread breathes, so a long command's output arrives when it ends. Ask /pulse for what is happening now."));
        json.Append('}');

        return json.ToString();
    }
}
