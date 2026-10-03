using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Phenome.Apps;

/// <summary>
/// Reports whether Rhino is idle, busy or blocked, computed without touching the UI thread.
/// </summary>
/// <remarks>
/// All other verbs run on the UI thread. When it is blocked they fail with the same message whatever the
/// cause. Two causes need opposite handling: a running command will finish (wait), and a modal dialog will
/// not (it must be answered). Telling them apart keeps an agent from abandoning work that was about to finish
/// and from waiting on a process that can never move.
/// <para>
/// This uses three signals a worker thread can read on its own. An idle handler stamps the time whenever the
/// UI thread is free, and a stale stamp means "not free". The command events record the running command name
/// as they fire. On Windows a modal disables its owner window: a disabled main window plus an enabled owned
/// window is the dialog.
/// </para>
/// <para>
/// A stale stamp with a command running is busy. A stale stamp with a dialog up is blocked.
/// </para>
/// <para>
/// A single copy is compiled into both halves of the link. The README beside this file describes how the
/// earlier duplicated copies drifted apart, among them <c>key</c> on
/// <see cref="Dismiss(string?, string?, string?)"/> and <c>clickable</c> in <see cref="Report"/>.
/// </para>
/// <para>
/// Dialog detection is Win32-only and disabled on macOS, where a dialog reads as busy and answering one is
/// refused. The idle stamp and command events still work there. The macOS path is untested.
/// </para>
/// </remarks>
internal static class Pulse
{
    private static DateTime lastIdle = DateTime.MinValue;
    private static string? runningCommand;
    private static DateTime commandStarted = DateTime.MinValue;

    /// <summary>
    /// Rhino's main window handle, recorded the first time the process is healthy enough to have one.
    /// </summary>
    /// <remarks>
    /// Modal detection asks which window the modal disabled. Querying <see cref="Process.MainWindowHandle"/> at
    /// the moment of the check is wrong during shutdown. Closing Rhino destroys the main frame *before*
    /// Grasshopper's multi-save prompt is answered, and <c>MainWindowHandle</c> then returns that prompt (a
    /// visible, enabled window). "Is the main window disabled" returns false, and the state is reported as
    /// "busy" while a dialog with a Close button is in fact holding the exit.
    /// <para>
    /// The handle is recorded once from the idle handler (the first idle proves Rhino is up and the frame
    /// exists) and never refreshed. That keeps the shutdown case correct: a destroyed frame stays destroyed
    /// here even once <c>MainWindowHandle</c> starts pointing elsewhere.
    /// </para>
    /// </remarks>
    private static IntPtr frame;

    /// <summary>
    /// Idle-stamp age below which the UI thread counts as free. Rhino raises Idle many times a second when it
    /// is idle, and 200 ms leaves a wide margin. The threshold only needs to be shorter than a noticeable pause.
    /// </summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromMilliseconds(200);

    internal static void Start()
    {
        // Subscribed from the UI thread: these events are raised there, and Rhino's handler lists are meant
        // to be changed there.
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

        // Three states, each calling for a different agent action. Idle is free. Busy means an answer is
        // coming and the agent waits. Blocked means nothing is coming until the dialog is answered.
        string verdict = free ? "idle" : dialog.Present ? "blocked" : "busy";

        StringBuilder json = new();
        json.Append("{\"ok\":true");
        json.Append(",\"state\":").Append(Json.Quote(verdict));
        json.Append(",\"uiFree\":").Append(free ? "true" : "false");

        // Reported so "busy" on macOS is not mistaken for proof that no dialog is open.
        if (!Readable)
        {
            json.Append(",\"dialogsReadable\":false");
        }

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

            // Listing the answer options here saves a round trip between choosing and pressing.
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

            // An empty button list does not mean the dialog has no buttons. It means the dialog draws its own
            // and cannot be clicked; the answer is a key.
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
            return "The Rhino UI thread did not answer in time, though it looks free. Try again.";
        }

        Dialog dialog = ModalDialog();
        if (dialog.Present)
        {
            return string.IsNullOrEmpty(dialog.Title)
                ? "Rhino is waiting on a dialog; it holds the UI thread until it is answered by an agent or the user."
                : $"Rhino is waiting on the dialog \"{dialog.Title}\"; it holds the UI thread until it is answered by an agent or the user.";
        }

        string? command = runningCommand;
        if (command is not null)
        {
            long seconds = (long)(DateTime.Now - commandStarted).TotalSeconds;
            return $"Rhino is busy running {command} ({seconds}s so far). It is working; ask /pulse instead of giving up.";
        }

        // A script running between two commands holds the UI thread with neither a named command nor a
        // dialog present; treat it like a command (wait). Only a thread that looks free is unexpected here.
        return "Rhino is busy: the UI thread is working and no dialog is open. Wait and ask /pulse again.";
    }

    private static string Advice(string verdict, string? command, Dialog dialog) => verdict switch
    {
        "idle" => "Rhino is free.",
        "blocked" => string.IsNullOrEmpty(dialog.Title)
            ? "A dialog is open. It blocks the UI thread until an agent answers it or the user clicks it."
            : $"The dialog \"{dialog.Title}\" is open. It blocks the UI thread until an agent answers it or the user clicks it.",
        _ => (command is null
            ? "The UI thread is working on something unnamed. Wait and ask again."
            : $"{command} is running. Wait and ask again.")
            + (Readable ? "" : " Dialogs cannot be read on this system. If this lasts, ask the user to look."),
    };

    /// <summary>Whether open dialogs can be found and answered here: on Windows only, so far.</summary>
    private static bool Readable => OperatingSystem.IsWindows();

    private static InvalidOperationException Unreadable() => new(
        "Answering a dialog works on Windows only so far. Ask the user to answer it.");

    private readonly record struct Dialog(bool Present, string? Title, IntPtr Handle = default);

    /// <summary>
    /// Answers an open dialog: presses a button on it by name, or closes it when no name is given.
    /// </summary>
    /// <remarks>
    /// The dialog handle found for diagnosis is enough to answer the dialog. PostMessage puts the click on that
    /// window's queue from this worker thread and needs nothing from the blocked thread.
    /// <para>
    /// <paramref name="expect"/> guards against a race: between reading which dialog is open and answering
    /// it, the user may have answered it and another dialog may have appeared. With the expected title named,
    /// that race ends in a refusal.
    /// </para>
    /// <para>
    /// The default is to close. Closing does what the window's X does and declines; pressing a button agrees
    /// to something and must be stated explicitly.
    /// </para>
    /// </remarks>
    internal static string Dismiss(string? button, string? expect) => Dismiss(button, expect, null);

    /// <summary>
    /// Answers the open dialog, and refuses to guess which answer was meant.
    /// </summary>
    /// <remarks>
    /// This replaces <c>dismiss</c>, whose name describes only one of three outcomes (press, type, close).
    /// Agents read "agree to it" as "dismiss with OK" and sometimes declined the confirmation they meant to
    /// accept. <c>dismiss</c> also closes when sent nothing, and close means decline: an omitted decline cannot
    /// be told from a deliberate one.
    /// <para>
    /// Here an omitted action does nothing: with no action given this refuses and lists the dialog's options.
    /// Declining takes an explicit <c>close</c>, the same way agreeing takes an explicit button.
    /// </para>
    /// <para>
    /// No code here guesses which button is affirmative. On a save prompt the affirmative is whichever of Save
    /// and Don't Save the caller intended, and guessing wrong writes or discards a file. <c>pulse</c> lists the
    /// buttons; the caller names one.
    /// </para>
    /// </remarks>
    internal static string Answer(string? button, string? key, bool close, string? expect) =>
        Act(button, key, close, expect);

    /// <summary>
    /// As above, with a key for dialogs that cannot be clicked.
    /// </summary>
    /// <remarks>
    /// Rhino's newer dialogs are Eto, not Win32, and draw their own buttons. There is no child window to post
    /// a click to, and the button list comes back empty, which is the signal that only a key will work.
    /// WM_CLOSE is no substitute: on a "save changes?" prompt closing means cancel, and the intended action
    /// does not happen.
    /// </remarks>
    internal static string Dismiss(string? button, string? expect, string? key) =>
        Act(
            button,
            key,
            // Nothing given means close. The default stays for existing callers that rely on "send nothing to
            // decline"; changing it would silently give them a different action.
            close: string.IsNullOrEmpty(button) && string.IsNullOrEmpty(key),
            expect);

    private static string Act(string? button, string? key, bool close, string? expect)
    {
        if (!Readable)
        {
            throw Unreadable();
        }

        Dialog dialog = ModalDialog();

        if (!dialog.Present || dialog.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("No dialog is open.");
        }

        if (!string.IsNullOrEmpty(expect) &&
            !string.Equals(dialog.Title ?? "", expect, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The open dialog is \"{dialog.Title}\", not \"{expect}\": it changed after it was read. Nothing was pressed.");
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
            if (close)
            {
                PostMessage(dialog.Handle, WmClose, IntPtr.Zero, IntPtr.Zero);
                return $"{{\"ok\":true,\"dialog\":{Json.Quote(dialog.Title ?? "")},\"did\":\"closed\"}}";
            }

            // Nothing was asked for and nothing is done. Buttons are listed (not only described) so the next
            // call can name one without a separate query.
            string choices = string.Join(", ", ButtonsOf(dialog.Handle).Select(b => b.Text).Where(t => t.Length > 0));

            throw new InvalidOperationException(
                $"The dialog \"{dialog.Title}\" was left alone: no answer was given. "
                + (choices.Length == 0
                    ? "It draws its own buttons. Send 'key': the underlined letter of the intended answer, or \"{ESC}\"."
                    : $"It offers: {choices}. Send 'button' to press one, 'key' to type, or close:true to decline."));
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
                ? $"The dialog \"{dialog.Title}\" has no buttons this can click. It draws its own, and there is nothing to post a click to. Send 'key' instead: the underlined letter of the intended answer, or \"{{ESC}}\"."
                : $"The dialog \"{dialog.Title}\" has no button called \"{button}\". It offers: {offered}.");
    }

    /// <summary>
    /// Cancels whatever Rhino is waiting for by posting Escape.
    /// </summary>
    /// <remarks>
    /// Covers the case <see cref="Dismiss(string?, string?, string?)"/> cannot. A command waiting on a pick is
    /// not a dialog: nothing is disabled and no window can be enumerated, and <c>dismiss</c> correctly refuses.
    /// The UI thread stays held, and every other verb fails with "busy", which reads as "wait" though the wait
    /// never ends. This usually happens when scripting an interactive command, e.g. <c>-_Zoom</c> with an
    /// unrecognized magnification, which waits for a point a script never supplies.
    /// <para>
    /// The key is posted to the target window's message queue, the same way a dialog button is pressed. The
    /// queue can be written while the reading thread is busy, and nothing is asked of the blocked thread.
    /// </para>
    /// <para>
    /// Escape goes to the focused window. Rhino's getters read input from wherever focus is (a viewport or the
    /// command line), and a key posted to the frame is not always routed there. The main window is the
    /// fallback when focus cannot be read.
    /// </para>
    /// <para>
    /// <paramref name="times"/> is the number of Escape levels to cancel: a command with sub-options can be
    /// several deep, and the caller knows what it started. It is capped because a stream of Escapes into an
    /// idle Rhino can clear a selection the user wanted.
    /// </para>
    /// </remarks>
    internal static string Escape(int times)
    {
        if (!Readable)
        {
            throw Unreadable();
        }

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

        // No pulse follows. The key is queued and not yet delivered; a state read here would be the state
        // before the key was handled and could be misread as a failed Escape.
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
    /// Matched on a class name that <em>contains</em> "Button". A raw Win32 button's class is exactly
    /// <c>Button</c>, but frameworks that superclass it register their own name (WinForms buttons come back as
    /// <c>WindowsForms10.Button.app.0.&lt;hash&gt;</c>), and an equality test misses them all. Grasshopper's
    /// dialogs are WinForms. Under an equality test the multi-save prompt that blocks Rhino's exit reports
    /// <c>buttons: []</c> and <c>clickable: false</c> despite its normal <c>Close</c> button, and
    /// <c>dismiss</c> cannot answer the dialog agents most need to answer.
    /// <para>
    /// Checkboxes and radio buttons match too, since they superclass the same Win32 class. That is intended:
    /// they are labelled, clickable controls, and a caller naming one means to press it.
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
    /// Windows disables a modal's owner while it is open. A main window that cannot be clicked is the signal,
    /// read without polling Rhino's state and without the blocked thread. The dialog is then a visible, enabled
    /// window of this process that the main window owns or that has the dialog class (which is what a message
    /// box is), found in one pass. A titled window wins over an untitled one.
    /// <para>
    /// When the frame is <em>gone</em>, every visible enabled window of this process is a candidate: there is
    /// no frame left to own one. That is the shutdown case; <see cref="frame"/> explains why the handle is
    /// recorded once and not queried.
    /// </para>
    /// </remarks>
    private static Dialog ModalDialog()
    {
        if (!Readable)
        {
            return new Dialog(false, null);
        }

        try
        {
            using Process self = Process.GetCurrentProcess();

            // The recorded frame, or, before the first idle records one, whatever the OS reports as this
            // process's main window.
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

                    // With the frame destroyed, no window is owned by it and none is the frame. Any window
                    // still standing is the one holding the process.
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

                    // Keep looking only until a readable window is found: a titled window is worth naming,
                    // and an untitled one is a fallback.
                    return string.IsNullOrEmpty(title);
                },
                IntPtr.Zero);

            // With the frame gone and nothing visible left there is no dialog to name: the process is exiting,
            // and reporting "blocked" would mislead more than reporting nothing.
            return dialog == IntPtr.Zero && destroyed
                ? new Dialog(false, null)
                : new Dialog(true, title, dialog);
        }
        catch (Exception)
        {
            // On any failure this reports no dialog. A throw would lose the whole report, and the caller still
            // needs to know the thread is not free, whatever else is wrong.
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

    /// <summary>Whether a handle still names a live window; false once it has been destroyed.</summary>
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
