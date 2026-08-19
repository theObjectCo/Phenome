using System.Net;
using System.Text;
using System.Text.Json;

using Grasshopper.Kernel;

using Phenome.Apps.GrasshopperLink.Definition;

using static Phenome.Apps.GrasshopperLink.Bridge.Verbs.Plumbing;

namespace Phenome.Apps.GrasshopperLink.Bridge.Verbs;

/// <summary>Rhino as a process rather than as a document.</summary>
/// <remarks>
/// Saying something on its command line, answering the dialog that is holding it, cancelling what it is
/// waiting for, and writing down what went wrong. The last of those is here rather than in the friction
/// log because what a caller reports is a request like any other; the log itself is in the namespace
/// above.
/// </remarks>
internal static class Process
{
    internal static string Say(JsonDocument request)
    {
        string author = Author(request);
        string text = Field(request, "text") ?? throw new ArgumentException("say needs 'text'.");

        Journal.AppendMessage(author, text, Field(request, "to"));

        return "{\"ok\":true}";
    }

    /// <summary>
    /// Answers the dialog Rhino is waiting on. Journalled like any other hand on the machine.
    /// </summary>
    internal static string Dismissed(JsonDocument request)
    {
        string author = Author(request);
        string? button = Field(request, "button");
        string? expect = Field(request, "expect");
        string? key = Field(request, "key");

        string answer = Pulse.Dismiss(button, expect, key);

        Journal.Append(author, "dismiss", $",\"button\":{Json.Quote(key ?? button ?? "close")}");

        return answer;
    }

    internal static string Escaped(JsonDocument request)
    {
        string author = Author(request);

        int times = request.RootElement.TryGetProperty("times", out JsonElement field)
            && field.ValueKind == JsonValueKind.Number
                ? field.GetInt32()
                : 1;

        string answer = Pulse.Escape(times);

        Journal.Append(author, "escape", $",\"times\":{Json.Number(times)}");

        return answer;
    }

    internal static string Reported(JsonDocument request)
    {
        string author = Author(request);
        string expected = Field(request, "expected") ?? throw new ArgumentException("report needs 'expected'.");
        string got = Field(request, "got") ?? throw new ArgumentException("report needs 'got'.");

        Friction.Reported(author, expected, got, Field(request, "notes"));

        // Into the journal as well, so the human watching sees the complaint as it is made rather than
        // discovering it in a file later.
        Journal.Append(author, "report", $",\"expected\":{Json.Quote(expected)},\"got\":{Json.Quote(got)}");

        return $"{{\"ok\":true,\"log\":{Json.Quote(Friction.Path)}}}";
    }

    internal static string Feedback(JsonDocument request)
    {
        string author = Author(request);
        string expected = Field(request, "expected") ?? throw new ArgumentException("feedback needs 'expected'.");
        string got = Field(request, "got") ?? throw new ArgumentException("feedback needs 'got'.");

        (string session, string findings) = OnUi(() =>
        {
            GH_Document? document = ActiveDocument();

            string where = document is null
                ? "No Grasshopper document open."
                : $"Document '{document.DisplayName ?? "unsaved"}', {document.ObjectCount} objects, "
                    + $"solver {(GH_Document.EnableSolutions ? "on" : "locked")}.";

            return (where, Review.Whole(document));
        });

        (string path, string subject, _, string mailto) =
            Friction.Draft(expected, got, session, findings, Field(request, "to"));

        Journal.Append(author, "feedback", $",\"path\":{Json.Quote(path)}");

        return $"{{\"ok\":true,\"path\":{Json.Quote(path)},\"subject\":{Json.Quote(subject)},"
            + $"\"mailto\":{Json.Quote(mailto)},\"sent\":false}}";
    }
}
