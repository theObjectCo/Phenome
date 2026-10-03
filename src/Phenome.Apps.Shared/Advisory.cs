using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Phenome.Apps;

/// <summary>
/// Stops a released version of the link that is known to be unsafe, on machines outside the maintainers' control.
/// </summary>
/// <remarks>
/// The link runs on other people's computers and updates when they choose to. When a released version has a
/// hole that a web page can drive, there is otherwise no way to reach the affected users: deleting a release
/// does not uninstall anything, and the people at risk are those not watching the repository. The plugin
/// therefore reads one static file and acts on it locally. Four properties matter:
/// <para>
/// <b>Fails open.</b> With no network, a blocked domain, a malformed file or GitHub down, the link starts. A
/// safety notice that stops the tool when the network hiccups is worse than the problem it guards against, and
/// would couple local work to a remote service. Blocking the domain defeats the check entirely. That is
/// accepted: the check is there to reach users who want the notice, and a determined opt-out is out of scope.
/// </para>
/// <para>
/// <b>Sends nothing.</b> The whole file is fetched and compared locally; no server is asked whether this
/// version is safe. GitHub sees that a public file was fetched from this machine's address and learns nothing
/// about who runs which version. There is no telemetry to secure, document or disable.
/// </para>
/// <para>
/// <b>Disables only the link.</b> Rhino and Grasshopper behave exactly as before. Removing the user's CAD
/// application because the bridge has a bug would be disproportionate. The bridge is the only part its
/// maintainers can withdraw.
/// </para>
/// <para>
/// <b>Can be overridden.</b> <c>PHENOME_IGNORE_ADVISORY=1</c> makes the link start anyway, with a notice at
/// every startup. Without the switch a bad commit could stop every installation with no recourse. The machine
/// belongs to its owner and the domain can be blocked anyway; a documented switch is the clearer design.
/// </para>
/// <para>
/// The file lives in the same repository the releases come from. Whoever can edit it can already publish a
/// malicious build, and the file grants no new trust. Setting the flag is a public commit, which a file on a
/// private server would not be.
/// </para>
/// </remarks>
internal static class Advisory
{
    private const string Source =
        "https://raw.githubusercontent.com/theObjectCo/Phenome/main/security/advisory.json";

    /// <summary>What the notice says about the version running here.</summary>
    internal sealed class Verdict
    {
        internal bool Blocked { get; init; }
        internal string Reason { get; init; } = "";
        internal string More { get; init; } = "";
        internal string MinimumSafe { get; init; } = "";

        internal string Sentence =>
            $"Phenome Link {Running} has been withdrawn: {Reason} "
            + $"Update to {MinimumSafe} or later. {More}";
    }

    /// <summary>Set once a notice has withdrawn this version; every server then refuses its verbs.</summary>
    /// <remarks>
    /// Stored here, outside any one server, because a withdrawal must reach all of them. Kept on the canvas
    /// link only, it would stop the canvas while the Rhino half kept answering. The Rhino half types at the
    /// command line and can run code just as well.
    /// </remarks>
    internal static Verdict? Withdrawn { get; private set; }

    /// <summary>Whether the owner of this machine has chosen to run a withdrawn version anyway.</summary>
    internal static bool Overridden =>
        Environment.GetEnvironmentVariable("PHENOME_IGNORE_ADVISORY") is "1" or "true" or "TRUE";

    /// <summary>The version of the assembly this code was compiled into.</summary>
    internal static Version Running =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// Fetches the notice in the background and calls back only if this version is withdrawn.
    /// </summary>
    /// <remarks>
    /// Runs in the background: a plugin that waits on the network before Grasshopper can draw is one people
    /// uninstall. The answer comes through a callback because it arrives after the decision to start. The
    /// caller stops serving when the callback runs, and until then the link works. The result is not cached:
    /// a notice published a minute ago takes effect on the current run.
    /// </remarks>
    internal static void Watch(Action<Verdict> withdrawn)
    {
        if (Overridden)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                Verdict? verdict = await Fetch();

                if (verdict is { Blocked: true })
                {
                    // Set before the callback: every server refuses its verbs even if the caller only logs.
                    Withdrawn = verdict;
                    withdrawn(verdict);
                }
            }
            catch (Exception)
            {
                // Fails open and stays silent. The check is a courtesy to the user and guarantees nothing to
                // the maintainers. A warning about a failed safety check would be noise on every machine behind
                // a proxy.
            }
        });
    }

    private static async Task<Verdict?> Fetch()
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(8) };

        // A plain GET for a public file: no version, machine, or account is sent.
        string json = await client.GetStringAsync(Source);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string minimum = Json.Text(root, "minimum_safe") ?? "";

        if (!Version.TryParse(minimum, out Version? safe))
        {
            return null;
        }

        return new Verdict
        {
            Blocked = Running < safe,
            Reason = Json.Text(root, "reason") ?? "a security problem was found in this version.",
            More = Json.Text(root, "more") ?? "",
            MinimumSafe = minimum,
        };
    }
}
