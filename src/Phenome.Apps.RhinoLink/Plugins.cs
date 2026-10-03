using System.Text;
using System.Text.Json;

using Rhino.PlugIns;

namespace Phenome.Apps.RhinoLink;

/// <summary>
/// Reports on the plug-ins Rhino has records for, and loads one explicitly.
/// </summary>
/// <remarks>
/// This lives in the Rhino half on purpose. Plug-in development happens with no definition open and no reason
/// to start Grasshopper, and the question "did the plug-in load" should not require a canvas. This plugin also
/// loads with Rhino, before Grasshopper exists, and is available when a plug-in fails at startup, while the
/// canvas half is not yet there to answer.
/// <para>
/// The reported fields are the facts needed to diagnose a plug-in that installs but does not load, with
/// nothing in any log: whether Rhino has a record of it at all, the path Rhino has recorded, whether it is
/// managed, whether it is load protected, and its registry key. All of them are in Rhino's own record and not
/// reachable any other way; without this report they take manual checks such as <c>reg query</c>.
/// </para>
/// </remarks>
internal static class Plugins
{
/// <summary>
/// Lists every plug-in Rhino has a record of, with the runtime it would load into.
/// </summary>
/// <remarks>
/// The runtime is reported first. It decides whether an assembly can load, and it is the hardest fact to
/// determine from outside. Rhino 8 hosts two CLRs (a .NET Framework executable and a .NET Core half), and
/// "Rhino 8 is .NET 8" is true in one mode and false in the other. A plug-in must match whichever is running.
/// The runtime is a mode that can differ between runs, and the report states it instead of assuming it.
/// <para>
/// Shipped plug-ins are omitted unless requested. There are about a hundred, they are rarely the cause, and
/// they bury the relevant entries in a long list.
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

            // The four fields that explain why a plug-in is not loading, beside the one that says it is not.
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
/// Loads a plug-in explicitly: no dialog, and no refusal due to an earlier failed attempt.
/// </summary>
/// <remarks>
/// Both flags serve the build-and-load loop. <c>loadQuietly</c> handles the confirmation a load-protected
/// plug-in raises. Every installed plug-in is load protected, and that dialog is the normal case. Disabling
/// the prompt globally is different and wrong: with <c>AskOnLoadProtection</c> false, Rhino does not load a
/// protected plug-in at all and gives no indication. That silence and the dialog are two settings of one
/// switch, and this verb needs neither.
/// <para>
/// <c>forceLoad</c> covers the other half. Rhino does not retry a load that previously failed. Without the
/// flag the normal loop of fix, rebuild and load does nothing on the second pass, which looks like a plug-in
/// that is still broken.
/// </para>
/// <para>
/// The result reports Rhino's record state afterwards rather than the call's return value, because
/// <c>LoadPlugInResult</c> collapses every failure to ErrorUnknown.
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

                // An empty guid names nothing, but PlugInExists answers true for it. The checks below would
                // pass it, and the report would describe whichever plug-in the manager had cached. It is
                // rejected here: it is the one id that cannot be intended, and a caller sends it when it
                // believed it had an id and did not.
                if (id == Guid.Empty)
                {
                    throw new ArgumentException(
                        "An all-zero guid is not a plug-in id. GET /plugins lists the ids Rhino has.");
                }

                // Checked before loading. Rhino answers an unknown-id load the same way it answers a failed
                // one, and GetPlugInInfo returns another plug-in's record for an unknown id. A report after the
                // load would describe a plug-in that was not requested. An all-zero guid confirmed this by
                // coming back as this plugin, loaded and healthy.
                if (Record(id) is null)
                {
                    throw new KeyNotFoundException(
                        $"Rhino has no record of a plug-in with the id {id}; there is nothing to load. "
                        + "GET /plugins lists the records it does have; pass 'path' for an .rhp it has "
                        + "never seen.");
                }

                // Loads without its dialog, forcing a retry even after a prior failure - see the remarks.
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

                // Reported only when the plug-in is still not loaded after the load request, where the
                // cause is nearly always one of these two.
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
                    .Append(Json.Quote("Rhino has no record of that plug-in. Nothing was loaded."));
            }

            return json.Append('}').ToString();
        });
    }

/// <summary>
/// Rhino's record for that id, or null when there is none.
/// </summary>
/// <remarks>
/// <c>GetPlugInInfo</c> cannot answer this. For an unknown id it returns a record instead of null, and the
/// record is inconsistent. Its id matches the one requested, which leaves a comparison of ids proving nothing,
/// and its name, version and path come from a different plug-in. An all-zero guid confirmed this by returning
/// this plugin, loaded and healthy.
/// <para>
/// <c>PlugInExists</c> is the reliable check: it looks the id up in the manager's index and returns false
/// when the index has no entry.
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
