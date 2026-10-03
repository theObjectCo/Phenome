using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Phenome.Apps;

/// <summary>
/// The two primitives every hand-built JSON response needs: a safely quoted string and an invariant number.
/// </summary>
/// <remarks>
/// JSON is built by hand, without a serializer. The shapes are small and are assembled from live Grasshopper
/// objects that a serializer should not walk. The protocol is also a contract: what goes on the wire is exactly
/// what is written here and does not depend on how a library version serializes an object graph.
/// <para>
/// It sits in <c>Phenome.Apps</c>, the parent of both plugin namespaces (see the README beside this file).
/// Every call site in both halves reads <c>Json.Quote</c> with no import.
/// </para>
/// </remarks>
internal static class Json
{
    internal static string Quote(string value)
    {
        StringBuilder quoted = new(value.Length + 2);

        quoted.Append('"');

        foreach (char letter in value)
        {
            switch (letter)
            {
                case '"':
                    quoted.Append("\\\"");
                    break;
                case '\\':
                    quoted.Append("\\\\");
                    break;
                case '\n':
                    quoted.Append("\\n");
                    break;
                case '\r':
                    quoted.Append("\\r");
                    break;
                case '\t':
                    quoted.Append("\\t");
                    break;
                default:
                    if (letter < ' ')
                    {
                        quoted.Append("\\u").Append(((int)letter).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        quoted.Append(letter);
                    }

                    break;
            }
        }

        return quoted.Append('"').ToString();
    }

    internal static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Re-indents JSON for human readers.</summary>
    internal static string Indented(string json)
    {
        using JsonDocument parsed = JsonDocument.Parse(json);

        return JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// A string field, or null when it is absent or is not a string.
    /// </summary>
    /// <remarks>
    /// Missing and wrongly typed are reported the same way (as null), deliberately: every caller falls back
    /// to what it would have done without the field. A verb that requires the field reports that itself, in
    /// its own message.
    /// </remarks>
    internal static string? Text(JsonElement request, string name) =>
        request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty(name, out JsonElement field)
            && field.ValueKind == JsonValueKind.String
                ? field.GetString()
                : null;

    /// <summary>As above, from the root of a parsed request.</summary>
    internal static string? Text(JsonDocument request, string name) => Text(request.RootElement, name);

    /// <summary>An integer field, or <paramref name="fallback"/> when it is absent or is not a number.</summary>
    internal static int Int(JsonElement request, string name, int fallback) =>
        request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty(name, out JsonElement field)
            && field.ValueKind == JsonValueKind.Number
            && field.TryGetInt32(out int value)
                ? value
                : fallback;

    /// <summary>As above, from the root of a parsed request.</summary>
    internal static int Int(JsonDocument request, string name, int fallback) =>
        Int(request.RootElement, name, fallback);
}
