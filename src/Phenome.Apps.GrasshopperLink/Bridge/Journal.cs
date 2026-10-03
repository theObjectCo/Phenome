using System.Text;

namespace Phenome.Apps.GrasshopperLink.Bridge;

/// <summary>
/// Append-only record of canvas events, polled by each client from its own position.
/// </summary>
/// <remarks>
/// This is how an agent sees what the user is doing, and the other way round. There is no subscriber
/// list and no push: every entry gets a sequence number and a client asks for everything after the last
/// number it saw. A client that was not running reads it later, and ten clients cost the server exactly what
/// one does. Entries carry <c>author</c>: changes by the user and by different agents can be told apart, and a
/// client can skip its own echo.
/// </remarks>
internal static class Journal
{
    private const int Kept = 10_000;
    private const int MessagesKept = 100;

    private static readonly object Gate = new();
    private static readonly List<(long Seq, string Json)> Entries = [];
    private static readonly List<(string Author, string Text, string? To)> Messages = [];
    private static long next = 1;

    /// <summary>Raised after an entry is appended, with its kind. Handlers run on the appending thread and must marshal.</summary>
    internal static event Action<string>? Appended;

    /// <summary>Appends one entry. <paramref name="fields"/> is extra JSON, starting with a comma, or empty.</summary>
    internal static void Append(string author, string kind, string fields = "")
    {
        lock (Gate)
        {
            long seq = next++;

            Entries.Add((seq,
                $"{{\"seq\":{seq},\"at\":\"{DateTime.Now:HH:mm:ss}\"," +
                $"\"author\":{Json.Quote(author)},\"kind\":{Json.Quote(kind)}{fields}}}"));

            if (Entries.Count > Kept)
            {
                // The cap bounds memory and costs no correctness. A client further behind has missed entries,
                // the gap in sequence numbers reports that, and the client should then re-read /canvas.
                Entries.RemoveRange(0, Entries.Count - Kept);
            }
        }

        Appended?.Invoke(kind);
    }

    /// <summary>A message entry, stored twice: in the journal for clients, and as a readable list for the canvas component.</summary>
    internal static void AppendMessage(string author, string text, string? to)
    {
        lock (Gate)
        {
            Messages.Add((author, text, to));

            if (Messages.Count > MessagesKept)
            {
                Messages.RemoveRange(0, Messages.Count - MessagesKept);
            }
        }

        Append(author, "message",
            $",\"text\":{Json.Quote(text)}{(to is null ? "" : $",\"to\":{Json.Quote(to)}")}");
    }

    /// <summary>The recent messages, oldest first, for the canvas conversation view.</summary>
    internal static IReadOnlyList<(string Author, string Text, string? To)> RecentMessages()
    {
        lock (Gate)
        {
            return [.. Messages];
        }
    }

    /// <summary>Everything after <paramref name="since"/>, with the latest number to ask from next time.</summary>
    internal static string Since(long since)
    {
        lock (Gate)
        {
            StringBuilder json = new("{\"latest\":");

            json.Append(Json.Number(next - 1)).Append(",\"events\":[");

            bool first = true;

            foreach ((long seq, string entry) in Entries)
            {
                if (seq <= since)
                {
                    continue;
                }

                if (!first)
                {
                    json.Append(',');
                }

                first = false;
                json.Append(entry);
            }

            return json.Append("]}").ToString();
        }
    }
}
