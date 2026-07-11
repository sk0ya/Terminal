namespace Terminal;

internal static class TerminalTabCollectionState
{
    internal static int GetSelectionAfterClose(int closedIndex, int remainingCount, bool wasSelected)
        => !wasSelected || remainingCount <= 0 ? -1 : Math.Clamp(closedIndex, 0, remainingCount - 1);

    internal static int MoveSelection(int currentIndex, int count, int delta)
        => count <= 0 ? -1 : (Math.Max(0, currentIndex) + delta % count + count) % count;

    internal static bool MoveItem<T>(IList<T> items, int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= items.Count || toIndex < 0 || toIndex >= items.Count || fromIndex == toIndex) return false;
        T item = items[fromIndex]; items.RemoveAt(fromIndex); items.Insert(toIndex, item); return true;
    }
}
