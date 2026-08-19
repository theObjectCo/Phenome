using System.Drawing;
using System.Drawing.Drawing2D;

using Grasshopper.GUI.Canvas;

using Rhino.Display;

using Phenome.Apps.GrasshopperLink.Bridge;

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

    /// <summary>
    /// Two pixels, solid.
    /// </summary>
    /// <remarks>
    /// A glow was the first idea and it kept failing at the only thing it had to do. Soft enough to look
    /// good and it went unseen; strong enough to be seen and it was a painted frame with blurred edges,
    /// which is a worse version of a line. Three rounds of tuning opacity were three rounds of asking a
    /// gradient to behave like a border.
    /// <para>
    /// A border, then. Thin enough to take no room and to sit outside the drawing, definite enough that
    /// there is nothing to squint at, and the same on a white viewport as on a grey one - which the
    /// gradient never managed, because a gradient's visibility depends on what is under it.
    /// </para>
    /// </remarks>
    private const int Thickness = 2;

    private static Conduit? conduit;
    private static System.Timers.Timer? clock;
    private static bool lit;

    /// <summary>The canvas already being painted, so a second subscription is not added to it.</summary>
    private static GH_Canvas? painted;

    internal static void Start()
    {
        Rhino.RhinoApp.InvokeOnUiThread(() =>
        {
            conduit = new Conduit { Enabled = true };

            // Both, because neither alone is enough. This runs as the plugin loads, and at that moment
            // there may be no canvas yet - Grasshopper builds it when its window first opens, which is
            // usually after. Subscribing only to the event misses a canvas that already exists; only
            // attaching now misses every canvas made later, which was the whole of it: the border worked
            // in Rhino and never once appeared on the canvas, because the handler had been hung on null.
            Attach(Grasshopper.Instances.ActiveCanvas);
            Grasshopper.Instances.CanvasCreated += Attach;

            // Polled rather than driven by the requests themselves: the border has to go out when nothing
            // happens, and "nothing happens" raises no event. Twice a second is under the threshold at
            // which a light looks like it is flickering, and costs nothing while dark.
            clock = new System.Timers.Timer(500) { AutoReset = true };
            clock.Elapsed += (_, _) => Tick();
            clock.Start();
        });
    }

    /// <summary>Hangs the border on a canvas, once per canvas.</summary>
    private static void Attach(GH_Canvas? canvas)
    {
        if (canvas is null || ReferenceEquals(canvas, painted)) return;

        canvas.CanvasPostPaintOverlay += PaintCanvas;
        painted = canvas;
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

    private static void PaintCanvas(GH_Canvas canvas)
    {
        if (!Busy) return;

        Graphics? graphics = canvas.Graphics;
        if (graphics is null) return;

        Rectangle frame = canvas.ClientRectangle;
        if (frame.Width <= Thickness * 2 || frame.Height <= Thickness * 2) return;

        // The overlay stage still carries the canvas's own transform - it is where objects are drawn, in
        // document coordinates. A border belongs to the window, not to the document: left as it was, it
        // scrolled and scaled with the definition, which is the one thing a frame must never do.
        GraphicsState state = graphics.Save();
        graphics.ResetTransform();

        // Inset by half the pen, so the whole line lands inside the control instead of half of it being
        // clipped away by the edge it is drawn on.
        using Pen pen = new(Glow, Thickness);
        graphics.DrawRectangle(
            pen,
            frame.Left + Thickness / 2,
            frame.Top + Thickness / 2,
            frame.Width - Thickness,
            frame.Height - Thickness);

        graphics.Restore(state);
    }

    /// <summary>The same border in every Rhino viewport, drawn over the scene rather than in it.</summary>
    private sealed class Conduit : DisplayConduit
    {
        protected override void DrawForeground(DrawEventArgs e)
        {
            if (!Busy) return;

            var bounds = e.Viewport.Bounds;
            if (bounds.Width <= Thickness * 2 || bounds.Height <= Thickness * 2) return;

            Rectangle frame = new(
                Thickness / 2,
                Thickness / 2,
                bounds.Width - Thickness,
                bounds.Height - Thickness);

            e.Display.Draw2dRectangle(frame, Glow, Thickness, Color.Transparent);
        }
    }
}
