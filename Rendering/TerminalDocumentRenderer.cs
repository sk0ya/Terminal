using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

using Terminal.Buffer;

namespace Terminal.Rendering;

internal sealed class TerminalDocumentRenderer
{
    private readonly FlowDocument _document = new()
    {
        TextAlignment = TextAlignment.Left
    };

    private readonly List<Paragraph> _paragraphs = [];
    private readonly List<FrameworkElement?> _paragraphCursorAnchors = [];
    private readonly List<AnsiTerminalBuffer.TerminalRenderLineSnapshot> _renderedLines = [];

    public FlowDocument Document => _document;

    public FrameworkElement? CursorAnchor { get; private set; }

    public void Update(AnsiTerminalBuffer.TerminalRenderSnapshot snapshot, FontFamily fontFamily, double fontSize, Brush background)
    {
        _document.FontFamily = fontFamily;
        _document.FontSize = fontSize;
        _document.Background = background;

        ApplyDiff(snapshot.Lines);
    }

    private void ApplyDiff(AnsiTerminalBuffer.TerminalRenderLineSnapshot[] nextLines)
    {
        int prefixLength = FindCommonPrefix(nextLines);
        int suffixLength = FindCommonSuffix(nextLines, prefixLength);
        int oldCount = _renderedLines.Count;
        int newCount = nextLines.Length;
        int oldMiddleCount = oldCount - prefixLength - suffixLength;
        int newMiddleCount = newCount - prefixLength - suffixLength;
        int sharedMiddleCount = Math.Min(oldMiddleCount, newMiddleCount);

        for (int index = 0; index < sharedMiddleCount; index++)
        {
            int lineIndex = prefixLength + index;
            UpdateParagraph(lineIndex, nextLines[lineIndex]);
        }

        if (oldMiddleCount > newMiddleCount)
        {
            int removeCount = oldMiddleCount - newMiddleCount;
            int removeIndex = prefixLength + sharedMiddleCount;
            for (int index = 0; index < removeCount; index++)
            {
                RemoveParagraph(removeIndex);
            }
        }

        if (newMiddleCount > oldMiddleCount)
        {
            int insertIndex = prefixLength + sharedMiddleCount;
            int insertCount = newMiddleCount - oldMiddleCount;
            for (int index = 0; index < insertCount; index++)
            {
                InsertParagraph(insertIndex + index, nextLines[insertIndex + index]);
            }
        }

        _renderedLines.Clear();
        _renderedLines.AddRange(nextLines);
        CursorAnchor = ResolveCursorAnchor(nextLines);
    }

    private int FindCommonPrefix(AnsiTerminalBuffer.TerminalRenderLineSnapshot[] nextLines)
    {
        int maxLength = Math.Min(_renderedLines.Count, nextLines.Length);
        int index = 0;
        while (index < maxLength && _renderedLines[index].ContentEquals(nextLines[index]))
        {
            index++;
        }

        return index;
    }

    private int FindCommonSuffix(AnsiTerminalBuffer.TerminalRenderLineSnapshot[] nextLines, int prefixLength)
    {
        int oldCount = _renderedLines.Count;
        int newCount = nextLines.Length;
        int suffixLength = 0;
        while (suffixLength < oldCount - prefixLength &&
               suffixLength < newCount - prefixLength &&
               _renderedLines[oldCount - 1 - suffixLength].ContentEquals(nextLines[newCount - 1 - suffixLength]))
        {
            suffixLength++;
        }

        return suffixLength;
    }

    private void UpdateParagraph(int index, AnsiTerminalBuffer.TerminalRenderLineSnapshot lineSnapshot)
    {
        FrameworkElement? cursorAnchor = null;
        PopulateParagraph(_paragraphs[index], lineSnapshot, ref cursorAnchor);
        _paragraphCursorAnchors[index] = cursorAnchor;
    }

    private void InsertParagraph(int index, AnsiTerminalBuffer.TerminalRenderLineSnapshot lineSnapshot)
    {
        FrameworkElement? cursorAnchor = null;
        var paragraph = CreateParagraph(lineSnapshot, ref cursorAnchor);
        if (index >= _paragraphs.Count)
        {
            _document.Blocks.Add(paragraph);
        }
        else
        {
            _document.Blocks.InsertBefore(_paragraphs[index], paragraph);
        }

        _paragraphs.Insert(index, paragraph);
        _paragraphCursorAnchors.Insert(index, cursorAnchor);
    }

    private void RemoveParagraph(int index)
    {
        _document.Blocks.Remove(_paragraphs[index]);
        _paragraphs.RemoveAt(index);
        _paragraphCursorAnchors.RemoveAt(index);
    }

    private static Paragraph CreateParagraph(AnsiTerminalBuffer.TerminalRenderLineSnapshot lineSnapshot, ref FrameworkElement? cursorAnchor)
    {
        var paragraph = new Paragraph();
        PopulateParagraph(paragraph, lineSnapshot, ref cursorAnchor);
        return paragraph;
    }

    private static void PopulateParagraph(Paragraph paragraph, AnsiTerminalBuffer.TerminalRenderLineSnapshot lineSnapshot, ref FrameworkElement? cursorAnchor)
    {
        paragraph.Inlines.Clear();
        if (lineSnapshot.Segments.Length == 0)
        {
            if (lineSnapshot.AnchorSegmentIndex == 0)
            {
                AnsiTerminalBuffer.InsertCursorAnchor(paragraph.Inlines, ref cursorAnchor);
            }

            paragraph.Inlines.Add(new Run(string.Empty));
            return;
        }

        for (int index = 0; index < lineSnapshot.Segments.Length; index++)
        {
            if (lineSnapshot.AnchorSegmentIndex == index)
            {
                AnsiTerminalBuffer.InsertCursorAnchor(paragraph.Inlines, ref cursorAnchor);
            }

            AnsiTerminalBuffer.AppendSegment(paragraph.Inlines, lineSnapshot.Segments[index]);
        }

        if (lineSnapshot.AnchorSegmentIndex == lineSnapshot.Segments.Length)
        {
            AnsiTerminalBuffer.InsertCursorAnchor(paragraph.Inlines, ref cursorAnchor);
        }

        if (paragraph.Inlines.Count == 0)
        {
            paragraph.Inlines.Add(new Run(string.Empty));
        }
    }

    private FrameworkElement? ResolveCursorAnchor(AnsiTerminalBuffer.TerminalRenderLineSnapshot[] nextLines)
    {
        for (int index = 0; index < nextLines.Length && index < _paragraphCursorAnchors.Count; index++)
        {
            if (nextLines[index].AnchorSegmentIndex >= 0)
            {
                return _paragraphCursorAnchors[index];
            }
        }

        return null;
    }
}
