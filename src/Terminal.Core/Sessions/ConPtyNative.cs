using System.Runtime.InteropServices;

namespace Terminal.Sessions;

/// <summary>
/// 擬似コンソールの 3 つの入口（作る・大きさを変える・閉じる）を、どの ConPTY で呼ぶか決める。
/// <para>実行フォルダーに Microsoft 配布の <c>conpty.dll</c>（NuGet <c>Microsoft.Windows.Console.ConPTY</c>）と、その隣の
/// <c>&lt;arch&gt;\OpenConsole.exe</c> があればそちらを使う。無ければ Windows に入っている kernel32 の ConPTY。</para>
/// <para>違いが効くのは端末への問い合わせ：受信箱の conhost は DA1（<c>CSI c</c>）に自分で即答するので、問い合わせのあとに
/// DA1 を送って「先に返事が来たら非対応」と判定するプログラム（Claude Code の OSC 7501 など）では、端末の返事が必ず負ける。
/// 新しい ConPTY（microsoft/terminal #17510 以降）は DA1 も端末へ回すので、端末が順に答えられる。</para>
/// </summary>
internal static class ConPtyNative
{
    private static readonly Lazy<bool> Bundled = new(DetectBundled);

    /// <summary>同梱の ConPTY を使っているか。</summary>
    public static bool UsesBundled => Bundled.Value;

    public static int Create(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole)
        => UsesBundled
            ? ConptyCreatePseudoConsole(size, input, output, flags, out pseudoConsole)
            : CreatePseudoConsole(size, input, output, flags, out pseudoConsole);

    public static int Resize(IntPtr pseudoConsole, Coord size)
        => UsesBundled ? ConptyResizePseudoConsole(pseudoConsole, size) : ResizePseudoConsole(pseudoConsole, size);

    public static void Close(IntPtr pseudoConsole)
    {
        if (UsesBundled)
            ConptyClosePseudoConsole(pseudoConsole);
        else
            ClosePseudoConsole(pseudoConsole);
    }

    /// <summary>
    /// 擬似コンソールの隠しコンソールウィンドウの持ち主を <paramref name="ownerWindow"/> にする（Windows Terminal・VS Code 1.144 と同じ）。
    /// プログラムがコンソールに結び付けて出すダイアログ（資格情報の選択・サインイン）が、端末を映すウィンドウの手前に出るようになる。
    /// 同梱の ConPTY にしか無い口なので、受信箱の ConPTY では何もしない（false）。
    /// </summary>
    public static bool Reparent(IntPtr pseudoConsole, IntPtr ownerWindow)
    {
        if (!UsesBundled || pseudoConsole == IntPtr.Zero)
            return false;
        try
        {
            return ConptyReparentPseudoConsole(pseudoConsole, ownerWindow) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;   // 古い conpty.dll
        }
    }

    private static bool DetectBundled()
    {
        if (Environment.GetEnvironmentVariable("TERMINAL_CONPTY") is { } forced)
        {
            // 切り分け用：inbox で kernel32 に戻す。bundled は同梱が揃っているときだけ効く。
            if (forced.Equals("inbox", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        string baseDirectory = AppContext.BaseDirectory;
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        return File.Exists(Path.Combine(baseDirectory, "conpty.dll"))
               && File.Exists(Path.Combine(baseDirectory, arch, "OpenConsole.exe"));
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Coord
    {
        internal Coord(short x, short y) { X = x; Y = y; }
        internal readonly short X;
        internal readonly short Y;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("conpty.dll", SetLastError = true)]
    private static extern int ConptyCreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

    [DllImport("conpty.dll", SetLastError = true)]
    private static extern int ConptyResizePseudoConsole(IntPtr pseudoConsole, Coord size);

    [DllImport("conpty.dll", SetLastError = false)]
    private static extern void ConptyClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("conpty.dll", SetLastError = false)]
    private static extern int ConptyReparentPseudoConsole(IntPtr pseudoConsole, IntPtr newParent);
}
