using System.Reflection;

namespace Phenome.Apps.GrasshopperLink;

/// <summary>
/// The record of what did not work: every refused request, and every note an agent leaves about the gap
/// between what it expected and what it got.
/// </summary>
/// <remarks>
/// The journal keeps what happened; this keeps what failed to happen, which is the half that improves the
/// bridge. Until now a refusal reached the agent as one message and vanished - every fault report had to be
/// assembled by hand from memory, which is why the reports were good but rare.
/// <para>
/// Strictly local: one JSONL file under the user's own application data, never sent anywhere, no telemetry
/// of any kind. Sharing it is a deliberate act - hand the file over, or ask an agent to summarise it. It
/// carries the plugin version, so a report is attributable to a build rather than to a memory of one.
/// </para>
/// </remarks>
internal static class Friction
{
    private const long TooBig = 2_000_000;
    private const int PayloadKept = 600;

    private static readonly object Gate = new();

    // Without the build metadata a git hash appends: a subject line wants a version, not a commit.
    private static readonly string Version = (typeof(Friction).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.1.0").Split('+')[0];

    /// <summary>Where the log lives - said out loud at load, so nobody has to hunt for it.</summary>
    internal static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Phenome",
        "link-friction.jsonl");

    /// <summary>A request the server refused, with what was asked and what it said back.</summary>
    internal static void Refused(string verb, string payload, string said) =>
        Append($"{{\"kind\":\"refused\",\"verb\":{Json.Quote(verb)},"
            + $"\"asked\":{Json.Quote(Trim(payload))},\"said\":{Json.Quote(said)}}}");

    /// <summary>An agent's own words: what it expected, what it got, and anything else worth saying.</summary>
    internal static void Reported(string author, string expected, string got, string? notes)
    {
        Append($"{{\"kind\":\"report\",\"author\":{Json.Quote(author)},"
            + $"\"expected\":{Json.Quote(expected)},\"got\":{Json.Quote(got)}"
            + (notes is null ? "" : $",\"notes\":{Json.Quote(notes)}")
            + "}");
    }

    /// <summary>The last few entries, for whoever is writing the report up.</summary>
    internal static string Tail(int lines)
    {
        lock (Gate)
        {
            if (!File.Exists(Path))
            {
                return "{\"path\":" + Json.Quote(Path) + ",\"entries\":[]}";
            }

            string[] all = File.ReadAllLines(Path);
            IEnumerable<string> kept = all.Length > lines ? all[^lines..] : all;

            return "{\"path\":" + Json.Quote(Path) + ",\"entries\":[" + string.Join(",", kept) + "]}";
        }
    }

    /// <summary>
    /// The whole complaint as one file a person can read and send: what was expected, what happened, the
    /// session it happened in, and the friction behind it.
    /// </summary>
    /// <remarks>
    /// Assembled, saved, and handed back - never sent. Sending is the human's act, from their own mail
    /// client, after they have read what it says; an agent's part is to ask whether to prepare it.
    /// </remarks>
    /// <summary>Where reports go when nobody says otherwise: a role address, not a person.</summary>
    internal const string Intake = "hi+phenomelogs@object.pl";

    internal static (string Path, string Subject, string Body, string Mailto) Draft(
        string expected,
        string got,
        string session,
        string findings,
        string? to)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string file = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Path)!,
            $"phenome-link-report-{stamp}.md");

        string report = $"""
            # Phenome Link report

            - **When:** {DateTime.Now:yyyy-MM-dd HH:mm}
            - **Plugin:** Phenome Link {Version}
            - **Rhino:** {Rhino.RhinoApp.Version}

            ## Expected

            {expected}

            ## Got

            {got}

            ## Session

            {session}

            ## Composition review

            ```json
            {findings}
            ```

            ## Friction log (recent)

            ```jsonl
            {RecentLines(80)}
            ```

            ---
            Assembled locally by Phenome Link. Nothing here was sent anywhere; you are looking at the
            whole of it, and it goes no further than you choose to send it.
            """;

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                File.WriteAllText(file, report);
            }
            catch (Exception failure)
            {
                throw new InvalidOperationException($"Could not write the report: {failure.Message}");
            }
        }

        string subject = $"Phenome Link {Version}: {Shorten(expected)}";
        string body = $"{expected}\r\n\r\nGot instead:\r\n{got}\r\n\r\nThe full report, with the session "
            + $"and the friction log, is at:\r\n{file}\r\n\r\n(Attach that file before sending - mail "
            + "bodies are too small for the log itself.)\r\n";

        // A mailto rather than a send: the mail client opens with everything filled in, and the person
        // reads it, attaches the file and decides. Nothing leaves the machine on our say-so.
        string mailto = $"mailto:{Uri.EscapeDataString(to ?? Intake)}"
            + $"?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";

        return (file, subject, body, mailto);
    }

    private static string RecentLines(int lines)
    {
        try
        {
            if (!File.Exists(Path))
            {
                return "(nothing logged)";
            }

            string[] all = File.ReadAllLines(Path);

            return string.Join(Environment.NewLine, all.Length > lines ? all[^lines..] : all);
        }
        catch (Exception)
        {
            return "(the log could not be read)";
        }
    }

    private static string Shorten(string line)
    {
        string one = line.ReplaceLineEndings(" ").Trim();

        return one.Length <= 70 ? one : one[..70] + "…";
    }

    private static void Append(string entry)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

                // Halved rather than deleted when it grows: the oldest friction is the least useful, but
                // losing the lot on a threshold would throw away a session mid-report.
                if (File.Exists(Path) && new FileInfo(Path).Length > TooBig)
                {
                    string[] all = File.ReadAllLines(Path);

                    File.WriteAllLines(Path, all[(all.Length / 2)..]);
                }

                File.AppendAllText(
                    Path,
                    $"{{\"at\":\"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\",\"link\":{Json.Quote(Version)},"
                        + entry[1..] + Environment.NewLine);
            }
            catch (Exception)
            {
                // A log that cannot be written must not become the incident it was meant to describe.
            }
        }
    }

    private static string Trim(string payload) =>
        payload.Length <= PayloadKept ? payload : payload[..PayloadKept] + "…";
}
