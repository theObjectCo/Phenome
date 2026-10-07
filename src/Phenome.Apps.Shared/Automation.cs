using System.Reflection;

namespace Phenome.Apps;

/// <summary>
/// Reads a dialog's text and buttons through UI Automation, and presses a button that has no window of its own.
/// </summary>
/// <remarks>
/// Rhino 8's own dialogs are Eto windows drawn by WPF: "Save changes to Untitled?", the autosave recovery box
/// and the "Exception Occured" box among them. A WPF window has one window handle and no child windows, so
/// <see cref="Pulse"/> found neither its buttons nor its message by enumerating windows. It reported the title
/// alone and <c>clickable:false</c>, and the exception box's traceback could be read only by an agent driving UI
/// Automation from outside. UI Automation sees WPF, WinForms and Win32 controls alike.
/// <para>
/// The client assembly, UIAutomationClient, belongs to the Windows desktop runtime that Rhino loads for WPF.
/// It is reached by reflection: the plug-ins target plain <c>net7.0</c>, which cannot reference it, and a
/// Rhino without it gets no text and the window-based buttons only.
/// </para>
/// <para>
/// Calls are made from a worker thread while the dialog's modal loop runs. WPF answers them on its own thread
/// through that loop. Each call is given <see cref="Patience"/> and abandoned after it, so a dialog that does not
/// answer cannot hold <c>pulse</c>, which exists to answer when nothing else does.
/// </para>
/// </remarks>
internal static class Automation
{
    /// <summary>How long one read or press may take before it is given up.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    /// <summary>The dialog's message and the labels of its buttons, as a person reads them.</summary>
    internal readonly record struct Contents(string Text, IReadOnlyList<string> Buttons);

    /// <summary>What the window says and offers, or null when UI Automation is unavailable or did not answer.</summary>
    internal static Contents? Read(IntPtr window) => Within<Contents?>(() =>
    {
        Api api = Loaded.Value ?? throw new InvalidOperationException("UI Automation is not available.");

        List<string> texts = [];
        List<string> labels = [];
        HashSet<string> insideButtons = new(StringComparer.Ordinal);

        foreach (dynamic element in api.Content(api.FromHandle(window)))
        {
            dynamic current = element.Current;

            if (Equals(current.ControlType, api.Button))
            {
                string label = Label(api, element);

                if (label.Length > 0)
                {
                    labels.Add(label);
                }

                foreach (dynamic inner in api.Descendants(element))
                {
                    insideButtons.Add((string)inner.Current.Name);
                }

                insideButtons.Add((string)current.Name);
            }
            else if (Equals(current.ControlType, api.Text) || ((string)current.ClassName).Contains("Static"))
            {
                string text = ((string)current.Name).Trim();

                if (text.Length > 0)
                {
                    texts.Add(text);
                }
            }
        }

        // WPF reports a text twice, once for the element and once for the TextBlock inside it.
        List<string> message = [.. texts.Where(text => !insideButtons.Contains(text)).Distinct()];

        return new Contents(string.Join("\n", message), labels);
    });

    /// <summary>Presses the button labelled <paramref name="label"/>; false when there is none.</summary>
    internal static bool Press(IntPtr window, string label) => Within(() =>
    {
        Api api = Loaded.Value ?? throw new InvalidOperationException("UI Automation is not available.");

        foreach (dynamic element in api.Content(api.FromHandle(window)))
        {
            if (Equals(element.Current.ControlType, api.Button)
                && string.Equals(Label(api, element), label, StringComparison.OrdinalIgnoreCase))
            {
                element.GetCurrentPattern((dynamic)api.Invoke).Invoke();
                return true;
            }
        }

        return false;
    });

    /// <summary>
    /// A button's label: its name, or the text inside it when the name is empty, as on Eto's Yes and No.
    /// </summary>
    /// <remarks>
    /// WPF marks the access key with an underscore (<c>_Yes</c>) and Win32 with an ampersand; both are removed.
    /// </remarks>
    private static string Label(Api api, dynamic button)
    {
        string name = (string)button.Current.Name;

        if (string.IsNullOrWhiteSpace(name))
        {
            foreach (dynamic inner in api.Descendants(button))
            {
                name = (string)inner.Current.Name;

                if (!string.IsNullOrWhiteSpace(name))
                {
                    break;
                }
            }
        }

        return name.Replace("&", "").Replace("__", "\u0001").Replace("_", "").Replace("\u0001", "_").Trim();
    }

    /// <summary>Why the last read or press failed, for a refusal to quote.</summary>
    internal static string? Failure { get; private set; }

    /// <summary>Runs <paramref name="work"/> on a worker thread and waits <see cref="Patience"/> for it.</summary>
    private static T? Within<T>(Func<T> work)
    {
        if (!OperatingSystem.IsWindows() || Loaded.Value is null)
        {
            return default;
        }

        try
        {
            Task<T> task = Task.Run(work);

            return task.Wait(Patience) ? task.Result : default;
        }
        catch (Exception failed)
        {
            // A dialog closing while it is read throws from inside UI Automation. Nothing read is reported as
            // nothing, and the caller falls back to what windows alone show.
            Failure = failed.GetBaseException().Message;
            return default;
        }
    }

    private static readonly Lazy<Api?> Loaded = new(Api.Load);

    /// <summary>The handful of UI Automation members used here, found once by reflection.</summary>
    private sealed class Api
    {
        private readonly MethodInfo fromHandle;
        private readonly MethodInfo findAll;
        private readonly object descendants;
        private readonly object everything;

        internal object Button { get; }

        internal object Text { get; }

        private readonly object titleBar;

        private readonly object children;

        internal object Invoke { get; }

        private Api(Type element, Type scope, Type condition, Type controlType, Type invoke)
        {
            fromHandle = element.GetMethod("FromHandle", [typeof(IntPtr)])!;
            findAll = element.GetMethod("FindAll")!;
            descendants = Enum.Parse(scope, "Descendants");
            everything = condition.GetField("TrueCondition")!.GetValue(null)!;
            Button = controlType.GetField("Button")!.GetValue(null)!;
            Text = controlType.GetField("Text")!.GetValue(null)!;
            titleBar = controlType.GetField("TitleBar")!.GetValue(null)!;
            children = Enum.Parse(scope, "Children");
            Invoke = invoke.GetField("Pattern")!.GetValue(null)!;
        }

        internal object FromHandle(IntPtr window) => fromHandle.Invoke(null, [window])!;

        internal IEnumerable<object> Descendants(object element) =>
            ((System.Collections.IEnumerable)findAll.Invoke(element, [descendants, everything])!).Cast<object>();

        /// <summary>
        /// Every element inside <paramref name="element"/> in reading order, leaving out title bars.
        /// </summary>
        /// <remarks>
        /// Read from inside Rhino's process, a WPF window lists its title bar with a Close button, which the same
        /// window read from outside does not. That Close is the window's X, not one of the dialog's answers.
        /// </remarks>
        internal IEnumerable<object> Content(object element)
        {
            foreach (object child in ((System.Collections.IEnumerable)findAll.Invoke(element, [children, everything])!).Cast<object>())
            {
                if (Equals(((dynamic)child).Current.ControlType, titleBar))
                {
                    continue;
                }

                yield return child;

                foreach (object inner in Content(child))
                {
                    yield return inner;
                }
            }
        }

        internal static Api? Load()
        {
            try
            {
                Assembly client = Assembly.Load("UIAutomationClient");
                Assembly types = Assembly.Load("UIAutomationTypes");

                return new Api(
                    client.GetType("System.Windows.Automation.AutomationElement", throwOnError: true)!,
                    types.GetType("System.Windows.Automation.TreeScope", throwOnError: true)!,
                    client.GetType("System.Windows.Automation.Condition", throwOnError: true)!,
                    types.GetType("System.Windows.Automation.ControlType", throwOnError: true)!,
                    client.GetType("System.Windows.Automation.InvokePattern", throwOnError: true)!);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
