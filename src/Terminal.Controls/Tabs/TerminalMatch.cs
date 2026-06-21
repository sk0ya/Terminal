namespace Terminal.Tabs;

/// <summary>
/// ターミナルバッファ内のテキスト一致1件。ホストアプリ（コマンドパレット等）が
/// 一致一覧を提示し、選んだ箇所へジャンプするための情報を持つ。
/// </summary>
/// <param name="LineIndex">一致した行のインデックス（バッファ先頭からの0始まり）。</param>
/// <param name="Column">行内の一致開始位置（テキストインデックス、0始まり）。</param>
/// <param name="Length">一致した長さ（＝検索文字列の長さ）。</param>
/// <param name="LineText">一致した行のプレーンテキスト全体（一覧表示用）。</param>
public readonly record struct TerminalMatch(int LineIndex, int Column, int Length, string LineText);
