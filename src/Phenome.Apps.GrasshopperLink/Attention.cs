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
    /// The house teal, #4ab3a2 - a colour neither Rhino nor Grasshopper uses for anything of its own, so
    /// nothing else on the screen can be mistaken for it or it for anything else.
    /// </summary>
    private static readonly Color Glow = Color.FromArgb(0x4A, 0xB3, 0xA2);

    /// <summary>How far the glow reaches inwards, in pixels.</summary>
    private const int Reach = 24;

    /// <summary>How solid the border is at the very edge, out of 255.</summary>
    private const int Strength = 220;

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

    /// <summary>Whether an agent has touched this Rhino recently enough to still be here.</summary>
    private static bool Busy => DateTime.Now - LinkServer.LastRequest < Hold;

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
    /// The falloff is squared rather than straight. A linear ramp spreads the colour so evenly that it
    /// reads as a faint wash and is easy to miss entirely; squaring keeps the outermost pixels close to
    /// solid, so there is a definite edge, and lets the rest fade quickly enough to still be a glow.
    /// The first two rings are drawn at full strength so the border has a line, not only a haze.
    /// </remarks>
    private static int AlphaAt(int step)
    {
        if (step < 2) return Strength;

        double falling = 1.0 - (double)step / Reach;
        return Math.Max(0, (int)(Strength * falling * falling));
    }

    private static void PaintCanvas(GH_Canvas canvas)
    {
        if (!Busy) return;

        Graphics? graphics = canvas.Graphics;
        if (graphics is null) return;

        Rectangle frame = canvas.ClientRectangle;
        if (frame.Width <= Reach * 2 || frame.Height <= Reach * 2) return;

        SmoothingMode was = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        for (int step = 0; step < Reach; step++)
        {
            using Pen pen = new(Color.FromArgb(AlphaAt(step), Glow));
            graphics.DrawRectangle(
                pen,
                frame.Left + step,
                frame.Top + step,
                frame.Width - 1 - step * 2,
                frame.Height - 1 - step * 2);
        }

        graphics.SmoothingMode = was;
    }

    /// <summary>The same border in every Rhino viewport, drawn over the scene rather than in it.</summary>
    private sealed class Conduit : DisplayConduit
    {
        protected override void DrawForeground(DrawEventArgs e)
        {
            if (!Busy) return;

            var bounds = e.Viewport.Bounds;
            if (bounds.Width <= Reach * 2 || bounds.Height <= Reach * 2) return;

            for (int step = 0; step < Reach; step++)
            {
                Rectangle frame = new(
                    step,
                    step,
                    bounds.Width - 1 - step * 2,
                    bounds.Height - 1 - step * 2);

                e.Display.Draw2dRectangle(frame, Color.FromArgb(AlphaAt(step), Glow), 1, Color.Transparent);
            }
        }
    }
}
