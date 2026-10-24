namespace Terminal.Tabs;

/// <summary>OSC 7501（Program Status Protocol）でプログラムが申告した状態。</summary>
public enum ProgramStatusState
{
    /// <summary>手が空いている（入力を待っている）。</summary>
    Idle,

    /// <summary>作業中。<see cref="ProgramStatusRecord.Progress"/> があれば進捗率。</summary>
    Working,

    /// <summary>終わった。プロンプトが戻っても残る（いつ消すかは表示側が決める）。</summary>
    Done,

    /// <summary>人を待っている。<see cref="ProgramStatusRecord.Kind"/> に何を待っているか。</summary>
    Blocked,

    /// <summary>失敗した。プロンプトが戻っても残る。</summary>
    Error,
}

/// <summary><see cref="ProgramStatusState.Blocked"/> のとき、何を待っているか。</summary>
public enum ProgramStatusKind
{
    /// <summary>申告なし（または Blocked でない）。</summary>
    None,

    /// <summary>許可を待っている（ツールの実行の承認など）。</summary>
    Permission,

    /// <summary>質問への答えを待っている。</summary>
    Question,

    /// <summary>認証（サインイン・パスワード）を待っている。</summary>
    Auth,
}

/// <summary>
/// OSC 7501 のレコード 1 つ。<see cref="Id"/> は <c>build/test</c> のような階層（空文字はルート）。
/// <see cref="App"/> は自分が申告していなければ最も近い祖先のものを継いだ値。<see cref="Title"/> と <see cref="Message"/> は
/// base64 を解いた文字列（制御文字を含むものは端末が捨てているので来ない）。表示の仕方は受け手が決める——
/// 仕様は「端末は <c>msg</c> から意味を読み取ってはならない」としている。
/// </summary>
public sealed record ProgramStatusRecord(
    string Id,
    ProgramStatusState State,
    ProgramStatusKind Kind,
    int? Progress,
    string? App,
    string? Title,
    string? Message,
    DateTimeOffset UpdatedAt);

/// <summary><see cref="TerminalTabView.ProgramStatusChanged"/> のイベントデータ。いま生きているレコードの全部（Id 順）。</summary>
public sealed class ProgramStatusChangedEventArgs : EventArgs
{
    internal ProgramStatusChangedEventArgs(IReadOnlyList<ProgramStatusRecord> records) => Records = records;

    public IReadOnlyList<ProgramStatusRecord> Records { get; }
}
