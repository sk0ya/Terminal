using System.Windows;
using System.Windows.Media;

using Terminal.Buffer;
using Terminal.Rendering;

namespace Terminal.Tests;

public sealed class Osc8HyperlinkIdTests
{
    private const string Esc = "\u001b";

    private static string Open(string parameters, string uri) => $"{Esc}]8;{parameters};{uri}{Esc}\\";

    private static string Close => $"{Esc}]8;;{Esc}\\";

    [Theory]
    [InlineData(";https://a", "https://a", null)]
    [InlineData("id=x;https://a", "https://a", "x")]
    [InlineData("foo=1:id=row-7;https://a?q=1;2", "https://a?q=1;2", "row-7")]
    [InlineData("id=;https://a", "https://a", null)]
    [InlineData("id=x;", "", "x")]
    public void ParsesUriAndId(string body, string uri, string? id)
    {
        Assert.True(TerminalHyperlink.TryParse(body, out string parsedUri, out string? parsedId));
        Assert.Equal(uri, parsedUri);
        Assert.Equal(id, parsedId);
    }

    [Fact]
    public void SameIdAndUriIsOneLinkAcrossSeparateWrites()
    {
        var buffer = new AnsiTerminalBuffer(40, 5);
        buffer.Process($"{Open("id=1", "https://a")}ab{Close} -- {Open("id=1", "https://a")}cd{Close}");

        TerminalHyperlink? first = buffer.GetCellHyperlinkLink(0, 0);
        Assert.NotNull(first);
        Assert.Same(first, buffer.GetCellHyperlinkLink(0, 6));
        Assert.Null(buffer.GetCellHyperlinkLink(0, 3));
    }

    [Fact]
    public void LinksWithoutIdStayApartEvenWithTheSameUri()
    {
        var buffer = new AnsiTerminalBuffer(40, 5);
        buffer.Process($"{Open("", "https://a")}ab{Close}{Open("", "https://a")}cd{Close}");

        Assert.NotSame(buffer.GetCellHyperlinkLink(0, 0), buffer.GetCellHyperlinkLink(0, 2));
        Assert.Equal("https://a", buffer.GetCellHyperlink(0, 2));
    }

    [Fact]
    public void DifferentUriWithTheSameIdIsADifferentLink()
    {
        var buffer = new AnsiTerminalBuffer(40, 5);
        buffer.Process($"{Open("id=1", "https://a")}ab{Close}{Open("id=1", "https://b")}cd{Close}");

        Assert.NotSame(buffer.GetCellHyperlinkLink(0, 0), buffer.GetCellHyperlinkLink(0, 2));
    }

    [Fact]
    public void HoveringOnePieceHoversTheWholeLink()
    {
        StaTestRunner.Run(() =>
        {
            var buffer = new AnsiTerminalBuffer(20, 4);
            // Wraps onto the second row, then a separate piece with the same id on the third.
            buffer.Process($"{Open("id=doc", "https://a")}0123456789012345678901{Close}\r\n");
            buffer.Process($"see {Open("id=doc", "https://a")}here{Close}");

            var surface = new TerminalSurfaceControl { FontFamily = new FontFamily("Cascadia Mono"), FontSize = 14 };
            surface.Measure(new Size(400, 200));
            surface.Arrange(new Rect(0, 0, 400, 200));
            surface.UpdateSnapshot(buffer.CreateRenderSnapshot(showCursor: false));

            surface.HoverTextPositionForTests(lineIndex: 2, textIndex: 5);
            TerminalHyperlink? hovered = surface.HoveredOscLinkForTests;
            Assert.NotNull(hovered);
            Assert.Same(buffer.GetCellHyperlinkLink(0, 0), hovered);
            Assert.Same(buffer.GetCellHyperlinkLink(1, 0), hovered);
        });
    }
}
