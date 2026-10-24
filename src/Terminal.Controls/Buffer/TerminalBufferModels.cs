using System.Windows.Media;

namespace Terminal.Buffer;

internal enum TerminalLineSize
{
    SingleWidth,
    DoubleWidth,
    DoubleHeightTop,
    DoubleHeightBottom
}

internal sealed class TerminalLine
{
    public TerminalLine(int columns, TerminalStyle blankStyle)
    {
        Cells = new TerminalCell[columns];
        for (int index = 0; index < columns; index++)
        {
            Cells[index] = TerminalCell.CreateBlank(blankStyle);
        }
    }

    public TerminalCell[] Cells { get; }
    public List<TerminalImage> Images { get; } = [];
    public bool IsWrapped { get; set; }
    public TerminalLineSize LineSize { get; set; }

    /// <summary>この行に最初に文字が書かれた時刻（UTC の Ticks）。0 は不明（まだ何も書かれていない・古い再生）。
    /// 折り返し直しでは論理行の先頭の行の値を引き継ぐ。</summary>
    public long WrittenAtUtcTicks { get; set; }
}

/// <summary>
/// 画面とスクロールバックの1マス。スクロールバック 10000 行×120 桁なら 120 万個並ぶので、大きさがそのまま
/// ターミナル1本ぶんのメモリになる（以前は書式を WPF の <see cref="Color"/> のまま持って 184 バイト、
/// 1本 200MB を超えていた）。幅は 0〜2 しか取らないので 1 バイトに詰める。
/// </summary>
internal readonly record struct TerminalCell
{
    private readonly byte _width;

    public TerminalCell(string Text, TerminalStyle Style, TerminalHyperlink? Hyperlink, bool IsContinuation, int Width)
    {
        this.Text = Text;
        this.Style = Style;
        this.Hyperlink = Hyperlink;
        this.IsContinuation = IsContinuation;
        this.Width = Width;
    }

    public string Text { get; init; }
    public TerminalStyle Style { get; init; }
    public TerminalHyperlink? Hyperlink { get; init; }
    public bool IsContinuation { get; init; }

    public int Width
    {
        get => _width;
        init => _width = (byte)Math.Clamp(value, 0, byte.MaxValue);
    }

    public static TerminalCell CreateBlank(TerminalStyle style) =>
        new(" ", style, Hyperlink: null, IsContinuation: false, Width: 1);
}

/// <summary>
/// マスの書式。色は ARGB を <see cref="uint"/> に、真偽値はビットに詰めて 16 バイトで持つ。
/// WPF の <see cref="Color"/> は scRGB の float と ColorContext まで抱えて 1 つ 40 バイトあり、
/// それを 3 つ値で持つと書式だけで 160 バイトになっていた。<see cref="Color"/> は取り出すときに作る
/// （端末の色は sRGB の ARGB しか来ないので、詰めても失うものは無い）。
/// 等価比較は詰めた値どうしで行われ、色が無い（null）ときは詰めた値も 0 に揃えるので、同じ書式は必ず等しい。
/// </summary>
internal readonly record struct TerminalStyle
{
    private const ushort BoldFlag = 1 << 0;
    private const ushort DimFlag = 1 << 1;
    private const ushort ItalicFlag = 1 << 2;
    private const ushort BlinkFlag = 1 << 3;
    private const ushort InverseFlag = 1 << 4;
    private const ushort InvisibleFlag = 1 << 5;
    private const ushort StrikethroughFlag = 1 << 6;
    private const ushort OverlineFlag = 1 << 7;
    private const ushort ProtectedFlag = 1 << 8;
    private const ushort HasForegroundFlag = 1 << 9;
    private const ushort HasBackgroundFlag = 1 << 10;
    private const ushort HasUnderlineColorFlag = 1 << 11;

    private readonly uint _foreground;
    private readonly uint _background;
    private readonly uint _underlineColor;
    private readonly ushort _flags;
    private readonly byte _underlineStyle;

    public static readonly TerminalStyle Default = new(
        null, null, false, false, false, UnderlineStyle.None, null, false, false, false, false, false, false);

    public TerminalStyle(
        Color? Foreground,
        Color? Background,
        bool Bold,
        bool Dim,
        bool Italic,
        UnderlineStyle UnderlineStyle,
        Color? UnderlineColor,
        bool Blink,
        bool Inverse,
        bool Invisible,
        bool Strikethrough,
        bool Overline,
        bool Protected)
    {
        this.Foreground = Foreground;
        this.Background = Background;
        this.Bold = Bold;
        this.Dim = Dim;
        this.Italic = Italic;
        this.UnderlineStyle = UnderlineStyle;
        this.UnderlineColor = UnderlineColor;
        this.Blink = Blink;
        this.Inverse = Inverse;
        this.Invisible = Invisible;
        this.Strikethrough = Strikethrough;
        this.Overline = Overline;
        this.Protected = Protected;
    }

    public Color? Foreground
    {
        get => UnpackColor(_foreground, HasForegroundFlag);
        init
        {
            _foreground = PackColor(value);
            _flags = WithFlag(_flags, HasForegroundFlag, value.HasValue);
        }
    }

    public Color? Background
    {
        get => UnpackColor(_background, HasBackgroundFlag);
        init
        {
            _background = PackColor(value);
            _flags = WithFlag(_flags, HasBackgroundFlag, value.HasValue);
        }
    }

    public Color? UnderlineColor
    {
        get => UnpackColor(_underlineColor, HasUnderlineColorFlag);
        init
        {
            _underlineColor = PackColor(value);
            _flags = WithFlag(_flags, HasUnderlineColorFlag, value.HasValue);
        }
    }

    public UnderlineStyle UnderlineStyle
    {
        get => (UnderlineStyle)_underlineStyle;
        init => _underlineStyle = (byte)value;
    }

    public bool Bold { get => Has(BoldFlag); init => _flags = WithFlag(_flags, BoldFlag, value); }
    public bool Dim { get => Has(DimFlag); init => _flags = WithFlag(_flags, DimFlag, value); }
    public bool Italic { get => Has(ItalicFlag); init => _flags = WithFlag(_flags, ItalicFlag, value); }
    public bool Blink { get => Has(BlinkFlag); init => _flags = WithFlag(_flags, BlinkFlag, value); }
    public bool Inverse { get => Has(InverseFlag); init => _flags = WithFlag(_flags, InverseFlag, value); }
    public bool Invisible { get => Has(InvisibleFlag); init => _flags = WithFlag(_flags, InvisibleFlag, value); }
    public bool Strikethrough { get => Has(StrikethroughFlag); init => _flags = WithFlag(_flags, StrikethroughFlag, value); }
    public bool Overline { get => Has(OverlineFlag); init => _flags = WithFlag(_flags, OverlineFlag, value); }
    public bool Protected { get => Has(ProtectedFlag); init => _flags = WithFlag(_flags, ProtectedFlag, value); }

    private bool Has(ushort flag) => (_flags & flag) != 0;

    private static ushort WithFlag(ushort flags, ushort flag, bool value) =>
        value ? (ushort)(flags | flag) : (ushort)(flags & ~flag);

    private static uint PackColor(Color? color) =>
        color is { } c ? ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B : 0;

    private Color? UnpackColor(uint argb, ushort hasFlag) =>
        Has(hasFlag) ? PackedColorCache.Get(argb) : null;
}

/// <summary>
/// 詰めた ARGB から <see cref="Color"/> を作り直す手間を省く小さな表。<see cref="Color.FromArgb"/> は
/// scRGB への変換で 1 色につき 3 回の累乗を計算し、描画は表示中の全マスの書式を毎回解決するので、
/// 同じ数色を何千回も作り直すことになる。端末に出る色は少数なので、直接写像の表で十分当たる。
/// 項目は不変の参照型にして、別スレッド（ホストのセッションごとのスレッド）が同時に引いても値が千切れない。
/// </summary>
internal static class PackedColorCache
{
    private const int SizeBits = 8;
    private static readonly Entry?[] Entries = new Entry?[1 << SizeBits];

    public static Color Get(uint argb)
    {
        int index = (int)((argb * 2654435761u) >> (32 - SizeBits));
        Entry? entry = Volatile.Read(ref Entries[index]);
        if (entry is not null && entry.Argb == argb)
        {
            return entry.Color;
        }

        Color color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        Volatile.Write(ref Entries[index], new Entry(argb, color));
        return color;
    }

    private sealed class Entry(uint argb, Color color)
    {
        public uint Argb { get; } = argb;
        public Color Color { get; } = color;
    }
}
