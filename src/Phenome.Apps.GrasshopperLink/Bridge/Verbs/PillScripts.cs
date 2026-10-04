using System.Reflection;
using System.Text.Json;

using Grasshopper.Kernel;

using static Phenome.Apps.GrasshopperLink.Bridge.Verbs.Plumbing;

namespace Phenome.Apps.GrasshopperLink.Bridge.Verbs;

/// <summary>PillScript components, driven through PillScript itself in the same Rhino.</summary>
/// <remarks>
/// PillScript is a separate plug-in with its own C# script component. Its sources live in a project folder, its
/// inputs come from the <c>RunScript</c> signature, and the <c>script</c> verb, which writes the source of
/// Rhino's own script components, cannot reach it. PillScript has its own tools for this: list the components,
/// read and write their files, change references, compile, solve. Until this verb they were reachable only over
/// a second port that PillScript opens when <c>PILLSCRIPT_BRIDGE</c> is set before Rhino starts, through a
/// second MCP server.
/// <para>
/// The two plug-ins share a process, and this verb calls PillScript's <c>PillScript.Bridge.Entry.Run(string,
/// string)</c> directly. Only strings cross, so the link carries no reference to PillScript and finds the
/// method by name. The tool names and their arguments are PillScript's, and a tool PillScript adds later works
/// here with no change to the link.
/// </para>
/// </remarks>
internal static class PillScripts
{
    /// <summary>Tools that read and change nothing: no autosave first, no modified flag, no journal entry.</summary>
    private static readonly HashSet<string> Reads =
        ["list_components", "list_files", "read_file", "list_references", "solve", "open_editor"];

    private const string Releases = "https://github.com/theObjectCo/PillScript/releases/latest";

    internal static string Run(JsonDocument request)
    {
        string author = Author(request);
        string tool = Field(request, "tool")
            ?? throw new ArgumentException("pillscript needs 'tool', for example list_components.");

        string arguments = Arguments(request);
        MethodInfo entry = Locate();
        bool changes = !Reads.Contains(tool);

        string answer = OnUi(() =>
        {
            GH_Document? document = ActiveDocument();

            if (changes && document is not null)
            {
                EnsureAutosave(document);
            }

            string result = Invoke(entry, tool, arguments);

            // The script's files are kept in the component, and the component in the document. A file written
            // here is lost with an unsaved document, the same as any other edit.
            if (changes && document is not null)
            {
                Changed(document);
            }

            return result;
        });

        if (changes)
        {
            Journal.Append(author, "pillscript", $",\"tool\":{Json.Quote(tool)}");
        }

        return answer;
    }

    /// <summary>The tool's arguments as JSON text: an object, or a string holding one, as some clients send.</summary>
    private static string Arguments(JsonDocument request)
    {
        if (!request.RootElement.TryGetProperty("arguments", out JsonElement given))
        {
            return "{}";
        }

        return given.ValueKind switch
        {
            JsonValueKind.Object => given.GetRawText(),
            JsonValueKind.Null => "{}",
            JsonValueKind.String => given.GetString() is { Length: > 0 } text ? text : "{}",
            _ => throw new ArgumentException("pillscript takes 'arguments' as an object, such as {\"component\":\"7ef9\"}."),
        };
    }

    /// <summary>PillScript's entry point, or a refusal that says what is missing and how to get it.</summary>
    /// <remarks>
    /// The refusal is read by an agent, which then has to tell a user what to do. It names the file, the command
    /// and the restart, because PillScript is not on the Rhino package server and the Package Manager does not
    /// find it by name. It also says that the user installs it: a plug-in that runs code in Rhino is not
    /// installed by an agent on its own.
    /// </remarks>
    private static MethodInfo Locate()
    {
        Assembly? pill = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => assembly.GetName().Name == "PillScript");

        if (pill is null)
        {
            Version rhino = Rhino.RhinoApp.Version;

            // PillScript 0.5.0 is built for Rhino 8.30. Installing it into an older Rhino would fail in a way
            // that names neither, so the version is checked here and named first.
            string tooOld = rhino.Major == 8 && rhino.Minor < 30
                ? $" This Rhino is {rhino.Major}.{rhino.Minor}, and PillScript needs 8.30 or newer, so Rhino has to be updated first."
                : "";

            throw new InvalidOperationException(
                "PillScript is not loaded in this Rhino, which usually means it is not installed, and the pillscript "
                + "tool works only through it. "
                + "PillScript is a separate plug-in for Rhino 8.30 or newer on Windows." + tooOld + " "
                + Install("To install it")
                + " Ask the user before installing it.");
        }

        return pill.GetType("PillScript.Bridge.Entry")
                ?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(string)])
            ?? throw new InvalidOperationException(
                $"PillScript {pill.GetName().Version?.ToString(3)} is installed in this Rhino, and the pillscript tool "
                + "needs 0.5.0 or newer. " + Install("To update it"));
    }

    /// <summary>The installation steps from PillScript's release page, as one paragraph.</summary>
    private static string Install(string purpose) =>
        $"{purpose}, download the .yak file from {Releases} into a folder of its own, run "
        + "\"C:\\Program Files\\Rhino 8\\System\\Yak.exe\" install --source <that folder> pillscript, "
        + "and restart Rhino. Rhino loads a plug-in only when it starts.";

    private static string Invoke(MethodInfo entry, string tool, string arguments)
    {
        try
        {
            return entry.Invoke(null, [tool, arguments]) as string ?? "{}";
        }
        catch (TargetInvocationException thrown) when (thrown.InnerException is { } inner)
        {
            // PillScript explains its own refusals. The wrapper only says that reflection was involved.
            throw new InvalidOperationException(inner.Message, inner);
        }
    }
}
