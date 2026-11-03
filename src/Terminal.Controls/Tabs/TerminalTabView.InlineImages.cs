using Terminal.Buffer;

namespace Terminal.Tabs;

public partial class TerminalTabView
{
    /// <summary>
    /// インライン画像（Sixel・iTerm2 の <c>OSC 1337;File=</c>・Kitty Graphics）が画面に置かれたとき、ディスパッチャスレッドで
    /// 発火する。端末の中にはいつもどおり描かれる——これはホストが同じ画像を別の場所で見せ直すための口。
    /// 画面の再生（常駐セッションへの再接続）では発火しない。Kitty の「置き直し」（送ってある画像をもう一度置く）でも発火する
    /// ので、同じ画像を何度も受けうる。
    /// </summary>
    public event EventHandler<TerminalInlineImageEventArgs>? InlineImageReceived;

    private void TerminalBuffer_ImageReceived(object? sender, TerminalImage image)
    {
        if (_session is null || InlineImageReceived is not { } handler)
        {
            return;
        }

        handler(this, new TerminalInlineImageEventArgs(image, DateTime.UtcNow));
    }
}
