namespace Terminal.Buffer;

internal static class TerminalReflowCalculator
{
    /// <summary>論理行（折り返しでつながった行）のうち、最初に時刻を持っている行の時刻。無ければ 0。</summary>
    internal static long FirstWrittenAt(IReadOnlyList<TerminalLine> source, int start, int end)
    {
        for (int row = start; row <= end && row < source.Count; row++)
        {
            if (source[row].WrittenAtUtcTicks != 0)
            {
                return source[row].WrittenAtUtcTicks;
            }
        }

        return 0;
    }

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
        return ReflowLinesWithWrapState(
            source,
            targetColumns,
            cursorSourceRow,
            cursorSourceColumn,
            out cursorTargetRow,
            out cursorTargetColumn,
            savedCursorSourceRow,
            savedCursorSourceColumn,
            out savedCursorTargetRow,
            out savedCursorTargetColumn,
            out _,
            out _);
    }

    public static List<TerminalLine> ReflowLinesWithWrapState(
        List<TerminalLine> source,
        int targetColumns,
        int cursorSourceRow,
        int cursorSourceColumn,
        out int cursorTargetRow,
        out int cursorTargetColumn,
        int savedCursorSourceRow,
        int savedCursorSourceColumn,
        out int savedCursorTargetRow,
        out int savedCursorTargetColumn,
        out bool cursorTargetWrapPending,
        out bool savedCursorTargetWrapPending)
    {
        var result = new List<TerminalLine>();
        cursorTargetRow = cursorTargetColumn = savedCursorTargetRow = savedCursorTargetColumn = 0;
        cursorTargetWrapPending = savedCursorTargetWrapPending = false;
        int sourceRow = 0;
        while (sourceRow < source.Count)
        {
            int logicalStart = sourceRow;
            var cells = new List<TerminalCell>();
            var images = new List<(int Offset, TerminalImage Image)>();
            do
            {
                TerminalLine line = source[sourceRow];
                bool continues = ContinuesOnNextRow(line);
                int length = continues ? line.Cells.Length : FindLastOccupiedColumn(line) + 1;
                // Anchor each image at its offset from the start of the logical line, so it can be
                // remapped onto whichever target row that cell ends up on.
                TerminalReflowImages.Collect(line, cells.Count, images);
                for (int column = 0; column < length; column++)
                {
                    cells.Add(line.Cells[column]);
                }

                sourceRow++;
                if (!continues)
                {
                    break;
                }
            }
            while (sourceRow < source.Count);

            int logicalEnd = sourceRow - 1;
            int outputStart = result.Count;
            TerminalLineSize lineSize = source[logicalStart].LineSize;
            long writtenAt = FirstWrittenAt(source, logicalStart, logicalEnd);
            if (cells.Count == 0)
            {
                var line = new TerminalLine(targetColumns, TerminalStyle.Default) { LineSize = lineSize, WrittenAtUtcTicks = writtenAt };
                TerminalReflowImages.PlaceRemaining(line, images, 0, targetColumns);
                result.Add(line);
            }
            else
            {
                int offset = 0;
                while (offset < cells.Count)
                {
                    var line = new TerminalLine(targetColumns, TerminalStyle.Default) { LineSize = lineSize, WrittenAtUtcTicks = writtenAt };
                    int lineStartOffset = offset;
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
                    TerminalReflowImages.Place(line, images, lineStartOffset, offset, targetColumns);
                    if (!line.IsWrapped)
                    {
                        // Images anchored past the last occupied cell have no target column of their
                        // own; keep them on the closing row rather than dropping them.
                        TerminalReflowImages.PlaceRemaining(line, images, offset - lineStartOffset, targetColumns);
                    }

                    result.Add(line);
                }
            }

            TerminalReflowPositionMapper.Map(source, logicalStart, logicalEnd, cursorSourceRow, cursorSourceColumn,
                targetColumns, outputStart, result.Count - outputStart, out int mappedRow, out int mappedColumn,
                out bool mappedWrapPending);
            if (cursorSourceRow >= logicalStart && cursorSourceRow <= logicalEnd)
            {
                cursorTargetRow = mappedRow;
                cursorTargetColumn = mappedColumn;
                cursorTargetWrapPending = mappedWrapPending;
            }

            TerminalReflowPositionMapper.Map(source, logicalStart, logicalEnd, savedCursorSourceRow, savedCursorSourceColumn,
                targetColumns, outputStart, result.Count - outputStart, out mappedRow, out mappedColumn,
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
            target.LineSize = source.LineSize;
            TerminalReflowImages.CopyClamped(source, target, targetColumns);
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

    /// <summary>
    /// この行が次の行へ文字として続いているか。折り返しの印があっても、行末が既定の空白なら続いていないと見る。
    /// PSReadLine は入力候補（灰色の文字）を空白で上書きして消し、その空白が行末を越えると端末は折り返しの印を
    /// 付ける。次の行はその後に位置指定や改行で別の行として書かれるが、印は残るので、幅を変えて折り返し直すと
    /// 「入力 + 空白 + 次の行の出力やプロンプト」が 1 行につながってしまう。本当の文章が行末の空白でちょうど
    /// 折り返していた場合は、そこで改行される（つながりを 1 か所失うだけで、文字は消えない）。
    /// </summary>
    internal static bool ContinuesOnNextRow(TerminalLine line)
    {
        if (!line.IsWrapped || line.Cells.Length == 0)
        {
            return line.IsWrapped;
        }

        TerminalCell last = line.Cells[^1];
        bool blank = !last.IsContinuation && (string.IsNullOrEmpty(last.Text) || last.Text == " ") &&
            last.Style == TerminalStyle.Default && last.Hyperlink is null;
        return !blank;
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
