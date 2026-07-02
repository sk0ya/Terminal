namespace Terminal.Tabs;

/// <summary>
/// ConEmu 由来の OSC 9;4 タスクバー進捗の状態。
/// </summary>
public enum TaskbarProgressState
{
    /// <summary>進捗表示なし（OSC 9;4;0）。</summary>
    None,

    /// <summary>通常進捗（OSC 9;4;1）。<see cref="TaskbarProgressChangedEventArgs.Progress"/> が有効。</summary>
    Normal,

    /// <summary>エラー進捗（OSC 9;4;2、赤）。<see cref="TaskbarProgressChangedEventArgs.Progress"/> が有効。</summary>
    Error,

    /// <summary>不確定進捗（OSC 9;4;3）。進捗値は無視される。</summary>
    Indeterminate,

    /// <summary>一時停止・警告進捗（OSC 9;4;4、黄）。<see cref="TaskbarProgressChangedEventArgs.Progress"/> が有効。</summary>
    Warning
}

/// <summary>
/// <see cref="TerminalTabView.TaskbarProgressChanged"/> のイベントデータ。
/// アプリケーションが OSC 9;4 で報告したタスクバー進捗を表す。
/// </summary>
public sealed class TaskbarProgressChangedEventArgs : EventArgs
{
    internal TaskbarProgressChangedEventArgs(TaskbarProgressState state, int progress)
    {
        State = state;
        Progress = progress;
    }

    public TaskbarProgressState State { get; }

    /// <summary>0–100 の進捗率。<see cref="TaskbarProgressState.Indeterminate"/> と
    /// <see cref="TaskbarProgressState.None"/> のときは 0。</summary>
    public int Progress { get; }
}
