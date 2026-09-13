using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Phenome.Apps;

/// <summary>A way to stop a version of the link that is known to be unsafe, on machines we do not own.</summary>
/// <remarks>
/// The link runs on other people's computers and updates when they decide to update. When a released
/// version turns out to have a hole that a web page can drive, there is otherwise no way to reach anybody:
/// deleting a release does not uninstall anything, and the people at risk are precisely the ones not
/// reading the repository.
/// <para>
/// So the plugin reads one static file and decides for itself. Four properties matter and each was chosen
/// against an obvious alternative.
/// </para>
/// <para>
/// <b>It fails open.</b> No network, a blocked domain, a malformed file, GitHub down - the link starts. A
/// safety notice that bricks the tool when the internet hiccups is a worse failure than the one it guards
/// against, and it repeats the mistake of tying local work to a remote service. Anyone who blocks the
/// domain defeats this entirely, and that is accepted: the purpose is to reach honest users, not to win
/// against someone avoiding it.
/// </para>
/// <para>
/// <b>It sends nothing.</b> The whole file is fetched and compared here, rather than asking a server
/// whether *this version* is safe. GitHub learns that somebody fetched a file, which it would learn from
/// any download, and not who runs what. There is no telemetry to secure, publish a policy about, or be
/// asked to turn off.
/// </para>
/// <para>
/// <b>It disables the link and nothing else.</b> Grasshopper and Rhino carry on exactly as before. Taking
/// away somebody's CAD application because our bridge has a bug would be wildly out of proportion, and the
/// bridge is the only part that is ours to withdraw.
/// </para>
/// <para>
/// <b>It can be overridden.</b> <c>PHENOME_IGNORE_ADVISORY=1</c> in the environment, and the link starts
/// anyway, saying at every startup that it is doing so. A bad commit here could otherwise stop every
/// installation with no recourse, and the machine belongs to its owner. Somebody determined can block the
/// domain regardless, so the honest thing is a documented switch rather than a pretence that there is none.
/// </para>
/// <para>
/// It lives in the same repository the releases come from. Whoever can edit it can already publish a
/// malicious build, so no new trust is granted - and setting the flag is a public commit, which a file on a
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
    /// Here rather than on one of the servers, because a withdrawal has to reach all of them. The first
    /// draft put it on the canvas link alone, which would have stopped the canvas while the Rhino half went
    /// on answering - and that half types at the command line, so it is no less able to run code. A version
    /// is withdrawn or it is not; it cannot be withdrawn by half.
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
    /// Background, because a plugin that waits on the network before Grasshopper can draw is a plugin
    /// people uninstall. Callback rather than a return value, because the answer arrives after the decision
    /// to start has already been taken - the caller stops serving when it hears, and until then the link
    /// works. A notice published one minute ago therefore takes effect on this run rather than the next,
    /// which is the point of not deciding from a cache.
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
                    // Set before the callback, so a caller that only logs has still stopped serving.
                    Withdrawn = verdict;
                    withdrawn(verdict);
                }
            }
            catch (Exception)
            {
                // Fails open, deliberately and silently: this is a courtesy to the user, not a
                // guarantee to us, and a warning about a failed safety check is noise on every
                // machine behind a proxy.
            }
        });
    }

    private static async Task<Verdict?> Fetch()
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(8) };

        // No version, no machine, no account - a plain GET for a public file.
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
