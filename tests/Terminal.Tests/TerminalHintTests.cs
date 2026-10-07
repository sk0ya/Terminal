using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalHintTests
{
    private static IReadOnlyList<string> Texts(params string[] lines) =>
        TerminalHintFinder.Find(lines.Select((text, index) => (index, text))).Select(hint => hint.Text).ToList();

    [Theory]
    [InlineData("see https://example.com/a?b=1.", "https://example.com/a?b=1")]
    [InlineData("(https://x.dev/path)", "https://x.dev/path")]
    [InlineData(@"error in C:\work\src\app.cs: bad", @"C:\work\src\app.cs")]
    [InlineData("edit src/Terminal.Controls/Tabs/TerminalTabView.cs now", "src/Terminal.Controls/Tabs/TerminalTabView.cs")]
    [InlineData("cd ~/projects/terminal", "~/projects/terminal")]
    [InlineData("commit 6acf762 fixed it", "6acf762")]
    [InlineData("id 123e4567-e89b-12d3-a456-426614174000 ok", "123e4567-e89b-12d3-a456-426614174000")]
    [InlineData("listening on 127.0.0.1:8080", "127.0.0.1:8080")]
    public void FindsCommonTargets(string line, string expected)
    {
        Assert.Equal([expected], Texts(line));
    }

    [Theory]
    [InlineData("1234567 is just a number")]
    [InlineData("deadbeef is just a word without digits")]
    [InlineData("plain words only")]
    public void IgnoresLookalikes(string line)
    {
        Assert.Empty(Texts(line));
    }

    [Fact]
    public void NearestTheBottomGetsTheFirstLabelAndRepeatsShareOne()
    {
        IReadOnlyList<TerminalHint> hints = TerminalHintFinder.Find(
            [(0, "a1b2c3d top"), (1, "e4f5a6b middle"), (2, "a1b2c3d bottom")]);

        Assert.Equal("a", hints.Single(h => h.LineIndex == 2).Label);
        Assert.Equal("s", hints.Single(h => h.LineIndex == 1).Label);
        Assert.Equal("a", hints.Single(h => h.LineIndex == 0).Label);
    }

    [Fact]
    public void LabelsAreSingleLettersOrAllTwoLetters()
    {
        Assert.Equal(["a", "s", "d"], TerminalHintFinder.CreateLabels(3));
        IReadOnlyList<string> many = TerminalHintFinder.CreateLabels(30);
        Assert.Equal(30, many.Count);
        Assert.All(many, label => Assert.Equal(2, label.Length));
        Assert.Equal(many.Count, many.Distinct().Count());
        Assert.Equal(26 * 26, TerminalHintFinder.CreateLabels(10_000).Count);
    }

    [Fact]
    public void TypingALabelChoosesItAndShiftAlsoPastes()
    {
        var hints = new List<TerminalHint>
        {
            new(0, 0, 7, "aaaaaa1", "as"),
            new(1, 0, 7, "bbbbbb2", "ad"),
        };

        var selection = new TerminalHintSelection(hints);
        Assert.Equal(TerminalHintKeyResult.Continue, selection.TypeLetter('a', shift: false));
        Assert.Equal(2, selection.Remaining.Count());
        Assert.Equal(TerminalHintKeyResult.Continue, selection.TypeLetter('z', shift: false));
        Assert.Equal("a", selection.Typed);
        Assert.Equal(TerminalHintKeyResult.CopyAndPaste, selection.TypeLetter('D', shift: true));
        Assert.Equal("bbbbbb2", selection.Chosen?.Text);

        var other = new TerminalHintSelection(hints);
        Assert.Equal(TerminalHintKeyResult.Cancel, other.Backspace());
        other.TypeLetter('a', shift: false);
        Assert.Equal(TerminalHintKeyResult.Continue, other.Backspace());
        Assert.Equal(string.Empty, other.Typed);
    }
}
