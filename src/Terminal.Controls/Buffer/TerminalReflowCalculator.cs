namespace Terminal.Buffer;

internal static class TerminalReflowCalculator
{
    public static List<TerminalLine> ReflowLines(
        List<TerminalLine> source,
        int targetColumns,
        int cursorSourceRow,
        int cursorSourceColumn,
        out int cursorTargetRow,
        out int cursorTargetColumn,
        int savedCursorSourceRow,
        int savedCursorSourceColumn,
        out int savedCursorTargetRow,
        out int savedCursorTargetColumn)
    {
        var result = new List<TerminalLine>();
        cursorTargetRow = cursorTargetColumn = savedCursorTargetRow = savedCursorTargetColumn = 0;
        int sourceRow = 0;
        while (sourceRow < source.Count)
        {
            int logicalStart = sourceRow;
            var cells = new List<TerminalCell>();
            do
            {
                TerminalLine line = source[sourceRow];
                int length = line.IsWrapped ? line.Cells.Length : FindLastOccupiedColumn(line) + 1;
                for (int column = 0; column < length; column++)
                {
                    cells.Add(line.Cells[column]);
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
            if (cells.Count == 0)
            {
                result.Add(new TerminalLine(targetColumns, TerminalStyle.Default));
            }
            else
            {
                int offset = 0;
                while (offset < cells.Count)
                {
                    var line = new TerminalLine(targetColumns, TerminalStyle.Default);
                    int column = 0;
                    while (offset < cells.Count && column < targetColumns)
                    {
                        TerminalCell cell = cells[offset];
                        int width = cell.IsContinuation ? 1 : Math.Max(1, cell.Width);
                        if (!cell.IsContinuation && width == 2 && column == targetColumns - 1)
                        {
                            break;
                        }

                        line.Cells[column++] = cell;
                        offset++;
                        if (width == 2 && offset < cells.Count && cells[offset].IsContinuation)
                        {
                            line.Cells[column++] = cells[offset++];
                        }
                    }

                    line.IsWrapped = offset < cells.Count;
                    result.Add(line);
                }
            }

            MapReflowedPosition(source, logicalStart, logicalEnd, cursorSourceRow, cursorSourceColumn,
                targetColumns, outputStart, result.Count - outputStart, out int mappedRow, out int mappedColumn);
            if (cursorSourceRow >= logicalStart && cursorSourceRow <= logicalEnd)
            {
                cursorTargetRow = mappedRow;
                cursorTargetColumn = mappedColumn;
            }

            MapReflowedPosition(source, logicalStart, logicalEnd, savedCursorSourceRow, savedCursorSourceColumn,
                targetColumns, outputStart, result.Count - outputStart, out mappedRow, out mappedColumn);
            if (savedCursorSourceRow >= logicalStart && savedCursorSourceRow <= logicalEnd)
            {
                savedCursorTargetRow = mappedRow;
                savedCursorTargetColumn = mappedColumn;
            }
        }

        return result;
    }

    private static void MapReflowedPosition(List<TerminalLine> source, int logicalStart, int logicalEnd,
        int positionRow, int positionColumn, int targetColumns, int outputStart, int outputCount,
        out int targetRow, out int targetColumn)
    {
        var cells = new List<TerminalCell>();
        int positionOffset = 0;
        int clampedPositionRow = Math.Clamp(positionRow, logicalStart, logicalEnd);
        for (int row = logicalStart; row <= logicalEnd; row++)
        {
            TerminalLine line = source[row];
            int length = line.IsWrapped ? line.Cells.Length : FindLastOccupiedColumn(line) + 1;
            if (row < clampedPositionRow)
            {
                positionOffset += length;
            }

            for (int column = 0; column < length; column++)
            {
                cells.Add(line.Cells[column]);
            }
        }

        positionOffset += Math.Max(0, positionColumn);
        int rowOffset = 0;
        int targetCellColumn = 0;
        for (int offset = 0; offset < Math.Min(positionOffset, cells.Count); offset++)
        {
            TerminalCell cell = cells[offset];
            if (!cell.IsContinuation && cell.Width == 2 && targetCellColumn == targetColumns - 1)
            {
                rowOffset++;
                targetCellColumn = 0;
            }

            targetCellColumn++;
            if (targetCellColumn >= targetColumns)
            {
                rowOffset++;
                targetCellColumn = 0;
            }
        }

        targetRow = outputStart + Math.Min(rowOffset, outputCount - 1);
        targetColumn = Math.Min(targetCellColumn, targetColumns - 1);
    }

    public static List<TerminalLine> ResizeScreenBuffer(
        List<TerminalLine> sourceScreen,
        int sourceRows,
        int sourceColumns,
        int targetRows,
        int targetColumns,
        bool preserveBottomRows)
    {
        var resizedScreen = CreateScreen(targetRows, targetColumns);
        int copyRows = Math.Min(sourceRows, targetRows);
        int copyColumns = Math.Min(sourceColumns, targetColumns);
        int sourceStartRow = preserveBottomRows && targetRows < sourceRows
            ? sourceRows - targetRows
            : 0;
        int targetStartRow = preserveBottomRows && targetRows > sourceRows
            ? targetRows - sourceRows
            : 0;

        for (int row = 0; row < copyRows; row++)
        {
            TerminalLine source = sourceScreen[sourceStartRow + row];
            TerminalLine target = resizedScreen[targetStartRow + row];
            Array.Copy(source.Cells, 0, target.Cells, 0, copyColumns);
            SanitizeRightEdge(source, target, copyColumns, sourceColumns);
        }

        return resizedScreen;
    }

    private static void SanitizeRightEdge(TerminalLine source, TerminalLine target, int copiedColumns, int sourceColumns)
    {
        if (copiedColumns <= 0 || copiedColumns >= sourceColumns)
        {
            return;
        }

        if (source.Cells[copiedColumns].IsContinuation)
        {
            int lastCopiedColumn = copiedColumns - 1;
            target.Cells[lastCopiedColumn] = TerminalCell.CreateBlank(TerminalStyle.Default);
        }
    }

    private static List<TerminalLine> CreateScreen(int rows, int columns)
    {
        var screen = new List<TerminalLine>(rows);
        for (int row = 0; row < rows; row++)
        {
            screen.Add(new TerminalLine(columns, TerminalStyle.Default));
        }

        return screen;
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
