using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Phenome.Apps.GrasshopperLink;

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
/// </remarks>
internal static class Pulse
{
    private static DateTime lastIdle = DateTime.MinValue;
    private static string? runningCommand;
    private static DateTime commandStarted = DateTime.MinValue;

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
            Rhino.RhinoApp.Idle += (_, _) => lastIdle = DateTime.Now;

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
            json.Append(",\"idleAgoMs\":").Append((long)since.TotalMilliseconds);
        }

        if (command is not null)
        {
            json.Append(",\"command\":").Append(Json.Quote(command));
            json.Append(",\"commandForMs\":").Append((long)(DateTime.Now - startedAt).TotalMilliseconds);
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

            json.Append("]}");
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
    internal static string Dismiss(string? button, string? expect)
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
                ? $"The dialog \"{dialog.Title}\" has no button called \"{button}\", and none this can read."
                : $"The dialog \"{dialog.Title}\" has no button called \"{button}\". It offers: {offered}.");
    }

    /// <summary>Every push button on a dialog, with the ampersand Windows uses for accelerators removed.</summary>
    private static List<(IntPtr Handle, string Text)> ButtonsOf(IntPtr dialog)
    {
        List<(IntPtr, string)> found = new();

        EnumChildWindows(
            dialog,
            (handle, unused) =>
            {
                if (ClassOf(handle) == "Button" && IsWindowVisible(handle) && IsWindowEnabled(handle))
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
    /// </remarks>
    private static Dialog ModalDialog()
    {
        try
        {
            using Process self = Process.GetCurrentProcess();
            IntPtr main = self.MainWindowHandle;

            if (main == IntPtr.Zero || IsWindowEnabled(main))
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

                    bool owned = GetWindow(handle, (IntPtr)GwOwner) == main;
                    if (!owned && ClassOf(handle) != DialogClass)
                    {
                        return true;
                    }

                    title = TitleOf(handle);
                    dialog = handle;

                    // Keep looking only while nothing readable has been found: a titled window is the one
                    // worth naming, and an untitled one is better than nothing.
                    return string.IsNullOrEmpty(title);
                },
                IntPtr.Zero);

            return new Dialog(true, title, dialog);
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

    private const int BmClick = 0x00F5;

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr handle);

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
