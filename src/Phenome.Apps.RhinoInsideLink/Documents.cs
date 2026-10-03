using System.Text;

using Rhino;
using Rhino.FileIO;

namespace Phenome.Apps.RhinoInsideLink;

/// <summary>
/// Reads, writes and converts documents in a Rhino that was never opened.
/// </summary>
/// <remarks>
/// Each document is opened headless, read or written, and disposed within one call. Nothing is kept between
/// requests. Unlike the other two links, there is no document being viewed: the file on disk is the state.
/// <para>
/// Every .3dm write suppresses dialogs, because a headless core with <c>WindowStyle.NoWindow</c> can still open a
/// window and show a modal. Without suppression, saving a document holding Rhino 8 data as file version 7 would
/// raise "the model contains information that cannot be saved in a Rhino 7 file" with three buttons and no user
/// to answer it; the write would return false and the server thread would block.
/// </para>
/// </remarks>
internal static class Documents
{
    /// <summary>Options for every write. Dialogs and input are suppressed, and nothing waits for a user.</summary>
    static FileWriteOptions Writing(int version) => new()
    {
        FileVersion = version,
        SuppressDialogBoxes = true,
        SuppressAllInput = true,

        // Keep the document's path as the caller gave it. A conversion must not change the source document's
        // path.
        UpdateDocumentPath = false,
    };

    /// <summary>What a document holds, as JSON.</summary>
    internal static string Describe(string path)
    {
        Exists(path);

        using RhinoDoc doc = RhinoDoc.OpenHeadless(path);

        StringBuilder json = new();
        json.Append("{\"ok\":true");
        json.Append(",\"path\":").Append(Json.Quote(Path.GetFullPath(path)));
        json.Append(",\"name\":").Append(Json.Quote(doc.Name ?? ""));
        json.Append(",\"units\":").Append(Json.Quote(doc.ModelUnitSystem.ToString()));
        json.Append(",\"tolerance\":").Append(Json.Number(doc.ModelAbsoluteTolerance));
        json.Append(",\"objects\":").Append(Json.Number(doc.Objects.Count));

        json.Append(",\"layers\":[");
        bool first = true;

        foreach (Rhino.DocObjects.Layer layer in doc.Layers)
        {
            if (!first) json.Append(',');
            first = false;
            json.Append("{\"name\":").Append(Json.Quote(layer.Name ?? ""));
            json.Append(",\"visible\":").Append(layer.IsVisible ? "true" : "false");
            json.Append(",\"locked\":").Append(layer.IsLocked ? "true" : "false").Append('}');
        }

        json.Append("],\"contents\":[");
        first = true;

        // Objects are counted by kind. Listed one by one, a document with forty thousand objects would return
        // forty thousand lines. The verb answers "what is in there", and naming everything is outside its scope.
        foreach (IGrouping<string, Rhino.DocObjects.RhinoObject> kind in doc.Objects
            .GroupBy(o => o.ObjectType.ToString())
            .OrderByDescending(g => g.Count()))
        {
            if (!first) json.Append(',');
            first = false;
            json.Append("{\"kind\":").Append(Json.Quote(kind.Key));
            json.Append(",\"count\":").Append(Json.Number(kind.Count()));
            json.Append(",\"named\":").Append(Json.Number(kind.Count(o => !string.IsNullOrWhiteSpace(o.Name)))).Append('}');
        }

        json.Append("]}");

        return json.ToString();
    }

    /// <summary>
    /// Reads one file and writes another, in the format the target's extension specifies.
    /// </summary>
    /// <remarks>
    /// The formats are Rhino's own: <c>.3dm</c> goes through the archive writer, and anything else through the
    /// exporter registered for that extension. Export was verified headless for <c>.stl</c>, <c>.obj</c>,
    /// <c>.dxf</c> and <c>.step</c>. The exporters are plugins and a Rhino with no window may not load plugins,
    /// which is why this was checked and is recorded here.
    /// </remarks>
    internal static string Convert(string from, string to, int version)
    {
        Exists(from);

        string target = Path.GetFullPath(to);
        string? folder = Path.GetDirectoryName(target);

        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        using RhinoDoc doc = RhinoDoc.OpenHeadless(from);

        bool asRhino = Path.GetExtension(target).Equals(".3dm", StringComparison.OrdinalIgnoreCase);

        bool written = asRhino
            ? doc.WriteFile(target, Writing(version))
            : doc.Export(target);

        long size = File.Exists(target) ? new FileInfo(target).Length : 0;

        // Rhino returns a bool and the disk returns a size, and they can disagree: some exporters return true
        // without writing anything. Both are returned, and a caller can tell the cases apart.
        if (!written && size == 0)
        {
            throw new IOException(
                $"Rhino did not write {target}, and no file was created. " +
                "An extension Rhino has no exporter for is the usual reason.");
        }

        // Rhino does not refuse an unrecognized extension: Export writes a Rhino file under whatever name it is
        // given and returns true. Converting to '.zzz' produces a valid .3dm named .zzz, reported as a success.
        // A caller that asked for one format and received another under that name finds out only when the file
        // is opened. The file's contents are checked here instead: a .3dm begins with a fixed banner, which is
        // cheap to read and needs no hand-maintained list of Rhino's formats.
        if (!asRhino && size > 0 && LooksLikeRhino(target))
        {
            File.Delete(target);

            throw new IOException(
                $"Rhino has no exporter for '{Path.GetExtension(target)}' - it wrote a Rhino file under that " +
                "name instead, and nothing was kept. Ask for an extension Rhino supports: .3dm, .stl, .obj, " +
                ".dxf, .step and the rest of its export list.");
        }

        StringBuilder json = new();
        json.Append("{\"ok\":true");
        json.Append(",\"from\":").Append(Json.Quote(Path.GetFullPath(from)));
        json.Append(",\"to\":").Append(Json.Quote(target));
        json.Append(",\"wrote\":").Append(written ? "true" : "false");
        json.Append(",\"bytes\":").Append(Json.Number(size));
        json.Append(",\"objects\":").Append(Json.Number(doc.Objects.Count));
        json.Append('}');

        return json.ToString();
    }

    /// <summary>Whether a file begins with the banner every .3dm begins with.</summary>
    /// <remarks>
    /// "3D Geometry File Format" is the first content in an openNURBS archive, unchanged since the format
    /// existed. It is read as bytes, which involves no guess at an encoding.
    /// </remarks>
    static bool LooksLikeRhino(string path)
    {
        ReadOnlySpan<byte> banner = "3D Geometry File Format"u8;

        try
        {
            using FileStream file = File.OpenRead(path);

            Span<byte> head = stackalloc byte[banner.Length];

            return file.Read(head) == banner.Length && head.SequenceEqual(banner);
        }
        catch (Exception)
        {
            // An unreadable file does not answer the question; keep whatever was written.
            return false;
        }
    }

    static void Exists(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"There is no file at {path}.", path);
        }
    }
}
