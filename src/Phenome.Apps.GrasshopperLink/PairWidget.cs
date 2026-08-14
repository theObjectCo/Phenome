using System.Drawing;
using System.Drawing.Drawing2D;

using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Widgets;

namespace Phenome.Apps.GrasshopperLink;

/// <summary>
/// The button that starts the pairing: shown while nobody is on the line, one click opens VS Code with the
/// port in hand.
/// </summary>
/// <remarks>
/// The click launches a URI - <c>vscode://phenome.phenome/pair?port=N</c> by default - and the editor's
/// URI handler does the rest: wakes the window, starts an agent session with the handshake typed in. The
/// template is a Grasshopper setting (<c>PhenomeLink:PairUri</c>), so a different editor with a different
/// agent slots in by changing one string, not this code. The button hides itself as soon as the link has
/// heard from anyone recently - a paired canvas needs no invitation.
/// </remarks>
public sealed class PairWidget : GH_Widget
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(20);

    private Rectangle bounds = Rectangle.Empty;
    private bool visible = true;

    /// <inheritdoc/>
    public override string Name => "Phenome Link";

    /// <inheritdoc/>
    public override string Description => "Pair this canvas with an agent in VS Code.";

    /// <inheritdoc/>
    public override bool Visible
    {
        get => visible;
        set => visible = value;
    }

    /// <inheritdoc/>
    public override Bitmap Icon_24x24 => Icon();

    /// <inheritdoc/>
    public override string TooltipText =>
        "Open VS Code and start an agent session paired with this canvas.";

    /// <inheritdoc/>
    public override bool Contains(Point pt_control, PointF pt_canvas) => bounds.Contains(pt_control);

    /// <inheritdoc/>
    public override void Render(GH_Canvas Canvas)
    {
        if (DateTime.Now - LinkServer.LastRequest < Quiet)
        {
            // Someone is on the line; the invitation would be noise.
            bounds = Rectangle.Empty;
            return;
        }

        string text = "Pair with VS Code";

        using Font font = new(FontFamily.GenericSansSerif, 9f, FontStyle.Regular);

        SizeF size = Canvas.Graphics.MeasureString(text, font);

        bounds = new Rectangle(
            Canvas.ClientRectangle.Left + 12,
            Canvas.ClientRectangle.Bottom - (int)size.Height - 16,
            (int)size.Width + 16,
            (int)size.Height + 8);

        using GraphicsPath path = Rounded(bounds, 4);
        using SolidBrush back = new(Color.FromArgb(210, 30, 30, 32));
        using Pen edge = new(Color.FromArgb(160, 120, 200, 150));
        using SolidBrush ink = new(Color.FromArgb(235, 220, 240, 228));
        using StringFormat centred = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        using Matrix kept = Canvas.Graphics.Transform;
        SmoothingMode smoothing = Canvas.Graphics.SmoothingMode;

        Canvas.Graphics.ResetTransform();
        Canvas.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        Canvas.Graphics.FillPath(back, path);
        Canvas.Graphics.DrawPath(edge, path);
        Canvas.Graphics.DrawString(text, font, ink, bounds, centred);

        Canvas.Graphics.SmoothingMode = smoothing;
        Canvas.Graphics.Transform = kept;
    }

    private static bool extensionOffered;

    /// <inheritdoc/>
    public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
    {
        if (bounds.IsEmpty || !bounds.Contains(e.ControlLocation))
        {
            return GH_ObjectResponse.Ignore;
        }

        EnsureExtension();

        string template = global::Grasshopper.Instances.Settings.GetValue(
            "PhenomeLink:PairUri",
            "vscode://phenome.phenome-link/pair?port={0}");

        string uri = string.Format(template, LinkServer.Port);

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            Rhino.RhinoApp.WriteLine(
                $"Phenome Link: nothing answered {uri} - install VS Code with the Phenome extension, " +
                "or point the PhenomeLink:PairUri setting at your editor.");
        }

        return GH_ObjectResponse.Handled;
    }

    /// <summary>
    /// The other half of "install one thing": a .vsix shipped inside the yak package, beside this .gha,
    /// is handed to VS Code before the first pairing. Yak has no install hooks - deliberately - but the
    /// pair button is code, and one click is the whole install. Once per session, best effort, silent
    /// when there is nothing to do.
    /// </summary>
    private static void EnsureExtension()
    {
        if (extensionOffered)
        {
            return;
        }

        extensionOffered = true;

        try
        {
            string? beside = Path.GetDirectoryName(typeof(PairWidget).Assembly.Location);

            string? vsix = beside is null
                ? null
                : Directory.EnumerateFiles(beside, "phenome-link-*.vsix").OrderByDescending(f => f).FirstOrDefault();

            if (vsix is null)
            {
                return;
            }

            // 'code' is a .cmd shim, so it goes through the shell; a missing VS Code fails quietly here
            // and loudly two lines later, when the vscode:// launch has nobody to answer it.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd",
                Arguments = $"/c code --install-extension \"{vsix}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            })?.WaitForExit(20_000);
        }
        catch (Exception)
        {
            // Best effort by design: the URI handler may already be installed, and the launch below
            // gives its own instruction when it is not.
        }
    }

    private static Bitmap? icon;

    private static Bitmap Icon()
    {
        if (icon is not null)
        {
            return icon;
        }

        Bitmap bitmap = new(24, 24);

        using Graphics canvas = Graphics.FromImage(bitmap);
        canvas.SmoothingMode = SmoothingMode.AntiAlias;
        canvas.Clear(Color.Transparent);

        using Pen wire = new(Color.FromArgb(230, 40, 40, 44), 2f);

        canvas.DrawLine(wire, 4, 12, 20, 12);
        canvas.DrawEllipse(wire, 2, 9, 6, 6);
        canvas.DrawEllipse(wire, 16, 9, 6, 6);

        return icon = bitmap;
    }

    private static GraphicsPath Rounded(Rectangle box, int radius)
    {
        GraphicsPath path = new();
        int d = radius * 2;

        path.AddArc(box.X, box.Y, d, d, 180, 90);
        path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
        path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
        path.AddArc(box.X, box.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }
}
