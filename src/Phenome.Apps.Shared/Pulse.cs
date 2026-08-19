using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Phenome.Apps;

/// <summary>
/// Whether Rhino is idle, busy or stuck - answered without asking the UI thread anything.
/// </summary>
/// <remarks>
/// Every other verb here runs on the UI thread, so when that thread is not free they all fail the same
/// way, with the same sentence, whatever the reason. Two reasons hide behind it and they want opposite
/// responses: a long command is worth waiting for, and a modal dialog will wait forever. An agent that
/// cannot tell them apart either abandons work that was about to finish or sits watching a process that
/// will never move.
/// <para>
/// So this reads three things a worker thread can read on its own. An idle handler stamps the time
/// whenever the UI thread has nothing to do, which makes a stale stamp mean "not free". The command
/// events say what is running, cached as they fire rather than asked for on demand. And Windows itself
/// says whether a modal is up: it disables the owner window while one is open, so a main window that
/// cannot be clicked is the signal, and the owned window that can be is the dialog.
/// </para>
/// <para>
/// Stale stamp with a command running is "busy, wait". Stale stamp with a dialog up is "stuck, and here
/// is what it is asking".
/// </para>
/// <para>
/// One copy, compiled into both halves of the link. It was two, and they had drifted twice in the same
/// direction: <c>key</c> on <see cref="Dismiss(string?, string?, string?)"/> and <c>clickable</c> in
/// <see cref="Report"/> were each implemented on the canvas side and missing on the Rhino side, while both
/// halves' protocol text advertised them. See the README beside this file.
/// </para>
/// </remarks>
internal static class Pulse
{
    private static DateTime lastIdle = DateTime.MinValue;
    private static string? runningCommand;
    private static DateTime commandStarted = DateTime.MinValue;

    /// <summary>
    /// Rhino's own frame, remembered from the first moment the process was healthy enough to have one.
    /// </summary>
    /// <remarks>
    /// The diagnosis below turns on "which window is the owner a modal disabled", and asking the OS for it
    /// at the moment of the question is wrong at the one moment it matters most. Closing Rhino destroys the
    /// frame *before* Grasshopper's multi-save prompt is answered, and from then on
    /// <see cref="Process.MainWindowHandle"/> answers with that prompt - a visible, enabled window, so the
    /// check "is the main window disabled" says no and the whole thing reports "busy, working on something
    /// unnamed" while a dialog with a Close button sits there holding the exit. Met three times in one
    /// session, each time needing Win32 by hand to get out of.
    /// <para>
    /// Recorded once and never again, from the idle handler, because the first idle is the first proof that
    /// Rhino is up and the frame exists. Never refreshed, so a destroyed frame stays destroyed as far as
    /// this is concerned - which is the fact the shutdown case needs.
    /// </para>
    /// </remarks>
    private static IntPtr frame;

    /// <summary>
    /// Below this, a stamp is fresh enough to call the UI thread free. Rhino idles many times a second
    /// when nothing is happening, so a fifth of a second is generous; the number only has to be shorter
    /// than a human's idea of a pause.
    /// </summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromMilliseconds(200);

    internal static void Start()
    {
        // Subscribed from the UI thread, because that is where these events are raised and where Rhino
        // expects its handler lists to be touched.
        Rhino.RhinoApp.InvokeOnUiThread(() =>
        {
            Rhino.RhinoApp.Idle += (_, _) =>
            {
                lastIdle = DateTime.Now;

                if (frame == IntPtr.Zero)
                {
                    using Process self = Process.GetCurrentProcess();
                    frame = self.MainWindowHandle;
                }
            };

            Rhino.Commands.Command.BeginCommand += (_, e) =>
            {
                runningCommand = e.CommandEnglishName;
                commandStarted = DateTime.Now;
            };

            Rhino.Commands.Command.EndCommand += (_, _) =>
            {
                runningCommand = null;
                commandStarted = DateTime.MinValue;
            };
        });
    }

    /// <summary>The state as JSON, computed entirely off the UI thread.</summary>
    internal static string Report()
    {
        DateTime idleAt = lastIdle;
        TimeSpan since = idleAt == DateTime.MinValue ? TimeSpan.MaxValue : DateTime.Now - idleAt;
        bool free = since < Fresh;

        string? command = runningCommand;
        DateTime startedAt = commandStarted;

        Dialog dialog = ModalDialog();

        // The verdict is the whole point: three states an agent acts on differently. Free is free. Busy
        // means the answer is coming, so wait. Stuck means nothing is coming until somebody clicks.
        string verdict = free ? "idle" : dialog.Present ? "blocked" : "busy";

        StringBuilder json = new();
        json.Append("{\"ok\":true");
        json.Append(",\"state\":").Append(Json.Quote(verdict));
        json.Append(",\"uiFree\":").Append(free ? "true" : "false");

        if (since != TimeSpan.MaxValue)
        {
            json.Append(",\"idleAgoMs\":").Append(Json.Number((long)since.TotalMilliseconds));
        }

        if (command is not null)
        {
            json.Append(",\"command\":").Append(Json.Quote(command));
            json.Append(",\"commandForMs\":").Append(Json.Number((long)(DateTime.Now - startedAt).TotalMilliseconds));
        }

        json.Append(",\"dialog\":");
        if (dialog.Present)
        {
            json.Append("{\"present\":true,\"title\":").Append(Json.Quote(dialog.Title ?? ""));

            // What it can be answered with, so deciding and pressing are not two round trips.
            json.Append(",\"buttons\":[");
            string[] labels = dialog.Handle == IntPtr.Zero
                ? Array.Empty<string>()
                : ButtonsOf(dialog.Handle).Select(b => b.Text).Where(t => t.Length > 0).ToArray();

            for (int i = 0; i < labels.Length; i++)
            {
                if (i > 0)
                {
                    json.Append(',');
                }

                json.Append(Json.Quote(labels[i]));
            }

            json.Append(']');

            // Said outright, because an empty list of buttons is not the same as a dialog with none: it
            // means this one draws its own and cannot be clicked at all, and the answer is a key.
            json.Append(",\"clickable\":").Append(labels.Length > 0 ? "true" : "false");
            json.Append('}');
        }
        else
        {
            json.Append("{\"present\":false}");
        }

        json.Append(",\"advice\":").Append(Json.Quote(Advice(verdict, command, dialog)));
        json.Append('}');

        return json.ToString();
    }

    /// <summary>One sentence for the timeout message of any verb that could not get the UI thread.</summary>
    internal static string Sentence()
    {
        bool free = lastIdle != DateTime.MinValue && DateTime.Now - lastIdle < Fresh;
        if (free)
        {
            return "The Rhino UI thread did not answer in time, though it looks free - try again.";
        }

        Dialog dialog = ModalDialog();
        if (dialog.Present)
        {
            return string.IsNullOrEmpty(dialog.Title)
                ? "Rhino is waiting on a dialog; nothing will answer until somebody clicks it."
                : $"Rhino is waiting on the dialog \"{dialog.Title}\"; nothing will answer until somebody clicks it.";
        }

        string? command = runningCommand;
        if (command is not null)
        {
            long seconds = (long)(DateTime.Now - commandStarted).TotalSeconds;
            return $"Rhino is busy running {command} ({seconds}s so far). It is working - ask /pulse rather than giving up.";
        }

        // No command named and no dialog up still says something worth acting on: a script between two
        // commands holds the UI thread without either being true, and the answer is the same as for a
        // command - wait. Only a free-looking thread deserves to be called strange.
        return "Rhino is busy: the UI thread is working and no dialog is open. Wait and ask /pulse again.";
    }

    private static string Advice(string verdict, string? command, Dialog dialog) => verdict switch
    {
        "idle" => "Rhino is free.",
        "blocked" => string.IsNullOrEmpty(dialog.Title)
            ? "A dialog is open. Nothing will answer until somebody clicks it."
            : $"The dialog \"{dialog.Title}\" is open. Nothing will answer until somebody clicks it.",
        _ => command is null
            ? "The UI thread is working on something unnamed. Wait and ask again."
            : $"{command} is running. Wait and ask again.",
    };

    private readonly record struct Dialog(bool Present, string? Title, IntPtr Handle = default);

    /// <summary>
    /// Answers an open dialog: presses a button on it by name, or closes it when no name is given.
    /// </summary>
    /// <remarks>
    /// The handle is already found for the diagnosis, and a window that can be identified can be
    /// answered - the click is posted from this thread to that window's queue, which is what PostMessage
    /// is for and needs nothing from the thread that is stuck.
    /// <para>
    /// <paramref name="expect"/> exists because dialogs are transient: between reading which one is open
    /// and answering it, it may have been answered by the human and replaced by another asking something
    /// else entirely. Naming what you meant to answer turns that race into a refusal.
    /// </para>
    /// <para>
    /// Closing rather than pressing is the default deliberately. Closing a dialog is what the X does, and
    /// what the X does is decline; pressing a button is agreeing to something, and agreeing needs saying.
    /// </para>
    /// </remarks>
    internal static string Dismiss(string? button, string? expect) => Dismiss(button, expect, null);

    /// <summary>
    /// As above, and with a key for dialogs that cannot be clicked.
    /// </summary>
    /// <remarks>
    /// Not every dialog is Win32 all the way down. Rhino's newer ones are Eto: they draw their own
    /// buttons, so the buttons are not windows, there is nothing to post a click to, and the button list
    /// comes back empty - which is the signal that a key is the only way in. WM_CLOSE is not a
    /// substitute, because on a "save changes?" prompt closing means cancel, and cancel means the thing
    /// you were trying to do does not happen.
    /// </remarks>
    internal static string Dismiss(string? button, string? expect, string? key)
    {
        Dialog dialog = ModalDialog();

        if (!dialog.Present || dialog.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("No dialog is open.");
        }

        if (!string.IsNullOrEmpty(expect) &&
            !string.Equals(dialog.Title ?? "", expect, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The open dialog is \"{dialog.Title}\", not \"{expect}\" - it changed while you were deciding, so nothing was pressed.");
        }

        if (!string.IsNullOrEmpty(key))
        {
            SetForegroundWindow(dialog.Handle);

            foreach (char letter in key)
            {
                PostMessage(dialog.Handle, WmChar, (IntPtr)letter, IntPtr.Zero);
            }

            return $"{{\"ok\":true,\"dialog\":{Json.Quote(dialog.Title ?? "")},\"did\":\"typed\",\"key\":{Json.Quote(key)}}}";
        }

        if (string.IsNullOrEmpty(button))
        {
            PostMessage(dialog.Handle, WmClose, IntPtr.Zero, IntPtr.Zero);
            return $"{{\"ok\":true,\"dialog\":{Json.Quote(dialog.Title ?? "")},\"did\":\"closed\"}}";
        }

        List<(IntPtr Handle, string Text)> buttons = ButtonsOf(dialog.Handle);

        foreach ((IntPtr handle, string text) in buttons)
        {
            if (string.Equals(text, button, StringComparison.OrdinalIgnoreCase))
            {
                PostMessage(handle, BmClick, IntPtr.Zero, IntPtr.Zero);
                return $"{{\"ok\":true,\"dialog\":{Json.Quote(dialog.Title ?? "")},\"did\":\"pressed\",\"button\":{Json.Quote(text)}}}";
            }
        }

        string offered = string.Join(", ", buttons.Select(b => b.Text).Where(t => t.Length > 0));
        throw new InvalidOperationException(
            offered.Length == 0
                ? $"The dialog \"{dialog.Title}\" has no buttons this can click - it draws its own, so there is nothing to post a click to. Send 'key' instead: the underlined letter of the answer you want, or \"{{ESC}}\"."
                : $"The dialog \"{dialog.Title}\" has no button called \"{button}\". It offers: {offered}.");
    }

    /// <summary>
    /// Cancels whatever Rhino is waiting for, by posting Escape.
    /// </summary>
    /// <remarks>
    /// The gap <see cref="Dismiss(string?, string?, string?)"/> leaves. A command waiting for a pick is not
    /// a dialog: nothing is disabled, no window can be enumerated, and <c>dismiss</c> correctly refuses. But
    /// the UI thread is held all the same, so every other verb fails with a message about Rhino being busy --
    /// which reads as "wait" when the truth is "this will wait forever". Scripting an interactive command is
    /// the ordinary way to arrive here: <c>-_Zoom</c> with a magnification it does not recognise sits asking
    /// for a point that no script will ever supply.
    /// <para>
    /// Works for the same reason pressing a dialog's button works: the key is posted to the target window's
    /// own message queue, and a queue can be written to while the thread reading it is busy. Nothing here
    /// asks anything of the stuck thread.
    /// </para>
    /// <para>
    /// Aimed at the focused window rather than the main one, because Rhino's getters take their input
    /// wherever focus is -- a viewport, or the command line -- and a key posted to the frame is not always
    /// routed on. The main window is the fallback for when focus cannot be read.
    /// </para>
    /// <para>
    /// <paramref name="times"/> exists because one Escape cancels one level. A command with sub-options can
    /// be several deep, and the caller knows what they started better than this does. Capped, because a
    /// stream of Escapes into an idle Rhino is a way to clear a selection somebody wanted.
    /// </para>
    /// </remarks>
    internal static string Escape(int times)
    {
        times = Math.Clamp(times, 1, 5);

        using Process self = Process.GetCurrentProcess();
        IntPtr main = self.MainWindowHandle;

        if (main == IntPtr.Zero)
        {
            throw new InvalidOperationException("Rhino has no main window to post to.");
        }

        uint thread = GetWindowThreadProcessId(main, out _);
        IntPtr focus = FocusedWindow(thread);
        IntPtr target = focus != IntPtr.Zero ? focus : main;

        for (int i = 0; i < times; i++)
        {
            PostMessage(target, WmKeyDown, (IntPtr)VkEscape, IntPtr.Zero);
            PostMessage(target, WmKeyUp, (IntPtr)VkEscape, IntPtr.Zero);
        }

        // Deliberately not followed by a pulse: the key is queued, not delivered, so anything read here
        // would describe the state before it was handled and invite the caller to conclude it did not work.
        return "{\"ok\":true,\"posted\":" + Json.Number(times)
            + ",\"to\":" + Json.Quote(focus != IntPtr.Zero ? "the focused window" : "the main window")
            + ",\"next\":\"ask /pulse to see whether it took\"}";
    }

    /// <summary>The window holding keyboard focus on a given thread, or zero if it cannot be read.</summary>
    private static IntPtr FocusedWindow(uint thread)
    {
        GuiThreadInfo info = new() { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
    }

    /// <summary>
    /// Every push button on a dialog, with the ampersand Windows uses for accelerators removed.
    /// </summary>
    /// <remarks>
    /// Matched on the class name <em>containing</em> "Button" rather than being it. A raw Win32 button's class
    /// is exactly <c>Button</c>, but a framework that superclasses it registers its own name: WinForms buttons
    /// come back as <c>WindowsForms10.Button.app.0.&lt;hash&gt;</c>, and an equality test misses every one of
    /// them. That is not a corner case - Grasshopper's own dialogs are WinForms, so the multi-save prompt that
    /// holds Rhino's exit reported <c>buttons: []</c> and <c>clickable: false</c> while carrying a perfectly
    /// ordinary <c>Close</c> button, which left <c>dismiss</c> unable to answer the one dialog an agent most
    /// needs to answer.
    /// <para>
    /// A checkbox or radio button on a dialog will match too, since those superclass the same Win32 class. That
    /// is the right outcome rather than a false positive: they are labelled, clickable controls, and a caller
    /// naming one means to press it.
    /// </para>
    /// </remarks>
    private static List<(IntPtr Handle, string Text)> ButtonsOf(IntPtr dialog)
    {
        List<(IntPtr, string)> found = new();

        EnumChildWindows(
            dialog,
            (handle, unused) =>
            {
                bool isButton = ClassOf(handle).IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isButton && IsWindowVisible(handle) && IsWindowEnabled(handle))
                {
                    found.Add((handle, TitleOf(handle).Replace("&", "")));
                }

                return true;
            },
            IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// Whether a modal dialog is up, and what it says on it.
    /// </summary>
    /// <remarks>
    /// Windows disables a modal's owner for as long as it is open, so a main window that cannot be clicked
    /// is the signal - no polling of Rhino's own state, and nothing that needs the blocked thread. The
    /// dialog itself is then the visible, enabled, owned window belonging to this process; failing that,
    /// any visible window of the dialog class, which is what a message box is.
    /// <para>
    /// And when the frame is <em>gone</em> rather than disabled, every visible enabled window of this
    /// process is a candidate, because there is no longer a frame for one of them to be. That is the
    /// shutdown case: see <see cref="frame"/> for why it is the case that matters and why the handle is
    /// remembered rather than asked for.
    /// </para>
    /// </remarks>
    private static Dialog ModalDialog()
    {
        try
        {
            using Process self = Process.GetCurrentProcess();

            // The remembered frame, or - before the first idle has had a chance to record one - whatever the
            // OS calls this process's main window.
            bool remembered = frame != IntPtr.Zero;
            bool destroyed = remembered && !IsWindow(frame);
            IntPtr main = remembered ? frame : self.MainWindowHandle;

            if (!destroyed && (main == IntPtr.Zero || IsWindowEnabled(main)))
            {
                return new Dialog(false, null);
            }

            string? title = null;
            IntPtr dialog = IntPtr.Zero;

            EnumWindows(
                (handle, unused) =>
                {
                    if (!IsWindowVisible(handle) || !IsWindowEnabled(handle))
                    {
                        return true;
                    }

                    GetWindowThreadProcessId(handle, out uint owner);
                    if (owner != (uint)self.Id)
                    {
                        return true;
                    }

                    // With the frame destroyed there is nothing left to be owned by and nothing left that
                    // could be the frame, so any window still standing is the one holding the process.
                    if (!destroyed)
                    {
                        bool owned = GetWindow(handle, (IntPtr)GwOwner) == main;
                        if (!owned && ClassOf(handle) != DialogClass)
                        {
                            return true;
                        }
                    }

                    title = TitleOf(handle);
                    dialog = handle;

                    // Keep looking only while nothing readable has been found: a titled window is the one
                    // worth naming, and an untitled one is better than nothing.
                    return string.IsNullOrEmpty(title);
                },
                IntPtr.Zero);

            // With the frame gone and nothing visible left, there is no dialog to name - the process is on
            // its way out and saying "blocked" would be worse than saying nothing.
            return dialog == IntPtr.Zero && destroyed
                ? new Dialog(false, null)
                : new Dialog(true, title, dialog);
        }
        catch (Exception)
        {
            // A diagnostic that throws is worse than one that shrugs: whatever else is wrong, the caller
            // still wants to hear that the thread is not free.
            return new Dialog(false, null);
        }
    }

    private static string TitleOf(IntPtr handle)
    {
        StringBuilder text = new(512);
        int length = GetWindowText(handle, text, text.Capacity);
        return length > 0 ? text.ToString() : "";
    }

    private static string ClassOf(IntPtr handle)
    {
        StringBuilder name = new(256);
        int length = GetClassName(handle, name, name.Capacity);
        return length > 0 ? name.ToString() : "";
    }

    /// <summary>The window class Windows gives dialogs and message boxes.</summary>
    private const string DialogClass = "#32770";

    /// <summary>GW_OWNER: the window that owns this one, which for a modal is what it disabled.</summary>
    private const int GwOwner = 4;

    private const int WmClose = 0x0010;

    private const int WmChar = 0x0102;

    private const int BmClick = 0x00F5;

    private const int WmKeyDown = 0x0100;

    private const int WmKeyUp = 0x0101;

    private const int VkEscape = 0x1B;

    /// <summary>
    /// GUITHREADINFO, trimmed to the handles. The caret rectangle is four ints at the end that nothing here
    /// reads, but they have to be present or the size check inside the API rejects the call.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public int CaretLeft;
        public int CaretTop;
        public int CaretRight;
        public int CaretBottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr handle);

    /// <summary>Whether a handle still names a live window - false once it has been destroyed.</summary>
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr handle, IntPtr command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder name, int count);
}
