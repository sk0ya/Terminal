namespace Terminal.Buffer;

/// <summary>
/// Moves cell-anchored images across a reflow. An image belongs to the cell it was placed at, so a
/// resize has to carry it to whichever target row and column that cell lands on - including images
/// anchored on the continuation rows of a wrapped logical line, which are not part of the first row.
/// </summary>
internal static class TerminalReflowImages
{
    /// <summary>
    /// Appends <paramref name="line"/>'s images to <paramref name="images"/>, rebasing each anchor
    /// column onto <paramref name="lineStartOffset"/>, the line's offset within its logical line.
    /// </summary>
    public static void Collect(
        TerminalLine line,
        int lineStartOffset,
        List<(int Offset, TerminalImage Image)> images)
    {
        foreach (TerminalImage image in line.Images)
        {
            int column = Math.Clamp(image.Column, 0, Math.Max(0, line.Cells.Length - 1));
            images.Add((lineStartOffset + column, image));
        }
    }

    /// <summary>
    /// Moves every collected image whose anchor falls in [<paramref name="startOffset"/>,
    /// <paramref name="endOffset"/>) onto <paramref name="line"/>, removing it from the pending list.
    /// </summary>
    public static void Place(
        TerminalLine line,
        List<(int Offset, TerminalImage Image)> images,
        int startOffset,
        int endOffset,
        int targetColumns)
    {
        int keep = 0;
        for (int index = 0; index < images.Count; index++)
        {
            (int offset, TerminalImage image) = images[index];
            if (offset >= startOffset && offset < endOffset)
            {
                line.Images.Add(image with { Column = ClampColumn(offset - startOffset, targetColumns) });
                continue;
            }

            images[keep++] = images[index];
        }

        images.RemoveRange(keep, images.Count - keep);
    }

    /// <summary>
    /// Appends <paramref name="line"/>'s images to <paramref name="images"/> for a reflow whose
    /// logical offsets count graphemes rather than columns, converting each anchor column to the
    /// number of non-continuation cells that precede it.
    /// </summary>
    public static void CollectByGrapheme(
        TerminalLine line,
        int lineStartOffset,
        int length,
        List<(int Offset, TerminalImage Image)> images)
    {
        foreach (TerminalImage image in line.Images)
        {
            int anchor = Math.Clamp(image.Column, 0, Math.Max(0, line.Cells.Length - 1));
            int grapheme = 0;
            for (int column = 0; column < anchor && column < length; column++)
            {
                if (!line.Cells[column].IsContinuation)
                {
                    grapheme++;
                }
            }

            images.Add((lineStartOffset + grapheme, image));
        }
    }

    /// <summary>
    /// Moves every collected image anchored at <paramref name="offset"/> onto <paramref name="line"/>
    /// at <paramref name="column"/>, for reflows whose offsets do not map one-to-one onto columns.
    /// </summary>
    public static void PlaceAt(
        TerminalLine line,
        List<(int Offset, TerminalImage Image)> images,
        int offset,
        int column,
        int targetColumns)
    {
        int keep = 0;
        for (int index = 0; index < images.Count; index++)
        {
            if (images[index].Offset == offset)
            {
                line.Images.Add(images[index].Image with { Column = ClampColumn(column, targetColumns) });
                continue;
            }

            images[keep++] = images[index];
        }

        images.RemoveRange(keep, images.Count - keep);
    }

    /// <summary>Drains whatever is left onto <paramref name="line"/> at a clamped column.</summary>
    public static void PlaceRemaining(
        TerminalLine line,
        List<(int Offset, TerminalImage Image)> images,
        int fallbackColumn,
        int targetColumns)
    {
        foreach ((int _, TerminalImage image) in images)
        {
            line.Images.Add(image with { Column = ClampColumn(fallbackColumn, targetColumns) });
        }

        images.Clear();
    }

    /// <summary>Copies images for a plain (non-reflowing) resize, clamping anchors to the new width.</summary>
    public static void CopyClamped(TerminalLine source, TerminalLine target, int targetColumns)
    {
        foreach (TerminalImage image in source.Images)
        {
            target.Images.Add(image with { Column = ClampColumn(image.Column, targetColumns) });
        }
    }

    private static int ClampColumn(int column, int targetColumns) =>
        Math.Clamp(column, 0, Math.Max(0, targetColumns - 1));
}
