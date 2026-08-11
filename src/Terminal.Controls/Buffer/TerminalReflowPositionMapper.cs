namespace Terminal.Buffer;

/// <summary>
/// Maps a cursor position inside a logical wrapped line to its position after reflow.
/// This is kept separate from cell copying so position behavior can be tested independently.
/// </summary>
internal static class TerminalReflowPositionMapper
{
    public static void Map(
        List<TerminalLine> source,
        int logicalStart,
        int logicalEnd,
        int positionRow,
        int positionColumn,
        int targetColumns,
        int outputStart,
        int outputCount,
        out int targetRow,
        out int targetColumn)
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
