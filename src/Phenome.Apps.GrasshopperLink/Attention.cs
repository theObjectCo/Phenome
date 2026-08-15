using System.Drawing;
using System.Drawing.Drawing2D;

using Grasshopper.GUI.Canvas;

using Rhino.Display;

namespace Phenome.Apps.GrasshopperLink;

/// <summary>
/// Shows, on the screen itself, that something other than the person is driving.
/// </summary>
/// <remarks>
/// An agent working through this link moves the same canvas and the same viewports a human does, and
/// from across the room the two are indistinguishable: geometry appears, sliders move, the view jumps.
/// Whose hands did it is not a detail - it decides whether the person reaches for the mouse or waits,
/// and whether a surprise is a bug or somebody else's next step.
/// <para>
/// So while requests are arriving, both surfaces get a soft border lit from the inside: every Rhino
/// viewport and the Grasshopper canvas. It goes out on its own a few seconds after the last request, so
/// nobody has to turn it off, and an idle screen is never wearing it.
/// </para>
/// <para>
/// Drawn rather than announced. A dialog would have to be dismissed, a message in the command line
/// scrolls away, and both ask for attention the person may not want to give; a border is seen without
/// being read.
/// </para>
/// </remarks>
internal static class Attention
{
    /// <summary>
    /// How long after a request the border stays lit - long enough to bridge the gaps between calls.
    /// </summary>
    /// <remarks>
    /// Agents work in bursts with thinking in between, and a border that goes out during the thinking
    /// says the opposite of the truth: it says the machine is yours again, seconds before it is not.
    /// </remarks>
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Object Orange, #ff9800.
    /// </summary>
    /// <remarks>
    /// The house teal was the obvious choice and the wrong one twice over. It is the primary brand
    /// colour, which makes it the colour of things being normal, and this is not that. And it is close
    /// enough in value to Rhino's grey viewport background - and far too close on a white one - that it
    /// washed out exactly where it most needed to be read.
    /// <para>
    /// The house palette files orange under critical calls to action and warnings, which is the right
    /// register for "someone other than you is holding this machine", and it separates from both a grey
    /// and a white background without being alarming.
    /// </para>
    /// </remarks>
    private static readonly Color Glow = Color.FromArgb(0xFF, 0x98, 0x00);

    /// <summary>How far the glow reaches inwards, in pixels.</summary>
    private const int Reach = 40;

    /// <summary>
    /// How strong the glow is at the very edge, out of 255.
    /// </summary>
    /// <remarks>
    /// Half of what it took to make teal visible. The first number was chosen to fight a hue that was
    /// losing against the background, and once the hue was right it stopped being a fight: at that
    /// strength orange stopped reading as a glow and became a painted frame, which draws the eye and
    /// then keeps it. This is meant to be noticed at a glance and then ignored.
    /// </remarks>
    private const int Strength = 118;

    /// <summary>Corner radius of the lit frame, in pixels.</summary>
    private const int Radius = 26;

    private static Conduit? conduit;
    private static System.Timers.Timer? clock;
    private static bool lit;

    internal static void Start()
    {
        Rhino.RhinoApp.InvokeOnUiThread(() =>
        {
            conduit = new Conduit { Enabled = true };

            if (Grasshopper.Instances.ActiveCanvas is GH_Canvas canvas)
            {
                canvas.CanvasPostPaintOverlay += PaintCanvas;
            }

            // Polled rather than driven by the requests themselves: the border has to go out when nothing
            // happens, and "nothing happens" raises no event. Twice a second is under the threshold at
            // which a light looks like it is flickering, and costs nothing while dark.
            clock = new System.Timers.Timer(500) { AutoReset = true };
            clock.Elapsed += (_, _) => Tick();
            clock.Start();
        });
    }

    internal static void Stop()
    {
        clock?.Stop();
        clock?.Dispose();
        clock = null;

        if (conduit is not null) conduit.Enabled = false;
        conduit = null;
    }

    /// <summary>
    /// Whether an agent is working, not merely attached.
    /// </summary>
    /// <remarks>
    /// LastAction rather than LastRequest: a paired client polls the journal every couple of seconds
    /// whether or not it is doing anything, so a border keyed to requests would be lit for as long as
    /// anybody was connected - which is a light that means "somebody is in the building", and nobody
    /// needs telling that twice a second for an afternoon.
    /// </remarks>
    private static bool Busy => DateTime.Now - LinkServer.LastAction < Hold;

    private static void Tick()
    {
        bool now = Busy;
        if (now == lit) return;

        lit = now;

        // Only on the change: redrawing every half second whether or not anything altered would put a
        // constant load on a machine whose whole job is elsewhere.
        Rhino.RhinoApp.InvokeOnUiThread(() =>
        {
            Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
            Grasshopper.Instances.ActiveCanvas?.Refresh();
        });
    }

    /// <summary>
    /// Nested rectangles of falling opacity, from the edge inwards - a glow without a bitmap or a shader.
    /// </summary>
    /// <remarks>
    /// No solid ring at the edge. An inner glow is light coming from the border inwards, and the moment
    /// the outermost pixels are opaque it stops being that and becomes a stroke with a blur behind it -
    /// which is what the first attempt was, and it looked like a selection highlight rather than a
    /// presence.
    /// <para>
    /// So the strongest ring is already below solid, and the falloff is smoothstep: no flat plateau at
    /// the edge and no abrupt end inland, both of which the eye finds and reads as a line.
    /// </para>
    /// </remarks>
    private static int AlphaAt(int step)
    {
        double t = (double)step / Reach;
        if (t >= 1.0) return 0;

        double falling = 1.0 - t;
        double smooth = falling * falling * (3.0 - 2.0 * falling);

        return Math.Max(0, (int)(Strength * smooth));
    }

    private static void PaintCanvas(GH_Canvas canvas)
    {
        if (!Busy) return;

        Graphics? graphics = canvas.Graphics;
        if (graphics is null) return;

        Rectangle frame = canvas.ClientRectangle;
        if (frame.Width <= Reach * 2 || frame.Height <= Reach * 2) return;

        // The overlay stage still carries the canvas's own transform - it is where objects are drawn, in
        // document coordinates. A border belongs to the window, not to the document: left as it was, it
        // scrolled and scaled with the definition, which is the one thing a frame must never do.
        GraphicsState state = graphics.Save();
        graphics.ResetTransform();

        SmoothingMode was = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        for (int step = 0; step < Reach; step++)
        {
            int alpha = AlphaAt(step);
            if (alpha <= 0) continue;

            using Pen pen = new(Color.FromArgb(alpha, Glow));
            using GraphicsPath ring = Rounded(
                new Rectangle(
                    frame.Left + step,
                    frame.Top + step,
                    frame.Width - 1 - step * 2,
                    frame.Height - 1 - step * 2),
                Math.Max(2, Radius - step));

            graphics.DrawPath(pen, ring);
        }

        graphics.SmoothingMode = was;
        graphics.Restore(state);
    }

    /// <summary>A rectangle with its corners taken off - the shape the glow follows.</summary>
    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        GraphicsPath path = new();

        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }

    /// <summary>The same border in every Rhino viewport, drawn over the scene rather than in it.</summary>
    private sealed class Conduit : DisplayConduit
    {
        protected override void DrawForeground(DrawEventArgs e)
        {
            if (!Busy) return;

            var bounds = e.Viewport.Bounds;
            if (bounds.Width <= Reach * 2 || bounds.Height <= Reach * 2) return;

            // Square corners here, unlike the canvas: the viewport pipeline draws rectangles, not paths,
            // and a rounded frame would have to be assembled from line segments for a difference nobody
            // would notice at this opacity.
            for (int step = 0; step < Reach; step++)
            {
                int alpha = AlphaAt(step);
                if (alpha <= 0) continue;

                Rectangle frame = new(
                    step,
                    step,
                    bounds.Width - 1 - step * 2,
                    bounds.Height - 1 - step * 2);

                e.Display.Draw2dRectangle(frame, Color.FromArgb(alpha, Glow), 1, Color.Transparent);
            }
        }
    }
}
