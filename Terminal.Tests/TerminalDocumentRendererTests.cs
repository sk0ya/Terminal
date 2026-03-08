using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Documents;
using System.Windows.Media;

using Terminal.Buffer;
using Terminal.Rendering;

namespace Terminal.Tests;

public sealed class TerminalDocumentRendererTests
{
    [Fact]
    public void CreateRenderSnapshotIncludesScrollbackAndCursorAnchor()
    {
        var buffer = new AnsiTerminalBuffer(8, 2);

        buffer.Process("A\r\nB\r\nC");

        AnsiTerminalBuffer.TerminalRenderSnapshot snapshot = buffer.CreateRenderSnapshot(showCursor: false);

        Assert.Equal(3, snapshot.Lines.Length);
        Assert.Equal("A", GetLineText(snapshot.Lines[0]));
        Assert.Equal("B", GetLineText(snapshot.Lines[1]));
        Assert.Equal("C", GetLineText(snapshot.Lines[2]));
        Assert.Equal(1, snapshot.Lines[2].AnchorSegmentIndex);
    }

    [Fact]
    public void RendererPreservesUnchangedPrefixAndSuffixParagraphs()
    {
        RunSta(() =>
        {
            var renderer = new TerminalDocumentRenderer();
            var fontFamily = new FontFamily("Cascadia Mono");
            AnsiTerminalBuffer.TerminalRenderLineSnapshot firstLine = CreateLine("first");
            AnsiTerminalBuffer.TerminalRenderLineSnapshot anchoredLastLine = CreateLine("last", anchorSegmentIndex: 1);

            renderer.Update(
                new AnsiTerminalBuffer.TerminalRenderSnapshot([firstLine, anchoredLastLine]),
                fontFamily,
                14,
                Brushes.Black);

            Paragraph[] initialParagraphs = renderer.Document.Blocks.OfType<Paragraph>().ToArray();
            Paragraph originalFirst = initialParagraphs[0];
            Paragraph originalLast = initialParagraphs[1];

            renderer.Update(
                new AnsiTerminalBuffer.TerminalRenderSnapshot([firstLine, CreateLine("middle"), anchoredLastLine]),
                fontFamily,
                14,
                Brushes.Black);

            Paragraph[] updatedParagraphs = renderer.Document.Blocks.OfType<Paragraph>().ToArray();

            Assert.Equal(3, updatedParagraphs.Length);
            Assert.Same(originalFirst, updatedParagraphs[0]);
            Assert.Same(originalLast, updatedParagraphs[2]);
            Assert.Equal("middle", new TextRange(updatedParagraphs[1].ContentStart, updatedParagraphs[1].ContentEnd).Text.Trim());
            Assert.NotNull(renderer.CursorAnchor);
        });
    }

    private static AnsiTerminalBuffer.TerminalRenderLineSnapshot CreateLine(string text, int anchorSegmentIndex = -1)
    {
        return new AnsiTerminalBuffer.TerminalRenderLineSnapshot(
            anchorSegmentIndex,
            text.Length,
            [
                new AnsiTerminalBuffer.TerminalRenderSegmentSnapshot(
                    text,
                    text.Length,
                    Colors.White,
                    Colors.Black,
                    Bold: false,
                    Underline: false,
                    Hyperlink: null)
            ]);
    }

    private static string GetLineText(AnsiTerminalBuffer.TerminalRenderLineSnapshot line)
    {
        return string.Concat(line.Segments.Select(segment => segment.Text));
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ExceptionDispatchInfo.Capture(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        captured?.Throw();
    }
}
