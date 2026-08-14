namespace Terminal.Buffer;

/// <summary>
/// Reflows existing cells when the ambiguous-width policy changes.
/// Unlike ordinary resize reflow, cursor mapping is based on logical cells so a
/// width change before the cursor cannot shift it onto the wrong character.
/// </summary>
internal static class TerminalWidthReflowCalculator
{
    public static List<TerminalLine> ReflowLines(
        List<TerminalLine> source,
        int targetColumns,
        int cursorSourceRow,
        int cursorSourceColumn,
        bool cursorWrapPending,
        out int cursorTargetRow,
        out int cursorTargetColumn,
        out bool cursorTargetWrapPending,
        int savedCursorSourceRow,
        int savedCursorSourceColumn,
        bool savedCursorWrapPending,
        out int savedCursorTargetRow,
        out int savedCursorTargetColumn,
        out bool savedCursorTargetWrapPending,
        bool ambiguousAsWide)
    {
        var result = new List<TerminalLine>();
        cursorTargetRow = cursorTargetColumn = savedCursorTargetRow = savedCursorTargetColumn = 0;
        cursorTargetWrapPending = savedCursorTargetWrapPending = false;
        int sourceRow = 0;

        while (sourceRow < source.Count)
        {
            int logicalStart = sourceRow;
            var logicalCells = new List<TerminalCell>();
            var images = new List<(int Offset, TerminalImage Image)>();
            do
            {
                TerminalLine line = source[sourceRow];
                int length = line.IsWrapped ? line.Cells.Length : FindLastOccupiedColumn(line) + 1;
                // Offsets here count graphemes, since continuation cells are dropped and rebuilt.
                TerminalReflowImages.CollectByGrapheme(line, logicalCells.Count, length, images);
                for (int column = 0; column < length; column++)
                {
                    TerminalCell cell = line.Cells[column];
                    if (cell.IsContinuation)
                    {
                        continue;
                    }

                    int width = TerminalWidthCalculator.EstimateGraphemeWidth(
                        cell.Text.AsSpan(),
                        ambiguousAsWide);
                    logicalCells.Add(cell with { Width = width });
                }

                sourceRow++;
                if (!line.IsWrapped)
                {
                    break;
                }
            }
            while (sourceRow < source.Count);

            int logicalEnd = sourceRow - 1;
            int outputStart = result.Count;
            TerminalLineSize lineSize = source[logicalStart].LineSize;
            if (logicalCells.Count == 0)
            {
                var line = new TerminalLine(targetColumns, TerminalStyle.Default) { LineSize = lineSize };
                TerminalReflowImages.PlaceRemaining(line, images, 0, targetColumns);
                result.Add(line);
            }
            else
            {
                int offset = 0;
                while (offset < logicalCells.Count)
                {
                    var line = new TerminalLine(targetColumns, TerminalStyle.Default) { LineSize = lineSize };
                    int column = 0;
                    while (offset < logicalCells.Count && column < targetColumns)
                    {
                        TerminalCell cell = logicalCells[offset];
                        int width = Math.Clamp(cell.Width, 1, 2);
                        if (width == 2 && column == targetColumns - 1)
                        {
                            break;
                        }

                        if (images.Count > 0)
                        {
                            TerminalReflowImages.PlaceAt(line, images, offset, column, targetColumns);
                        }

                        line.Cells[column++] = cell with { Width = width };
                        offset++;
                        if (width == 2)
                        {
                            line.Cells[column++] = new TerminalCell(
                                string.Empty,
                                cell.Style,
                                cell.Hyperlink,
                                IsContinuation: true,
                                Width: 0);
                        }
                    }

                    line.IsWrapped = offset < logicalCells.Count;
                    if (!line.IsWrapped)
                    {
                        // Images anchored past the last occupied cell stay on the closing row.
                        TerminalReflowImages.PlaceRemaining(line, images, column, targetColumns);
                    }

                    result.Add(line);
                }
            }

            MapPosition(
                source,
                logicalStart,
                logicalEnd,
                cursorSourceRow,
                cursorSourceColumn,
                cursorWrapPending,
                logicalCells,
                targetColumns,
                outputStart,
                result.Count - outputStart,
                out int mappedRow,
                out int mappedColumn,
                out bool mappedWrapPending);
            if (cursorSourceRow >= logicalStart && cursorSourceRow <= logicalEnd)
            {
                cursorTargetRow = mappedRow;
                cursorTargetColumn = mappedColumn;
                cursorTargetWrapPending = mappedWrapPending;
            }

            MapPosition(
                source,
                logicalStart,
                logicalEnd,
                savedCursorSourceRow,
                savedCursorSourceColumn,
                savedCursorWrapPending,
                logicalCells,
                targetColumns,
                outputStart,
                result.Count - outputStart,
                out mappedRow,
                out mappedColumn,
                out mappedWrapPending);
            if (savedCursorSourceRow >= logicalStart && savedCursorSourceRow <= logicalEnd)
            {
                savedCursorTargetRow = mappedRow;
                savedCursorTargetColumn = mappedColumn;
                savedCursorTargetWrapPending = mappedWrapPending;
            }
        }

        return result;
    }

    private static void MapPosition(
        List<TerminalLine> source,
        int logicalStart,
        int logicalEnd,
        int positionRow,
        int positionColumn,
        bool wrapPending,
        List<TerminalCell> logicalCells,
        int targetColumns,
        int outputStart,
        int outputCount,
        out int targetRow,
        out int targetColumn,
        out bool targetWrapPending)
    {
        int logicalCellOffset = 0;
        for (int row = logicalStart; row <= logicalEnd; row++)
        {
            TerminalLine line = source[row];
            int length = line.IsWrapped ? line.Cells.Length : FindLastOccupiedColumn(line) + 1;
            for (int column = 0; column < length; column++)
            {
                if (line.Cells[column].IsContinuation)
                {
                    continue;
                }

                bool beforePosition = row < positionRow ||
                    (row == positionRow && (column < positionColumn ||
                        (wrapPending && column <= positionColumn)));
                if (beforePosition)
                {
                    logicalCellOffset++;
                }
            }
        }

        logicalCellOffset = Math.Clamp(logicalCellOffset, 0, logicalCells.Count);
        int rowOffset = 0;
        int targetCellColumn = 0;
        for (int index = 0; index < logicalCellOffset; index++)
        {
            int width = Math.Clamp(logicalCells[index].Width, 1, 2);
            if (width == 2 && targetCellColumn == targetColumns - 1)
            {
                rowOffset++;
                targetCellColumn = 0;
            }

            targetCellColumn += width;
            if (targetCellColumn >= targetColumns)
            {
                rowOffset++;
                targetCellColumn = 0;
            }
        }

        targetWrapPending = logicalCellOffset >= logicalCells.Count &&
            rowOffset >= outputCount;
        if (targetWrapPending)
        {
            targetRow = outputStart + outputCount - 1;
            targetColumn = targetColumns - 1;
            return;
        }

        targetRow = outputStart + Math.Min(rowOffset, outputCount - 1);
        targetColumn = Math.Min(targetCellColumn, targetColumns - 1);
    }

    private static int FindLastOccupiedColumn(TerminalLine line)
    {
        for (int column = line.Cells.Length - 1; column >= 0; column--)
        {
            if (!string.IsNullOrEmpty(line.Cells[column].Text) && line.Cells[column].Text != " ")
            {
                return column;
            }
        }

        return -1;
    }
}
