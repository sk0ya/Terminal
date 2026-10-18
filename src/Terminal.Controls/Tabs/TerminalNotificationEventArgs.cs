namespace Terminal.Tabs;

/// <summary>
/// <see cref="TerminalTabView.NotificationReceived"/> の引数。題と本文は改行・制御文字を空白にして長さを切ってある。
/// </summary>
public sealed class TerminalNotificationEventArgs(string? title, string body) : EventArgs
{
    /// <summary>題（OSC 777 だけが持つ。OSC 9 は null）。</summary>
    public string? Title { get; } = title;

    /// <summary>本文。</summary>
    public string Body { get; } = body;

    /// <summary>true にするとタブの中のトーストを出さない（ホストが自分で知らせるとき）。</summary>
    public bool Handled { get; set; }
}
