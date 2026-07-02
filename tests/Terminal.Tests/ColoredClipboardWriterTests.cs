using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;

using Terminal.Rendering;

namespace Terminal.Tests;

public sealed class ColoredClipboardWriterTests
{
    private static readonly Color Black = Color.FromRgb(0x00, 0x00, 0x00);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x00, 0x00);
    private static readonly Color Green = Color.FromRgb(0x00, 0xFF, 0x00);
    private static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);

    private static StyledSelection Selection(params IReadOnlyList<StyledRun>[] lines)
        => new(lines, White, Black);

    private static StyledRun Run(string text, Color? fg = null, Color? bg = null,
        bool bold = false, bool italic = false, bool underline = false)
        => new(text, fg ?? White, bg ?? Black, bold, italic, underline);

    [Fact]
    public void BuildHtmlComputesFragmentOffsetsMatchingCommentPositions()
    {
        StyledSelection selection = Selection([Run("Hello", Red, Black)]);

        string html = ColoredClipboardWriter.BuildHtml(selection);

        int startHtml = ParseHeaderValue(html, "StartHTML");
        int endHtml = ParseHeaderValue(html, "EndHTML");
        int startFragment = ParseHeaderValue(html, "StartFragment");
        int endFragment = ParseHeaderValue(html, "EndFragment");

        // StartFragment はフラグメント開始コメント直後、EndFragment は終了コメント開始位置。
        int commentEnd = html.IndexOf("<!--StartFragment-->", StringComparison.Ordinal)
            + "<!--StartFragment-->".Length;
        int endCommentStart = html.IndexOf("<!--EndFragment-->", StringComparison.Ordinal);
        int htmlTagStart = html.IndexOf("<html", StringComparison.Ordinal);

        Assert.Equal(ByteOffset(html, commentEnd), startFragment);
        Assert.Equal(ByteOffset(html, endCommentStart), endFragment);
        Assert.Equal(ByteOffset(html, htmlTagStart), startHtml);
        Assert.Equal(Encoding.UTF8.GetByteCount(html), endHtml);
    }

    [Fact]
    public void BuildHtmlEscapesSpecialCharacters()
    {
        StyledSelection selection = Selection([Run("a<b>&\"c")]);

        string html = ColoredClipboardWriter.BuildHtml(selection);

        Assert.Contains("a&lt;b&gt;&amp;&quot;c", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>&", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHtmlEmitsColorAndDecorationStyles()
    {
        StyledSelection selection = Selection(
        [
            Run("Hi", Red, Black, bold: true, italic: true, underline: true),
        ]);

        string html = ColoredClipboardWriter.BuildHtml(selection);

        Assert.Contains("color:#ff0000", html, StringComparison.Ordinal);
        Assert.Contains("background-color:#000000", html, StringComparison.Ordinal);
        Assert.Contains("font-weight:bold", html, StringComparison.Ordinal);
        Assert.Contains("font-style:italic", html, StringComparison.Ordinal);
        Assert.Contains("text-decoration:underline", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHtmlProducesTwoColorTwoLineFragment()
    {
        StyledSelection selection = Selection(
            [Run("Hi", Red, Black)],
            [Run("Yo", Green, Black)]);

        string html = ColoredClipboardWriter.BuildHtml(selection);
        int fragmentStart = html.IndexOf("<!--StartFragment-->", StringComparison.Ordinal);
        int fragmentEnd = html.IndexOf("<!--EndFragment-->", StringComparison.Ordinal);
        string fragment = html[fragmentStart..fragmentEnd];

        Assert.Contains("color:#ff0000", fragment, StringComparison.Ordinal);
        Assert.Contains("color:#00ff00", fragment, StringComparison.Ordinal);
        Assert.Contains(">Hi</span>", fragment, StringComparison.Ordinal);
        Assert.Contains(">Yo</span>", fragment, StringComparison.Ordinal);
        // 2 行なので <pre> 内に 1 個の改行が入る。
        int preStart = fragment.IndexOf("<pre", StringComparison.Ordinal);
        Assert.Equal(1, fragment[preStart..].Count(c => c == '\n'));
    }

    [Fact]
    public void BuildRtfBuildsColorTableAndSetsRunColors()
    {
        StyledSelection selection = Selection(
            [Run("Hi", Red, Black)],
            [Run("Yo", Green, Black)]);

        string rtf = ColoredClipboardWriter.BuildRtf(selection);

        // 色 1 番=赤、2 番=黒、3 番=緑（登場順）。
        Assert.Contains("{\\colortbl;\\red255\\green0\\blue0;\\red0\\green0\\blue0;\\red0\\green255\\blue0;}", rtf, StringComparison.Ordinal);
        Assert.Contains("\\cf1\\cb2 Hi}", rtf, StringComparison.Ordinal);
        Assert.Contains("\\cf3\\cb2 Yo}", rtf, StringComparison.Ordinal);
        Assert.Contains("\\par", rtf, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRtfEscapesBracesBackslashAndNonAscii()
    {
        StyledSelection selection = Selection([Run("{a}\\bあ")]);

        string rtf = ColoredClipboardWriter.BuildRtf(selection);

        Assert.Contains("\\{a\\}\\\\b\\u12354?", rtf, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRtfEmitsBoldItalicUnderlineControlWords()
    {
        StyledSelection selection = Selection([Run("x", Red, Black, bold: true, italic: true, underline: true)]);

        string rtf = ColoredClipboardWriter.BuildRtf(selection);

        Assert.Contains("\\b\\i\\ul x}", rtf, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPlainTextJoinsRunsAndLinesWithCrlf()
    {
        StyledSelection selection = Selection(
            [Run("foo", Red, Black), Run("bar", Green, Black)],
            [Run("baz")]);

        string text = ColoredClipboardWriter.BuildPlainText(selection);

        Assert.Equal("foobar\r\nbaz", text);
    }

    private static int ParseHeaderValue(string html, string name)
    {
        Match match = Regex.Match(html, $"{name}:(\\d+)");
        Assert.True(match.Success, $"header {name} not found");
        return int.Parse(match.Groups[1].Value);
    }

    private static int ByteOffset(string html, int charIndex)
        => Encoding.UTF8.GetByteCount(html[..charIndex]);
}
