namespace Terminal.Tabs;

/// <summary>
/// 画面内テキスト検索バー（Ctrl+Shift+F）の巡回ロジックと現在位置表示を担う純粋関数群。
/// UI やレンダリングに依存しないため単体テスト可能。<see cref="TerminalMatch"/> の一覧
/// （行頭→行末・上から下に整列済み）に対して「次/前」インデックスの算出、初期一致の選定、
/// 「3/17」形式の位置文字列生成を行う。
/// </summary>
public static class TerminalFindNavigator
{
    /// <summary>
    /// 現在のインデックスから「次（<paramref name="forward"/>=true）」または「前」の一致インデックスを
    /// 巡回（末尾↔先頭でラップ）して返す。<paramref name="count"/> が 0 以下なら -1。
    /// 現在インデックスが未確定（負値）のときは順方向で先頭、逆方向で末尾を返す。
    /// </summary>
    public static int Advance(int currentIndex, int count, bool forward)
    {
        if (count <= 0)
        {
            return -1;
        }

        if (currentIndex < 0)
        {
            return forward ? 0 : count - 1;
        }

        int next = forward ? currentIndex + 1 : currentIndex - 1;
        return ((next % count) + count) % count;
    }

    /// <summary>
    /// 指定位置（<paramref name="fromLine"/>, <paramref name="fromColumn"/>）を起点に、順方向なら
    /// その位置以降で最初、逆方向ならその位置以前で最後の一致インデックスを返す。該当が無ければ
    /// 末尾↔先頭にラップする。一覧が空なら -1。検索語入力時に「今見ている位置に近い一致」を
    /// 現在一致として選ぶために用いる。
    /// </summary>
    public static int SeedIndex(IReadOnlyList<TerminalMatch> matches, int fromLine, int fromColumn, bool forward)
    {
        if (matches is null || matches.Count == 0)
        {
            return -1;
        }

        if (forward)
        {
            for (int i = 0; i < matches.Count; i++)
            {
                TerminalMatch m = matches[i];
                if (m.LineIndex > fromLine || (m.LineIndex == fromLine && m.Column >= fromColumn))
                {
                    return i;
                }
            }

            return 0;
        }

        for (int i = matches.Count - 1; i >= 0; i--)
        {
            TerminalMatch m = matches[i];
            if (m.LineIndex < fromLine || (m.LineIndex == fromLine && m.Column <= fromColumn))
            {
                return i;
            }
        }

        return matches.Count - 1;
    }

    /// <summary>
    /// 現在位置の表示文字列を「現在/総数」（1 始まり）で生成する。例: index=2, count=17 → "3/17"。
    /// 一致が無ければ "0/0"。<paramref name="currentIndex"/> は範囲内にクランプする。
    /// </summary>
    public static string FormatPosition(int currentIndex, int count)
    {
        if (count <= 0)
        {
            return "0/0";
        }

        int shown = Math.Clamp(currentIndex, 0, count - 1) + 1;
        return $"{shown}/{count}";
    }
}
