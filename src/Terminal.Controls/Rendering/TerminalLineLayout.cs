using System.Collections.Immutable;

using Terminal.Buffer;

namespace Terminal.Rendering;

/// <summary>Drawing-independent layout data for one render-snapshot line.</summary>
internal readonly record struct TerminalLineLayout(
    string Text,
    int CellLength,
    ImmutableArray<TerminalLineSegmentLayout> Segments,
    ImmutableArray<TerminalHyperlinkSegment> HyperlinkSegments,
    TerminalTextCellMap TextCellMap,
    TerminalLineSize LineSize = TerminalLineSize.SingleWidth,
    ImmutableArray<TerminalImage> Images = default);

/// <summary>A render segment and its cell offset within its line.</summary>
internal readonly record struct TerminalLineSegmentLayout(
    int StartCell,
    AnsiTerminalBuffer.TerminalRenderSegmentSnapshot Snapshot);

/// <summary>Builds line layout data without a WPF control or drawing resources.</summary>
internal static class TerminalLineLayoutBuilder
{
    public static TerminalLineLayout Create(
        AnsiTerminalBuffer.TerminalRenderLineSnapshot line,
        bool ambiguousAsWide)
    {
        var segments = ImmutableArray.CreateBuilder<TerminalLineSegmentLayout>(line.Segments.Length);
        var hyperlinkSegments = ImmutableArray.CreateBuilder<TerminalHyperlinkSegment>(line.Segments.Length);
        int cellOffset = 0;

        for (int index = 0; index < line.Segments.Length; index++)
        {
            AnsiTerminalBuffer.TerminalRenderSegmentSnapshot segment = line.Segments[index];
            segments.Add(new TerminalLineSegmentLayout(cellOffset, segment));
            hyperlinkSegments.Add(new TerminalHyperlinkSegment(
                cellOffset, segment.CellLength, segment.Hyperlink));
            cellOffset += segment.CellLength;
        }

        TerminalTextCellMap textCellMap = TerminalTextCellMap.Create(
            line.Segments.Select(static segment => (segment.Text, segment.CellLength)),
            line.CellLength,
            ambiguousAsWide);

        return new TerminalLineLayout(
            textCellMap.Text,
            line.CellLength,
            segments.MoveToImmutable(),
            hyperlinkSegments.MoveToImmutable(),
            textCellMap,
            line.LineSize,
            line.Images is { Length: > 0 } images
                ? ImmutableArray.Create(images)
                : ImmutableArray<TerminalImage>.Empty);
    }
}
