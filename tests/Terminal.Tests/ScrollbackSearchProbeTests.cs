using System.Linq;

using Terminal.Buffer;

namespace Terminal.Tests;

// 回帰テスト：実バッファ（AnsiTerminalBuffer）に画面行数を超える出力を流したとき、
// CreateRenderSnapshot がスクロールバック（画面外）行を含むこと＝検索が画面外も対象にできることを保証する。
// （ホストの「ターミナル内検索」は FindMatches で _lines 全行を走査するため、画面外行が
//  スナップショットに含まれていることが画面外検索の前提になる。）
public sealed class ScrollbackSearchProbeTests
{
    [Fact]
    public void RealBuffer_Snapshot_IncludesOffscreenScrollbackLines()
    {
        var buffer = new AnsiTerminalBuffer(columns: 80, rows: 24);

        for (int i = 0; i < 100; i++)
        {
            buffer.Process($"line-{i:D5} content\r\n");
        }

        var snapshot = buffer.CreateRenderSnapshot(showCursor: false);

        var allText = snapshot.Lines
            .Select(l => string.Concat(l.Segments.Select(s => s.Text)))
            .ToArray();

        // 画面は24行。0..75行目は画面外（スクロールバック）に押し出されているはず。
        bool hasEarly = allText.Any(t => t.Contains("line-00001"));
        bool hasLate = allText.Any(t => t.Contains("line-00099"));

        Assert.True(
            snapshot.Lines.Length >= 90,
            $"snapshot line count = {snapshot.Lines.Length} (ScrollbackLineCount={buffer.ScrollbackLineCount})");
        Assert.True(hasLate, "late (visible) line missing");
        Assert.True(hasEarly, $"early off-screen line missing. lineCount={snapshot.Lines.Length}, scrollback={buffer.ScrollbackLineCount}");
    }
}
