namespace Terminal.Buffer;

/// <summary>
/// アプリケーションが求めたデスクトップ通知（OSC 9 の本文・OSC 777;notify;題;本文）。
/// 題と本文は外から来る文字列なので、改行・制御文字は空白にして長さを切ってある。
/// </summary>
public sealed record TerminalDesktopNotification(string? Title, string Body)
{
    /// <summary>題・本文それぞれの上限（知らせの 1 行に収める長さ）。</summary>
    public const int MaxLength = 300;

    /// <summary>1 行で見せる文（題があれば「題: 本文」）。</summary>
    public string DisplayText => string.IsNullOrEmpty(Title) ? Body : string.IsNullOrEmpty(Body) ? Title : $"{Title}: {Body}";

    /// <summary>題と本文を整えて作る。どちらも空なら null。</summary>
    public static TerminalDesktopNotification? Create(string? title, string? body)
    {
        var cleanTitle = Clean(title);
        var cleanBody = Clean(body);
        if (cleanTitle.Length == 0 && cleanBody.Length == 0)
        {
            return null;
        }

        return new TerminalDesktopNotification(cleanTitle.Length == 0 ? null : cleanTitle, cleanBody);
    }

    /// <summary>OSC 777 の値（<c>notify;題;本文</c>）。本文の中の <c>;</c> は本文のまま残す。notify 以外は null。</summary>
    public static TerminalDesktopNotification? TryParseOsc777(string value)
    {
        var parts = value.Split(';', 3);
        if (parts.Length < 2 || !string.Equals(parts[0], "notify", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Create(parts[1], parts.Length > 2 ? parts[2] : null);
    }

    private static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var chars = text.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        var clean = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return clean.Length <= MaxLength ? clean : clean[..(MaxLength - 1)] + "…";
    }
}
