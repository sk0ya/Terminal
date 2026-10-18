using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Terminal.Tabs;

/// <summary>
/// 行が出た時刻。スクロールバックの各行は最初に文字が書かれた時刻を持っていて（tmux 3.8 の
/// <c>capture-pane -I</c> と同じ考え方）、ホストはそれをマウスを載せた行の吹き出しで見せたり、
/// 時刻からその頃の出力へ飛んだりできる。常駐ホストからの再接続でも時刻は画面と一緒に戻る。
/// </summary>
public partial class TerminalTabView
{
    private bool _showLineTimeOnHover;
    private int _lineTimeToolTipLine = -1;

    /// <summary>
    /// true のとき、行にマウスを載せるとその行が出た時刻を吹き出しで出す（既定は false）。
    /// 時刻を持たない行（まだ書かれていない・時刻を知らない古い再生）では出さない。
    /// </summary>
    public bool ShowLineTimeOnHover
    {
        get => _showLineTimeOnHover;
        set
        {
            if (_showLineTimeOnHover == value)
            {
                return;
            }

            _showLineTimeOnHover = value;
            if (value)
            {
                TerminalOutput.MouseMove += TerminalOutput_LineTimeMouseMove;
                TerminalOutput.MouseLeave += TerminalOutput_LineTimeMouseLeave;
            }
            else
            {
                TerminalOutput.MouseMove -= TerminalOutput_LineTimeMouseMove;
                TerminalOutput.MouseLeave -= TerminalOutput_LineTimeMouseLeave;
                ClearLineTimeToolTip();
            }
        }
    }

    /// <summary>画面上の点にある行が出た時刻（UTC）。行が無い・時刻を持たないなら null。</summary>
    public DateTime? GetLineTimeAt(Point pointInTerminal)
    {
        if (!TerminalOutput.TryGetTextPositionFromPoint(pointInTerminal, out int displayLine, out _))
        {
            return null;
        }

        long ticks = _terminalBuffer.GetLineWrittenAtTicks(DisplayToBufferLine(displayLine));
        return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary>
    /// その時刻以降に出た最初の行を、画面の上端へ出す（その頃の出力へ飛ぶ）。追従は止める。
    /// その時刻以降の行が無ければ何もしないで false。
    /// </summary>
    public bool ScrollToTime(DateTime time)
    {
        if (_terminalBuffer.FindFirstLineWrittenAtOrAfter(time.ToUniversalTime().Ticks) is not { } line)
        {
            return false;
        }

        ScrollToAbsoluteLine(line);
        return true;
    }

    /// <summary>吹き出しの文（「14:03:21 に出力（3 分前）」）。テストから見るために分けてある。</summary>
    internal static string FormatLineTime(DateTime writtenUtc, DateTime nowUtc)
    {
        DateTime local = writtenUtc.ToLocalTime();
        string clock = local.Date == nowUtc.ToLocalTime().Date
            ? local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : local.ToString("M/d HH:mm:ss", CultureInfo.InvariantCulture);
        TimeSpan ago = nowUtc - writtenUtc;
        string relative = ago.TotalSeconds < 60 ? "たった今"
            : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes} 分前"
            : ago.TotalHours < 24 ? $"{(int)ago.TotalHours} 時間前"
            : $"{(int)ago.TotalDays} 日前";
        return $"{clock} に出力（{relative}）";
    }

    private void TerminalOutput_LineTimeMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed || !TerminalOutput.TryGetTextPositionFromPoint(e.GetPosition(TerminalOutput), out int displayLine, out _))
        {
            ClearLineTimeToolTip();
            return;
        }

        int bufferLine = DisplayToBufferLine(displayLine);
        if (bufferLine == _lineTimeToolTipLine)
        {
            return;
        }

        _lineTimeToolTipLine = bufferLine;
        long ticks = _terminalBuffer.GetLineWrittenAtTicks(bufferLine);
        if (ticks == 0)
        {
            TerminalOutput.ToolTip = null;
            return;
        }

        // 行を移るたびに出し直す（同じ吹き出しの中身だけ変えると、閉じたまま次の行の時刻が出ない）。
        var tip = new ToolTip { Content = FormatLineTime(new DateTime(ticks, DateTimeKind.Utc), DateTime.UtcNow) };
        ToolTipService.SetInitialShowDelay(TerminalOutput, 600);
        TerminalOutput.ToolTip = tip;
    }

    private void TerminalOutput_LineTimeMouseLeave(object sender, MouseEventArgs e) => ClearLineTimeToolTip();

    private void ClearLineTimeToolTip()
    {
        _lineTimeToolTipLine = -1;
        if (TerminalOutput.ToolTip is not null)
        {
            TerminalOutput.ToolTip = null;
        }
    }
}
