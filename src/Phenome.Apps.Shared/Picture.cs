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
    /// <para>
    /// A presentation picture needs more than the framing of all geometry, and agents wrote their own capture
    /// scripts for every picture: set the camera, hide the grid and axes, zoom to a box, change the display mode,
    /// crop the background. The query carries all of it now. <c>box</c> frames six numbers instead of everything.
    /// <c>direction</c> is where the camera looks, with <c>up</c> defaulting to Y when the view is nearly vertical
    /// and to Z otherwise. <c>parallel</c> chooses the projection; a perspective is taken with a 35 mm lens unless
    /// <c>lens</c> says otherwise. <c>displayMode</c> names a display mode, <c>grid</c> and <c>axes</c> hide them,
    /// and <c>trim</c> crops the plain background to a margin in percent of what remains.
    /// </para>
    /// <para>
    /// The camera, the projection, the grid and the axes are put back afterwards: the viewport is the user's. The
    /// display mode is never set on the viewport, only given to the capture.
    /// </para>
    /// </remarks>
    internal static string Viewport(HttpListenerRequest request, Func<Func<string>, string> onUi)
    {
        Asked asked = Read(request);
        Look look = Look.Read(request);

        return onUi(() =>
        {
            Rhino.RhinoDoc doc = Rhino.RhinoDoc.ActiveDoc
                ?? throw new InvalidOperationException("There is no Rhino document.");

            Rhino.Display.RhinoView view = doc.Views.ActiveView
                ?? throw new InvalidOperationException("There is no Rhino view to capture.");

            Rhino.Display.RhinoViewport viewport = view.ActiveViewport;

            Rhino.Display.DisplayModeDescription? mode = look.DisplayMode is { } name
                ? FindMode(name)
                : null;

            Size screen = view.ClientRectangle.Size;
            Size size = SizeFor(asked, (double)Math.Max(1, screen.Width) / Math.Max(1, screen.Height), 640);

            // Kept whole and put back afterwards: the picture should show the subject, but the camera is the
            // user's and must stay where the user left it. The target is kept separately, because restoring the
            // projection alone recomputes the target from the frustum, and the user's camera would come back
            // aimed somewhere new.
            Rhino.DocObjects.ViewportInfo kept = new(viewport);
            Rhino.Geometry.Point3d target = viewport.CameraTarget;
            bool moved = false;

            // The grid and the axes are switched off on the viewport for the capture and back on before anything
            // redraws, which is the only way to leave them out of a capture in another display mode.
            (bool Grid, bool World, bool Plane) shown =
                (viewport.ConstructionGridVisible, viewport.WorldAxesVisible, viewport.ConstructionAxesVisible);

            try
            {

                if (look.Parallel is { } parallel)
                {
                    // Changed before the camera is placed: switching projection rebuilds the frustum.
                    if (parallel)
                    {
                        viewport.ChangeToParallelProjection(symmetricFrustum: true);
                    }
                    else
                    {
                        viewport.ChangeToPerspectiveProjection(symmetricFrustum: true, lensLength: look.Lens);
                    }

                    moved = true;
                }
                else if (look.Direction is not null && !viewport.IsParallelProjection)
                {
                    viewport.Camera35mmLensLength = look.Lens;
                }

                Rhino.Geometry.BoundingBox subject = look.Box ?? Visible(doc);

                if (look.Direction is { } direction)
                {
                    Aim(viewport, direction, look.Up, subject);
                    moved = true;
                }

                if (look.Box is { } box)
                {
                    viewport.ZoomBoundingBox(box);
                    moved = true;
                }
                else if (look.Frame || look.Direction is not null || look.Parallel is not null)
                {
                    viewport.ZoomExtents();
                    moved = true;
                }

                // Without the agent-at-work border, which belongs on the screen and not in the picture.
                Bitmap? captured;

                viewport.ConstructionGridVisible = look.Grid ?? shown.Grid;
                viewport.WorldAxesVisible = look.Axes ?? shown.World;
                viewport.ConstructionAxesVisible = look.Axes ?? shown.Plane;

                using (Capture.Quiet())
                {
                    // A display mode is passed to the capture and not set on the viewport. Set on the viewport,
                    // it takes effect only at the next real repaint, and the capture came out in the mode the
                    // screen last showed.
                    captured = mode is null ? view.CaptureToBitmap(size) : view.CaptureToBitmap(size, mode);
                }

                using Bitmap bitmap = captured
                    ?? throw new InvalidOperationException("The viewport would not be captured.");

                if (look.Trim is { } margin)
                {
                    using Bitmap trimmed = Trim(bitmap, margin);

                    return Answer(trimmed, "viewport", asked);
                }

                return Answer(bitmap, "viewport", asked);
            }
            finally
            {
                viewport.ConstructionGridVisible = shown.Grid;
                viewport.WorldAxesVisible = shown.World;
                viewport.ConstructionAxesVisible = shown.Plane;

                if (moved)
                {
                    viewport.SetViewProjection(kept, updateTargetLocation: false);
                    viewport.SetCameraTarget(target, updateCameraLocation: false);
                }

                view.Redraw();
            }
        });
    }

    /// <summary>How the viewport is to be framed and drawn for one capture.</summary>
    private sealed record Look(
        bool Frame,
        Rhino.Geometry.BoundingBox? Box,
        Rhino.Geometry.Vector3d? Direction,
        Rhino.Geometry.Vector3d? Up,
        bool? Parallel,
        double Lens,
        string? DisplayMode,
        bool? Grid,
        bool? Axes,
        double? Trim)
    {
        internal static Look Read(HttpListenerRequest request)
        {
            double[]? box = Numbers(request, "box", 6);
            double[]? direction = Numbers(request, "direction", 3);
            double[]? up = Numbers(request, "up", 3);

            Rhino.Geometry.Vector3d? aim = direction is null ? null : new(direction[0], direction[1], direction[2]);

            if (aim is { IsZero: true })
            {
                throw new ArgumentException("'direction' has no length; it is where the camera looks, e.g. [0,0,-1].");
            }

            double lens = double.TryParse(request.QueryString["lens"], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double given) && given > 0 ? given : 35;

            double? trim = request.QueryString["trim"] is { Length: > 0 } text
                ? double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double percent) && percent >= 0
                        ? percent
                        : throw new ArgumentException("'trim' is the margin left around the subject, in percent: 0 or more.")
                : null;

            return new Look(
                Frame: !string.Equals(request.QueryString["zoomExtents"], "false", StringComparison.OrdinalIgnoreCase),
                Box: box is null
                    ? null
                    : new Rhino.Geometry.BoundingBox(
                        Math.Min(box[0], box[3]), Math.Min(box[1], box[4]), Math.Min(box[2], box[5]),
                        Math.Max(box[0], box[3]), Math.Max(box[1], box[4]), Math.Max(box[2], box[5])),
                Direction: aim,
                Up: up is null ? null : new(up[0], up[1], up[2]),
                Parallel: Flag(request, "parallel"),
                Lens: lens,
                DisplayMode: request.QueryString["displayMode"] is { Length: > 0 } named ? named : null,
                Grid: Flag(request, "grid"),
                Axes: Flag(request, "axes"),
                Trim: trim);
        }

        private static bool? Flag(HttpListenerRequest request, string name) =>
            request.QueryString[name] switch
            {
                null or "" => null,
                { } text when text.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
                { } text when text.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
                { } text => throw new ArgumentException($"'{name}' is true or false; got {text}."),
            };

        private static double[]? Numbers(HttpListenerRequest request, string name, int count)
        {
            if (request.QueryString[name] is not { Length: > 0 } text)
            {
                return null;
            }

            string[] parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            double[] values = new double[parts.Length];

            bool read = parts.Length == count && parts.Select((part, at) => double.TryParse(
                part,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out values[at])).All(ok => ok);

            return read ? values : throw new ArgumentException($"'{name}' takes {count} numbers; got {text}.");
        }
    }

    /// <summary>A display mode by its English or local name, or a refusal that lists the names there are.</summary>
    private static Rhino.Display.DisplayModeDescription FindMode(string name)
    {
        Rhino.Display.DisplayModeDescription[] modes = Rhino.Display.DisplayModeDescription.GetDisplayModes();

        return modes.FirstOrDefault(mode =>
                string.Equals(mode.EnglishName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode.LocalName, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"There is no display mode called '{name}'. There are: {string.Join(", ", modes.Select(mode => mode.EnglishName))}.");
    }

    /// <summary>The box around every visible object, or a unit box when there is none.</summary>
    private static Rhino.Geometry.BoundingBox Visible(Rhino.RhinoDoc doc)
    {
        Rhino.Geometry.BoundingBox all = Rhino.Geometry.BoundingBox.Empty;

        foreach (Rhino.DocObjects.RhinoObject thing in doc.Objects.GetObjectList(new Rhino.DocObjects.ObjectEnumeratorSettings
        {
            VisibleFilter = true,
            HiddenObjects = false,
        }))
        {
            all.Union(thing.Geometry.GetBoundingBox(accurate: false));
        }

        return all.IsValid ? all : new Rhino.Geometry.BoundingBox(-1, -1, -1, 1, 1, 1);
    }

    /// <summary>Points the camera along <paramref name="direction"/> at the middle of the subject.</summary>
    /// <remarks>
    /// Up defaults to Y when the camera looks nearly straight up or down, where Z would be parallel to the view
    /// and the camera would have no up at all, and to Z otherwise. The camera stands back two diagonals; the zoom
    /// that follows sets the framing, and the distance only has to keep the subject in front of the camera.
    /// </remarks>
    private static void Aim(
        Rhino.Display.RhinoViewport viewport,
        Rhino.Geometry.Vector3d direction,
        Rhino.Geometry.Vector3d? up,
        Rhino.Geometry.BoundingBox subject)
    {
        direction.Unitize();

        Rhino.Geometry.Vector3d upward = up is { IsZero: false } given
            ? given
            : Math.Abs(direction.Z) > 0.99 ? Rhino.Geometry.Vector3d.YAxis : Rhino.Geometry.Vector3d.ZAxis;

        Rhino.Geometry.Point3d middle = subject.Center;
        double reach = Math.Max(subject.Diagonal.Length, 1) * 2;

        viewport.SetCameraLocations(middle, middle - (direction * reach));
        viewport.CameraUp = upward;
    }

    /// <summary>
    /// Crops the plain background, leaving <paramref name="percent"/> of the subject's longer side around it.
    /// </summary>
    /// <remarks>
    /// The background is the colour of the top-left pixel, and a pixel counts as background within a few levels
    /// of it on every channel, which absorbs the antialiasing at the subject's edge. A gradient background is not
    /// plain and is left as it is. A picture with nothing on it comes back whole.
    /// </remarks>
    internal static Bitmap Trim(Bitmap picture, double percent)
    {
        const int Tolerance = 8;

        Rectangle all = new(0, 0, picture.Width, picture.Height);
        BitmapData data = picture.LockBits(all, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        int left = picture.Width, top = picture.Height, right = -1, bottom = -1;

        try
        {
            int[] row = new int[picture.Width];
            int background = System.Runtime.InteropServices.Marshal.ReadInt32(data.Scan0);

            for (int y = 0; y < picture.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, picture.Width);

                for (int x = 0; x < picture.Width; x++)
                {
                    if (!Near(row[x], background, Tolerance))
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }
        }
        finally
        {
            picture.UnlockBits(data);
        }

        if (right < 0)
        {
            return new Bitmap(picture);
        }

        int margin = (int)Math.Round(Math.Max(right - left + 1, bottom - top + 1) * percent / 100);

        Rectangle kept = Rectangle.Intersect(
            all,
            Rectangle.FromLTRB(left - margin, top - margin, right + 1 + margin, bottom + 1 + margin));

        return picture.Clone(kept, picture.PixelFormat);
    }

    private static bool Near(int a, int b, int tolerance) =>
        Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF)) <= tolerance
        && Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF)) <= tolerance
        && Math.Abs((a & 0xFF) - (b & 0xFF)) <= tolerance;

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
