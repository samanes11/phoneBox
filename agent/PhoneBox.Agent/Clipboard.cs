using System.Diagnostics;
using System.Text;
#if WINDOWS
using System.Windows.Forms;
#endif

namespace PhoneBox.Agent;

/// <summary>
/// The computer's text clipboard, for macro steps (<c>clipboard:</c>, <c>paste:</c>) and the <c>{clipboard}</c> placeholder.
/// The text stays in the agent: it is never sent to the phone, logged or kept.
///  - Windows: the Windows Forms clipboard, on a short-lived thread of its own (it needs an STA thread, and it owns the
///    clipboard the way Windows expects).
///  - macOS: pbcopy / pbpaste.
///  - Linux: wl-copy / wl-paste (Wayland), or xclip / xsel (X11).
/// </summary>
public static class Clipboard
{
    internal interface IBackend
    {
        string? Get();
        void Set(string text, bool sensitive);
    }

    /// <summary>Tests swap the clipboard for one in memory, so they never touch (or depend on) the machine's own.</summary>
    internal static IBackend? TestBackend;

    /// <summary>The text on the clipboard; empty when it holds something else (a picture, files) or nothing.</summary>
    public static string GetText() => (TestBackend is { } t ? t.Get() : Read()) ?? "";

    /// <summary>
    /// Replaces what is on the clipboard with the text. Throws when no clipboard tool is available. With
    /// <paramref name="sensitive"/> (a token, an address typed by a paste: step) Windows is told to keep it out of its
    /// clipboard history, the cloud clipboard and clipboard managers; elsewhere there is no such flag to set.
    /// </summary>
    public static void SetText(string text, bool sensitive = false)
    {
        if (TestBackend is { } t) t.Set(text, sensitive);
        else Write(text, sensitive);
    }

#if WINDOWS
    // Formats that Windows' clipboard history (Win+V), the cloud clipboard and clipboard managers look for before they keep a copy
    private static readonly string[] SensitiveFormats =
        ["ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard"];

    private static string? Read() => OnStaThread(() =>
        System.Windows.Forms.Clipboard.ContainsText(TextDataFormat.UnicodeText) ? System.Windows.Forms.Clipboard.GetText(TextDataFormat.UnicodeText) : "");

    private static void Write(string text, bool sensitive) => OnStaThread<object?>(() =>
    {
        // empty text empties the clipboard: DataObject.SetText refuses an empty string. This is how a paste: step whose
        // clipboard held a picture or files (no text to put back) removes its own text afterwards.
        if (text.Length == 0) { System.Windows.Forms.Clipboard.Clear(); return null; }
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        if (sensitive)
        {
            // best effort: a Windows that ignores a format only keeps the text in its history, which is no worse than before
            foreach (var format in SensitiveFormats)
            {
                try { data.SetData(format, new MemoryStream(BitConverter.GetBytes(0))); }
                catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException) { }
            }
        }
        // copy: true keeps the text after this process exits; another program may hold the clipboard for a moment
        System.Windows.Forms.Clipboard.SetDataObject(data, copy: true, retryTimes: 10, retryDelay: 50);
        return null;
    });

    /// <summary>The Windows Forms clipboard needs a single-threaded apartment, which the agent's own threads are not.</summary>
    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true, Name = "clipboard" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("the clipboard did not answer");
        if (failure is not null) throw new InvalidOperationException("the clipboard is not available: " + failure.GetType().Name, failure);
        return result;
    }
#else
    private static bool Wayland => Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 };

    private const string Missing = "the clipboard needs pbcopy / pbpaste (macOS), wl-clipboard (Wayland) or xclip / xsel (X11)";

    private static string? Read()
    {
        // pbpaste exits 0 even for an empty clipboard, so null from it means it did not run or finish: an error, not "empty".
        // xclip, xsel and wl-paste exit non-zero when the clipboard holds nothing as text (an empty clipboard, a picture),
        // which cannot be told apart from a failure here; that is "no text", as on Windows.
        if (OperatingSystem.IsMacOS()) return Sh.Run("pbpaste") ?? throw new InvalidOperationException("pbpaste failed");
        if (Wayland && Sh.Which("wl-paste") is not null) return Sh.Run("wl-paste", "--no-newline") ?? "";
        if (Sh.Which("xclip") is not null) return Sh.Run("xclip", "-selection", "clipboard", "-out") ?? "";
        if (Sh.Which("xsel") is not null) return Sh.Run("xsel", "--clipboard", "--output") ?? "";
        if (Sh.Which("wl-paste") is not null) return Sh.Run("wl-paste", "--no-newline") ?? "";
        throw new InvalidOperationException(Missing);
    }

    private static void Write(string text, bool sensitive)
    {
        // no flag to set here: on a Mac a note in the docs is all there is (Universal Clipboard may sync it)
        if (OperatingSystem.IsMacOS()) Pipe("pbcopy", text);
        else if (Wayland && Sh.Which("wl-copy") is not null) Pipe("wl-copy", text);
        else if (Sh.Which("xclip") is not null) Pipe("xclip", text, "-selection", "clipboard", "-in");
        else if (Sh.Which("xsel") is not null) Pipe("xsel", text, "--clipboard", "--input");
        else if (Sh.Which("wl-copy") is not null) Pipe("wl-copy", text);
        else throw new InvalidOperationException(Missing);
    }

    /// <summary>
    /// Feeds the text to a tool's standard input. Its output is not captured: xclip and wl-copy stay running in the
    /// background to serve the clipboard, and would keep a captured pipe open until the timeout.
    /// </summary>
    private static void Pipe(string tool, string text, params string[] args)
    {
        var psi = new ProcessStartInfo(Sh.Which(tool) ?? throw new InvalidOperationException(Missing))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false), WorkingDirectory = AppPaths.NeutralCwd,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new IOException("Could not start " + tool);
        // the write has the same deadline as the tool: one that stops reading would block it once the pipe is full
        var feed = Task.Run(() =>
        {
            try
            {
                process.StandardInput.Write(text);
                process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { /* the tool quit early (or was killed); its exit code says why */ }
        });
        var deadline = DateTime.UtcNow.AddSeconds(3);
        if (!feed.Wait(TimeSpan.FromSeconds(3)) || !process.WaitForExit(Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds)))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException(tool + " did not finish");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException(tool + " failed");
    }
#endif
}
