using System.Globalization;
using System.Text;
using System.Windows.Media;

namespace Terminal.Rendering;

/// <summary>
/// 画面選択から取り出した装飾付きの 1 ラン（同一スタイルの連続文字列）。前景/背景色は
/// レンダラが解決した実 RGB（ANSI インデックス・default・反転を解決済み）を保持する。
/// </summary>
internal readonly record struct StyledRun(
    string Text,
    Color Foreground,
    Color Background,
    bool Bold,
    bool Italic,
    bool Underline);

/// <summary>
/// 画面選択を「行ごとの装飾付きラン列」として表した中間表現。ここから CF_HTML / RTF /
/// プレーンテキストを生成する。<see cref="Background"/>/<see cref="Foreground"/> は
/// コンテナ（<c>&lt;pre&gt;</c> や RTF 全体）の既定色に使う。
/// </summary>
internal sealed class StyledSelection(
    IReadOnlyList<IReadOnlyList<StyledRun>> lines,
    Color foreground,
    Color background)
{
    public IReadOnlyList<IReadOnlyList<StyledRun>> Lines { get; } = lines;

    public Color Foreground { get; } = foreground;

    public Color Background { get; } = background;
}

/// <summary>
/// 装飾付き選択（<see cref="StyledSelection"/>）を CF_HTML・RTF・プレーンテキストの各文字列へ
/// 変換する純粋関数群。クリップボードへ書き込む文字列だけを組み立て、副作用は持たない
/// （テスト可能に切り出してある）。
/// </summary>
internal static class ColoredClipboardWriter
{
    // CF_HTML ヘッダ。オフセットは常に 10 桁ゼロ詰めで書き、ヘッダ長がオフセット値に依らず
    // 一定になるようにする（相互再帰を避けるための定石）。
    private const string HtmlHeaderFormat =
        "Version:0.9\r\n" +
        "StartHTML:{0:D10}\r\n" +
        "EndHTML:{1:D10}\r\n" +
        "StartFragment:{2:D10}\r\n" +
        "EndFragment:{3:D10}\r\n";

    private const string HtmlPrefix = "<html>\r\n<body>\r\n<!--StartFragment-->";
    private const string HtmlSuffix = "<!--EndFragment-->\r\n</body>\r\n</html>";

    /// <summary>選択を CF_HTML（クリップボード用の必須ヘッダ付き HTML）へ変換する。</summary>
    public static string BuildHtml(StyledSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        string fragment = BuildHtmlFragment(selection);

        // ヘッダ長はダミー値でも実値でも 10 桁固定なので一定。各オフセットは UTF-8 の
        // バイト位置で数える（CF_HTML はバイトオフセット規定）。
        int headerBytes = Encoding.UTF8.GetByteCount(
            string.Format(CultureInfo.InvariantCulture, HtmlHeaderFormat, 0, 0, 0, 0));

        int prefixBytes = Encoding.UTF8.GetByteCount(HtmlPrefix);
        int fragmentBytes = Encoding.UTF8.GetByteCount(fragment);
        int suffixBytes = Encoding.UTF8.GetByteCount(HtmlSuffix);

        int startHtml = headerBytes;
        int startFragment = headerBytes + prefixBytes;
        int endFragment = startFragment + fragmentBytes;
        int endHtml = headerBytes + prefixBytes + fragmentBytes + suffixBytes;

        string header = string.Format(
            CultureInfo.InvariantCulture,
            HtmlHeaderFormat,
            startHtml,
            endHtml,
            startFragment,
            endFragment);

        return header + HtmlPrefix + fragment + HtmlSuffix;
    }

    private static string BuildHtmlFragment(StyledSelection selection)
    {
        var builder = new StringBuilder();
        builder.Append("<pre style=\"margin:0;color:")
            .Append(ToHtmlColor(selection.Foreground))
            .Append(";background-color:")
            .Append(ToHtmlColor(selection.Background))
            .Append(";font-family:monospace;\">");

        for (int lineIndex = 0; lineIndex < selection.Lines.Count; lineIndex++)
        {
            if (lineIndex > 0)
            {
                // <pre> 内では改行文字がそのまま改行として描画される。
                builder.Append('\n');
            }

            foreach (StyledRun run in selection.Lines[lineIndex])
            {
                if (run.Text.Length == 0)
                {
                    continue;
                }

                builder.Append("<span style=\"color:")
                    .Append(ToHtmlColor(run.Foreground))
                    .Append(";background-color:")
                    .Append(ToHtmlColor(run.Background));
                if (run.Bold)
                {
                    builder.Append(";font-weight:bold");
                }

                if (run.Italic)
                {
                    builder.Append(";font-style:italic");
                }

                if (run.Underline)
                {
                    builder.Append(";text-decoration:underline");
                }

                builder.Append("\">");
                AppendHtmlEscaped(builder, run.Text);
                builder.Append("</span>");
            }
        }

        builder.Append("</pre>");
        return builder.ToString();
    }

    private static void AppendHtmlEscaped(StringBuilder builder, string text)
    {
        foreach (char ch in text)
        {
            switch (ch)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }
    }

    private static string ToHtmlColor(Color color)
        => string.Create(CultureInfo.InvariantCulture, $"#{color.R:x2}{color.G:x2}{color.B:x2}");

    /// <summary>選択を RTF（カラーテーブル付き）へ変換する。</summary>
    public static string BuildRtf(StyledSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        // 使用する全色を重複なく採番する。RTF のカラーテーブルはインデックス 0 が
        // 「自動」（先頭のセミコロン）なので、実際の色は 1 番から割り当てる。
        var colorIndices = new Dictionary<Color, int>();
        int NextColor(Color color)
        {
            if (!colorIndices.TryGetValue(color, out int index))
            {
                index = colorIndices.Count + 1;
                colorIndices[color] = index;
            }

            return index;
        }

        var body = new StringBuilder();
        for (int lineIndex = 0; lineIndex < selection.Lines.Count; lineIndex++)
        {
            if (lineIndex > 0)
            {
                body.Append("\\par\r\n");
            }

            foreach (StyledRun run in selection.Lines[lineIndex])
            {
                if (run.Text.Length == 0)
                {
                    continue;
                }

                int foreground = NextColor(run.Foreground);
                int background = NextColor(run.Background);

                // ラングループ内でのみ属性が効くよう {} で囲う。
                body.Append("{\\cf").Append(foreground).Append("\\cb").Append(background);
                if (run.Bold)
                {
                    body.Append("\\b");
                }

                if (run.Italic)
                {
                    body.Append("\\i");
                }

                if (run.Underline)
                {
                    body.Append("\\ul");
                }

                body.Append(' ');
                AppendRtfEscaped(body, run.Text);
                body.Append('}');
            }
        }

        var colorTable = new StringBuilder("{\\colortbl;");
        foreach (Color color in colorIndices.OrderBy(pair => pair.Value).Select(pair => pair.Key))
        {
            colorTable.Append("\\red").Append(color.R)
                .Append("\\green").Append(color.G)
                .Append("\\blue").Append(color.B)
                .Append(';');
        }

        colorTable.Append('}');

        return "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0\\fmodern Consolas;}}" +
            colorTable + "\r\n\\f0 " + body + "}";
    }

    private static void AppendRtfEscaped(StringBuilder builder, string text)
    {
        foreach (char ch in text)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '{':
                    builder.Append("\\{");
                    break;
                case '}':
                    builder.Append("\\}");
                    break;
                default:
                    if (ch < 0x80)
                    {
                        builder.Append(ch);
                    }
                    else
                    {
                        // 非 ASCII は \uN? で表す。N は符号付き 16bit（UTF-16 コードユニット）、
                        // 続く '?' は Unicode 非対応リーダー向けのフォールバック文字。
                        int code = ch;
                        if (code > 32767)
                        {
                            code -= 65536;
                        }

                        builder.Append("\\u").Append(code).Append('?');
                    }

                    break;
            }
        }
    }

    /// <summary>選択のプレーンテキスト表現（行を CRLF で連結）を返す。</summary>
    public static string BuildPlainText(StyledSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var builder = new StringBuilder();
        for (int lineIndex = 0; lineIndex < selection.Lines.Count; lineIndex++)
        {
            if (lineIndex > 0)
            {
                builder.Append("\r\n");
            }

            foreach (StyledRun run in selection.Lines[lineIndex])
            {
                builder.Append(run.Text);
            }
        }

        return builder.ToString();
    }
}
