using System.Text;
using System.Text.Json;

using Rhino.PlugIns;

namespace Phenome.Apps.RhinoLink;

/// <summary>
/// What Rhino makes of the plug-ins it knows about, and loading one on purpose.
/// </summary>
/// <remarks>
/// Here rather than on the canvas link, and that placement is the point. Somebody developing a Rhino
/// plug-in has no definition open and no reason to start Grasshopper, yet the canvas link was the only
/// half that answered this - so the question "did my plug-in load" needed a canvas nobody wanted. Worse,
/// this plugin loads with Rhino itself, which means it is alive before Grasshopper exists: the moment a
/// plug-in fails at startup is exactly the moment the other half is not there to be asked.
/// <para>
/// The fields are chosen from a report of a session lost to this. An agent building a plug-in found it
/// installed and not loading, with nothing logged anywhere, and spent the morning proving the registry
/// innocent by hand - <c>reg query</c> against a plug-in that worked. Every fact it needed was already in
/// Rhino's own record and none of it was reachable: whether Rhino has it at all, the path Rhino believes,
/// whether Rhino thinks it is managed, whether it is load protected, and the registry key itself.
/// </para>
/// </remarks>
internal static class Plugins
{
    /// <summary>
    /// Every plug-in Rhino has a record of, with the runtime it would have to load into.
    /// </summary>
    /// <remarks>
    /// The runtime is at the top because it decides whether an assembly can load at all and is the
    /// hardest fact to get from outside. Rhino 8 hosts two CLRs - the executable is .NET Framework and
    /// there is a .NET Core half beside it - so "Rhino 8 is .NET 8" is true of one mode and false of the
    /// other, and a plug-in has to match whichever one is running. A report of a lost morning blamed the
    /// target framework as a constant when it is a mode; this answers the mode.
    /// <para>
    /// Shipped plug-ins are left out unless asked for. There are a hundred of them, they are never the
    /// suspect, and a list where the answer is buried at position sixty is a list nobody reads.
    /// </para>
    /// </remarks>
    internal static string List(bool includeShipped) => Ui.On(() =>
    {
        StringBuilder json = new("{\"ok\":true,\"runtime\":");

        json.Append(Json.Quote(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription));
        json.Append(",\"rhino\":").Append(Json.Quote(Rhino.RhinoApp.Version.ToString()));
        json.Append(",\"plugins\":[");

        bool first = true;

        foreach (Guid id in PlugIn.GetInstalledPlugIns(localizedPlugInName: false).Keys)
        {
            if (Record(id) is not { } info)
            {
                continue;
            }

            if (info.ShipsWithRhino && !includeShipped)
            {
                continue;
            }

            if (!first)
            {
                json.Append(',');
            }

            first = false;

            json.Append("{\"id\":").Append(Json.Quote(id.ToString()));
            json.Append(",\"name\":").Append(Json.Quote(info.Name ?? ""));
            json.Append(",\"version\":").Append(Json.Quote(info.Version ?? ""));
            json.Append(",\"path\":").Append(Json.Quote(info.FileName ?? ""));

            // The four that answer "why is it not loading", rather than the one that says it is not.
            json.Append(",\"loaded\":").Append(info.IsLoaded ? "true" : "false");
            json.Append(",\"dotnet\":").Append(info.IsDotNet ? "true" : "false");
            json.Append(",\"loadProtected\":").Append(info.IsLoadProtected(out bool silently) ? "true" : "false");
            json.Append(",\"loadsSilently\":").Append(silently ? "true" : "false");
            json.Append(",\"registryPath\":").Append(Json.Quote(info.RegistryPath ?? ""));

            if (info.ShipsWithRhino)
            {
                json.Append(",\"shipped\":true");
            }

            json.Append('}');
        }

        return json.Append("]}").ToString();
    });

    /// <summary>
    /// Loads a plug-in on purpose: no dialog, and no refusal because a previous attempt failed.
    /// </summary>
    /// <remarks>
    /// Both flags exist because of the loop this verb is for. <c>loadQuietly</c> answers the confirmation
    /// a load-protected plug-in raises - and every plug-in somebody installed is load protected, so that
    /// dialog is the normal case rather than the odd one. Turning the prompt off globally is not the same
    /// thing and is a trap: with <c>AskOnLoadProtection</c> false, Rhino silently does not load a protected
    /// plug-in at all, which is the silence a field report spent a morning on. The dialog and the silence
    /// are two settings of one switch, and this verb needs neither.
    /// <para>
    /// <c>forceLoad</c> is the other half. Rhino remembers a failed load and will not try again, so the
    /// ordinary development loop - fix the code, rebuild, load it - does nothing at all on the second pass
    /// and looks exactly like a plug-in that is still broken.
    /// </para>
    /// <para>
    /// Answered with what Rhino says afterwards rather than with the call's own result, because
    /// <c>LoadPlugInResult</c> collapses every failure to ErrorUnknown: the state of the record is more
    /// use than a word that means "no".
    /// </para>
    /// </remarks>
    internal static string Load(string payload)
    {
        using JsonDocument request = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(payload) ? "{}" : payload);

        string? asked = Field(request, "id");
        string? path = Field(request, "path");

        if (asked is null && path is null)
        {
            throw new ArgumentException(
                "load needs 'id' (a plug-in Rhino already has a record of) or 'path' (an .rhp on disk). "
                + "GET /plugins lists the records with their ids.");
        }

        return Ui.On(() =>
        {
            Guid id;
            string how;

            if (path is not null)
            {
                if (!File.Exists(path))
                {
                    throw new KeyNotFoundException($"There is no file at {path}.");
                }

                LoadPlugInResult result = PlugIn.LoadPlugIn(path, out id);
                how = result.ToString();
            }
            else
            {
                id = Guid.Parse(asked!);

                // An empty guid names nothing, and Rhino's own index says otherwise: PlugInExists answers
                // true for it, so every check below waves it through and the report comes back about
                // whichever plug-in the manager had to hand. Refused here because it is the one id that
                // cannot be meant - it is what a caller sends when it thought it had an id and did not.
                if (id == Guid.Empty)
                {
                    throw new ArgumentException(
                        "An all-zero guid is not a plug-in id. GET /plugins lists the ids Rhino has.");
                }

                // Refused before loading rather than after, because Rhino answers a load for an id it has
                // never heard of the same way it answers one that failed - and GetPlugInInfo hands back
                // somebody else's record for an id it does not know, so the report that followed described
                // a plug-in the caller had not asked about. Measured with an all-zero guid, which came back
                // as this plugin, loaded and healthy.
                if (Record(id) is null)
                {
                    throw new KeyNotFoundException(
                        $"Rhino has no record of a plug-in with the id {id}, so there is nothing to load. "
                        + "GET /plugins lists the records it does have; pass 'path' for an .rhp it has "
                        + "never seen.");
                }

                // Quietly, and again even if it failed before - see the remarks; this is the whole verb.
                how = PlugIn.LoadPlugIn(id, loadQuietly: true, forceLoad: true) ? "Accepted" : "Refused";
            }

            StringBuilder json = new("{\"ok\":true,\"result\":");
            json.Append(Json.Quote(how));

            if (Record(id) is { } info)
            {
                json.Append(",\"loaded\":").Append(info.IsLoaded ? "true" : "false");
                json.Append(",\"name\":").Append(Json.Quote(info.Name ?? ""));
                json.Append(",\"version\":").Append(Json.Quote(info.Version ?? ""));
                json.Append(",\"path\":").Append(Json.Quote(info.FileName ?? ""));

                // Said when it matters: a record that is not loaded after being told to load is the
                // interesting case, and the reason is nearly always one of these two.
                if (!info.IsLoaded)
                {
                    json.Append(",\"dotnet\":").Append(info.IsDotNet ? "true" : "false");
                    json.Append(",\"runtime\":").Append(Json.Quote(
                        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription));
                }
            }
            else
            {
                json.Append(",\"loaded\":false,\"note\":")
                    .Append(Json.Quote("Rhino has no record of that plug-in, so nothing was loaded."));
            }

            return json.Append('}').ToString();
        });
    }

    /// <summary>
    /// Rhino's record for that id, or null when it has none.
    /// </summary>
    /// <remarks>
    /// <c>GetPlugInInfo</c> cannot be asked this. For an id Rhino has never heard of it hands back a record
    /// rather than null, and that record is a chimera: its id is the one you asked for, so comparing them
    /// proves nothing, while its name, version and path come from somewhere else entirely. Measured with an
    /// all-zero guid, which answered as this plugin, loaded and healthy - a report about a plug-in nobody
    /// had asked about, and the reason this check exists.
    /// <para>
    /// <c>PlugInExists</c> is the honest test: it looks the id up in the manager's index and says no when
    /// the index says no.
    /// </para>
    /// </remarks>
    private static PlugInInfo? Record(Guid id) =>
        PlugIn.PlugInExists(id, out _, out _) ? PlugIn.GetPlugInInfo(id) : null;

    private static string? Field(JsonDocument request, string name) =>
        request.RootElement.TryGetProperty(name, out JsonElement field)
            && field.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(field.GetString())
                ? field.GetString()
                : null;
}
