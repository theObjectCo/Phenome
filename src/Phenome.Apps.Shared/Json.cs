using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Phenome.Apps;

/// <summary>The two things every hand-built JSON needs: a safe string and an invariant number.</summary>
/// <remarks>
/// Built by hand rather than with a serializer for the same reason the transcriber does it: the shapes here
/// are small, assembled from live Grasshopper objects that no serializer should be pointed at, and the
/// protocol is a contract - what goes on the wire is exactly what is written here, not what a library
/// decides an object graph looks like this version.
/// <para>
/// In <c>Phenome.Apps</c> rather than in either plugin's own namespace, which is the whole trick: it is the
/// parent of both, so every call site in both halves reads <c>Json.Quote</c> unchanged and no file needs an
/// import. See the README beside this file.
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

    /// <summary>Re-laid with indentation, for the reader who is a person pasting into a prompt.</summary>
    internal static string Indented(string json)
    {
        using JsonDocument parsed = JsonDocument.Parse(json);

        return JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// A string field, or null when it is absent or is not a string.
    /// </summary>
    /// <remarks>
    /// Silent about the difference between missing and wrongly typed, deliberately: every caller of this
    /// treats an unusable field the same way, by falling back to what it would have done without one. A
    /// verb that needs the field to be there says so itself, in its own words, which are better words than
    /// anything a reader could produce.
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
