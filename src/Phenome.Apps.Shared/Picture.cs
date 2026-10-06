using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;

namespace Phenome.Apps;

/// <summary>
/// The pictures the link takes for an agent: their size, the copy kept on disk, and the copy sent back.
/// </summary>
/// <remarks>
/// Both halves take pictures. The canvas half draws the canvas, and either half captures the viewport, the Rhino
/// half first. The size rules, the file and the answer are the same for all three, and they are kept here so that
/// the halves cannot drift on them.
/// <para>
/// A picture is drawn at the size asked for and kept on disk at that size. Pictures are used for documentation,
/// and a model reading one shrinks anything with a long edge above 1568 pixels before it looks. The answer can
/// therefore carry a smaller copy (<c>preview</c>), which costs less to send and loses nothing the model would
/// have seen, while the file keeps every pixel.
/// </para>
/// <para>
/// The headless server compiles the shared folder without this file: it has no view to draw.
/// </para>
/// </remarks>
internal static class Picture
{
    /// <summary>The longest side a picture may have, in pixels.</summary>
    private const int MaxSide = 8000;

    /// <summary>The most pixels one picture may have: 40 MP, about 160 MB of bitmap before it is encoded.</summary>
    private const long MaxPixels = 40_000_000;

    /// <summary>What the caller asked for.</summary>
    internal readonly record struct Asked(int? Width, int? Height, int? Preview, bool Save, string? Path);

    /// <summary>Reads width, height, preview, save and path from the query string.</summary>
    internal static Asked Read(HttpListenerRequest request)
    {
        static int? Number(string? text) => int.TryParse(text, out int value) && value > 0 ? value : null;

        string? path = request.QueryString["path"];

        if (!string.IsNullOrWhiteSpace(path)
            && !string.Equals(System.IO.Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'path' names the PNG file to write and has to end in .png; got {path}.");
        }

        return new Asked(
            Number(request.QueryString["width"]),
            Number(request.QueryString["height"]),
            Number(request.QueryString["preview"]),
            !string.Equals(request.QueryString["save"], "false", StringComparison.OrdinalIgnoreCase),
            string.IsNullOrWhiteSpace(path) ? null : path);
    }

    /// <summary>
    /// The size to draw: both sides as asked, or one side and the other from the subject's proportions.
    /// </summary>
    /// <param name="asked">The sides the caller gave, either, both or neither.</param>
    /// <param name="aspect">The subject's width over its height.</param>
    /// <param name="defaultWidth">The width used when neither side is asked for.</param>
    internal static Size SizeFor(Asked asked, double aspect, int defaultWidth)
    {
        aspect = double.IsFinite(aspect) && aspect > 0 ? aspect : 1;

        double width = asked.Width ?? (asked.Height is { } h ? h * aspect : defaultWidth);
        double height = asked.Height ?? width / aspect;

        // Too large on a side or in total: scaled down whole, so the proportions asked for survive.
        double shrink = Math.Min(1, Math.Min(MaxSide / Math.Max(width, height), Math.Sqrt(MaxPixels / (width * height))));

        return new Size(
            Math.Max(16, (int)Math.Round(width * shrink)),
            Math.Max(16, (int)Math.Round(height * shrink)));
    }

    /// <summary>
    /// Keeps the picture on disk unless told not to, and answers with it: the whole of it, or a copy no larger
    /// than <c>preview</c> on its long side.
    /// </summary>
    internal static string Answer(Bitmap picture, string kind, Asked asked)
    {
        string? kept = asked.Save ? Keep(picture, kind, asked.Path) : null;

        int longest = Math.Max(picture.Width, picture.Height);
        string png;
        Size sent = picture.Size;

        if (asked.Preview is { } limit && longest > limit)
        {
            double scale = (double)limit / longest;

            sent = new Size(Math.Max(1, (int)Math.Round(picture.Width * scale)), Math.Max(1, (int)Math.Round(picture.Height * scale)));

            using Bitmap smaller = new(sent.Width, sent.Height);

            using (Graphics paint = Graphics.FromImage(smaller))
            {
                paint.InterpolationMode = InterpolationMode.HighQualityBicubic;
                paint.DrawImage(picture, 0, 0, sent.Width, sent.Height);
            }

            png = Encode(smaller);
        }
        else
        {
            png = Encode(picture);
        }

        return "{\"ok\":true"
            + $",\"png\":{Json.Quote(png)}"
            + $",\"width\":{Json.Number(picture.Width)}"
            + $",\"height\":{Json.Number(picture.Height)}"
            + (sent != picture.Size ? $",\"sent\":{{\"width\":{Json.Number(sent.Width)},\"height\":{Json.Number(sent.Height)}}}" : "")
            + (kept is null ? "" : $",\"path\":{Json.Quote(kept)}")
            + "}";
    }

    /// <summary>
    /// Captures the active viewport at the size asked for, framed on the geometry unless told otherwise.
    /// </summary>
    /// <remarks>
    /// Both halves answer <c>/screenshot</c>, and this is the one body they share. <paramref name="onUi"/> is each
    /// half's own way onto Rhino's UI thread.
    /// </remarks>
    internal static string Viewport(HttpListenerRequest request, Func<Func<string>, string> onUi)
    {
        Asked asked = Read(request);
        bool frame = !string.Equals(request.QueryString["zoomExtents"], "false", StringComparison.OrdinalIgnoreCase);

        return onUi(() =>
        {
            Rhino.Display.RhinoView view = Rhino.RhinoDoc.ActiveDoc?.Views.ActiveView
                ?? throw new InvalidOperationException("There is no Rhino view to capture.");

            Size screen = view.ClientRectangle.Size;
            Size size = SizeFor(asked, (double)Math.Max(1, screen.Width) / Math.Max(1, screen.Height), 640);

            // Framed for the capture and restored afterward: the picture should show the geometry, but the
            // camera is the user's and must stay where the user left it.
            Rhino.DocObjects.ViewportInfo? kept = frame
                ? new Rhino.DocObjects.ViewportInfo(view.ActiveViewport)
                : null;

            // The target is saved separately. Restoring the projection alone recomputes the target from the
            // frustum, and the user's camera would come back aimed somewhere new.
            Rhino.Geometry.Point3d target = view.ActiveViewport.CameraTarget;

            if (frame)
            {
                view.ActiveViewport.ZoomExtents();
            }

            try
            {
                // Without the agent-at-work border, which belongs on the screen and not in the picture.
                Bitmap? captured;

                using (Capture.Quiet())
                {
                    captured = view.CaptureToBitmap(size);
                }

                using Bitmap bitmap = captured
                    ?? throw new InvalidOperationException("The viewport would not be captured.");

                return Answer(bitmap, "viewport", asked);
            }
            finally
            {
                if (kept is not null)
                {
                    view.ActiveViewport.SetViewProjection(kept, updateTargetLocation: false);
                    view.ActiveViewport.SetCameraTarget(target, updateCameraLocation: false);
                    view.Redraw();
                }
            }
        });
    }

    /// <summary>Writes the picture where asked, or into Pictures\Phenome Link under a name that sorts by time.</summary>
    private static string Keep(Bitmap picture, string kind, string? path)
    {
        string file = path is not null
            ? System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))
            : Fresh(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "Phenome Link",
                $"{DateTime.Now:yyyy-MM-dd HHmmss} {kind} {picture.Width}x{picture.Height}.png"));

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        picture.Save(file, ImageFormat.Png);

        return file;
    }

    /// <summary>The name itself, or with a counter when two pictures land in the same second.</summary>
    private static string Fresh(string file)
    {
        string stem = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(file)!,
            System.IO.Path.GetFileNameWithoutExtension(file));

        string candidate = file;

        for (int n = 2; File.Exists(candidate); n++)
        {
            candidate = $"{stem} ({n}).png";
        }

        return candidate;
    }

    private static string Encode(Bitmap picture)
    {
        using MemoryStream bytes = new();

        picture.Save(bytes, ImageFormat.Png);

        return Convert.ToBase64String(bytes.ToArray());
    }
}
