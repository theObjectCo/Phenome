using System.Net;
using System.Text;
using System.Text.Json;

using Grasshopper.Kernel;

using Phenome.Apps.GrasshopperLink.Definition;

using static Phenome.Apps.GrasshopperLink.Bridge.Verbs.Plumbing;

namespace Phenome.Apps.GrasshopperLink.Bridge.Verbs;

/// <summary>Verbs that treat Rhino as a process, not as a document.</summary>
/// <remarks>
/// They write to its command line, answer the dialog holding it, cancel what it waits on and record what went
/// wrong. Reporting is a verb here because a report is a request like any other; the friction log itself lives
/// in the namespace above.
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
    /// Answers the dialog Rhino is waiting on, journalled like any other action.
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

    /// <summary>Answers the open dialog; nothing is assumed when no action is given.</summary>
    internal static string AnswerDialog(JsonDocument request)
    {
        string author = Author(request);
        string? button = Field(request, "button");
        string? expect = Field(request, "expect");
        string? key = Field(request, "key");

        bool close = request.RootElement.TryGetProperty("close", out JsonElement asked) && AsBool(asked);

        string answer = Pulse.Answer(button, key, close, expect);

        Journal.Append(author, "dialog", $",\"answer\":{Json.Quote(key ?? button ?? (close ? "close" : "none"))}");

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

        // Also journalled: a user watching sees the complaint when it is made and does not have to find it in
        // a file later.
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
