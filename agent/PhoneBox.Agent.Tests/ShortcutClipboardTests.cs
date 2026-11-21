using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Macro steps that use the clipboard. The clipboard and the keyboard are replaced by recorders, so no test touches the machine's own.</summary>
[Collection("Macro recorders")]
public sealed class ShortcutClipboardTests : IDisposable
{
    private sealed class FakeClipboard(string text) : Clipboard.IBackend
    {
        public string Text = text;
        public int Reads;
        public readonly List<bool> Sensitive = [];
        public string? Get() { Reads++; return Text; }
        public void Set(string value, bool sensitive) { Text = value; Sensitive.Add(sensitive); }
    }

    private readonly FakeClipboard _clip = new("from the clipboard");
    private readonly List<string> _events = [];

    public ShortcutClipboardTests()
    {
        Clipboard.TestBackend = _clip;
        Shortcuts.TestKeys = k => _events.Add("keys:" + k);
        Shortcuts.TestType = t => _events.Add("type:" + t);
        Shortcuts.PasteSettleMs = 0;
    }

    public void Dispose()
    {
        Clipboard.TestBackend = null;
        Shortcuts.TestKeys = null;
        Shortcuts.TestType = null;
        Shortcuts.PasteSettleMs = 400;
    }

    private string? Macro(params string[] steps) => Shortcuts.Execute(new Shortcut { Id = "m", Label = "m", Cmd = "builtin:macro", Steps = [.. steps] }, NullLogger.Instance);

    [Fact]
    public void ClipboardStepPutsTheTextOnTheClipboard()
    {
        Assert.Null(Macro("clipboard:hello there"));
        Assert.Equal("hello there", _clip.Text);
        Assert.Empty(_events);
    }

    [Fact]
    public void PasteStepPutsTheTextOnTheClipboardForThePasteKeysAndThenPutsTheOldTextBack()
    {
        var onClipboardDuringThePaste = "";
        Shortcuts.TestKeys = k => { _events.Add("keys:" + k); onClipboardDuringThePaste = _clip.Text; };
        Assert.Null(Macro("paste:ünïcode ✓ text"));
        Assert.Equal([OperatingSystem.IsMacOS() ? "keys:win+v" : "keys:ctrl+v"], _events);
        Assert.Equal("ünïcode ✓ text", onClipboardDuringThePaste);
        Assert.Equal("from the clipboard", _clip.Text); // what the user had copied is back
    }

    [Fact]
    public void WhenThePasteKeysFailTheOldTextStillComesBack()
    {
        Shortcuts.TestKeys = _ => throw new InvalidOperationException("no keyboard");
        Assert.NotNull(Macro("paste:secret-token"));
        Assert.Equal("from the clipboard", _clip.Text);
    }

    [Fact]
    public void AfterPastingOverAPictureTheClipboardIsLeftEmptyNotHoldingThePastedText()
    {
        _clip.Text = ""; // a picture or files read as no text
        Assert.Null(Macro("paste:secret-token"));
        Assert.Equal("", _clip.Text); // the token does not stay behind
    }

    [Fact]
    public void AClipboardStepWithNothingToPutEmptiesTheClipboard()
    {
        _clip.Text = "";
        Assert.Null(Macro("clipboard:{clipboard}"));
        Assert.Equal("", _clip.Text);
    }

    [Fact]
    public void ThePastedTextIsMarkedSensitiveAndAClipboardStepIsNot()
    {
        Assert.Null(Macro("paste:token-123"));
        Assert.All(_clip.Sensitive, Assert.True);
        _clip.Sensitive.Clear();
        Assert.Null(Macro("clipboard:kept for later"));
        Assert.Equal([false], _clip.Sensitive);
    }

    [Fact]
    public void SomethingCopiedDuringThePasteIsNotOverwrittenByTheRestore()
    {
        Shortcuts.TestKeys = k => { _events.Add("keys:" + k); _clip.Text = "copied meanwhile"; };
        Assert.Null(Macro("paste:token-123"));
        Assert.Equal("copied meanwhile", _clip.Text);
    }

    [Fact]
    public void AClipboardThatCannotBeReadStillPastesAndKeepsThePastedText()
    {
        Clipboard.TestBackend = new UnreadableClipboard(_clip);
        Assert.Null(Macro("paste:token-123"));
        Assert.Equal("token-123", _clip.Text);
        Assert.Single(_events);
    }

    private sealed class UnreadableClipboard(FakeClipboard inner) : Clipboard.IBackend
    {
        public string? Get() => throw new InvalidOperationException("the clipboard is not available");
        public void Set(string text, bool sensitive) => inner.Set(text, sensitive);
    }

    [Fact]
    public void TextStepTypesTheClipboardWhereThePlaceholderIs()
    {
        Assert.Null(Macro("text:[{clipboard}]"));
        Assert.Equal(["type:[from the clipboard]"], _events);
    }

    [Fact]
    public void AClipboardStepCanWrapWhatIsOnTheClipboard()
    {
        Assert.Null(Macro("clipboard:> {clipboard}"));
        Assert.Equal("> from the clipboard", _clip.Text);
    }

    [Fact]
    public void ATypeTextKeyAlsoUsesThePlaceholder()
    {
        Assert.Null(Shortcuts.Execute(new Shortcut { Id = "t", Label = "t", Cmd = "builtin:text", Args = "x={clipboard}" }, NullLogger.Instance));
        Assert.Equal(["type:x=from the clipboard"], _events);
    }

    [Fact]
    public void TheClipboardIsReadOnlyWhenAStepAsksForIt()
    {
        Assert.Null(Macro("text:plain", "clipboard:also plain")); // (paste: reads it too, to put the old text back)
        Assert.Equal(0, _clip.Reads);
    }

    [Fact]
    public void ALongClipboardIsCutBeforeItIsTyped()
    {
        _clip.Text = new string('a', 25_000);
        Assert.Null(Macro("text:{clipboard}"));
        Assert.Equal(10_000, _events.Single().Length - "type:".Length);
    }

    [Fact]
    public void ACutNeverLeavesHalfOfAnEmoji()
    {
        _clip.Text = new string('a', 9_999) + "😀" + "tail"; // the emoji's two halves sit at 9,999 and 10,000
        Assert.Null(Macro("text:{clipboard}"));
        var typed = _events.Single()["type:".Length..];
        Assert.Equal(9_999, typed.Length);
        Assert.DoesNotContain(typed, c => char.IsSurrogate(c));
    }

    [Fact]
    public void AnEmptyClipboardLeavesTheTextAroundIt()
    {
        _clip.Text = "";
        Assert.Null(Macro("text:<{clipboard}>"));
        Assert.Equal(["type:<>"], _events);
    }

    [Fact]
    public void ANonTextClipboardIsEmptyText()
    {
        Clipboard.TestBackend = new NullClipboard();
        Assert.Null(Macro("text:<{clipboard}>"));
        Assert.Equal(["type:<>"], _events);
    }

    private sealed class NullClipboard : Clipboard.IBackend
    {
        public string? Get() => null;
        public void Set(string text, bool sensitive) { }
    }

    [Fact]
    public void TheErrorListNamesTheNewSteps()
    {
        var error = Macro("nonsense:1");
        Assert.NotNull(error);
        Assert.Contains("clipboard:", error);
        Assert.Contains("paste:", error);
    }
}
