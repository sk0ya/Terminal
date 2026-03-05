# ConPTY実装メモ（WPF）

## 概要
ConPTY は、コンソールアプリを疑似端末（Pseudo Console）に接続し、端末の入出力をパイプ経由で扱うための仕組みです。  
WPF で使う場合、ConPTY 出力には VT/ANSI 制御シーケンスが含まれるため、`TextBox.AppendText` だけでは正しく表示できません。

## ConPTY の最小フロー
1. `CreatePipe` で 2 系統のパイプを作る
   - アプリ -> ConPTY 入力
   - ConPTY 出力 -> アプリ
2. `CreatePseudoConsole` で疑似コンソール（`HPCON`）を作成
3. `InitializeProcThreadAttributeList` で起動属性リストを確保
4. `UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE)` で `HPCON` を設定
5. `CreateProcessW` を `EXTENDED_STARTUPINFO_PRESENT` 付きで起動
6. アプリ側で入力を書き込み、出力を読み取る
7. 画面サイズ変更時に `ResizePseudoConsole` を呼ぶ
8. 終了時に `ClosePseudoConsole`、各ハンドル、ストリームを順に解放

## WPF 統合時のポイント
- UI 更新は `Dispatcher` 経由で行う
- コントロールのサイズとフォント情報から列数・行数を算出する
- プロセス終了監視はバックグラウンドタスクで行う
- 初期化途中で失敗した場合もハンドルを確実に解放する

## 実装中に発生した問題と対応

### 1) EntryPoint 名の不一致
- 症状: `Unable to find an entry point named 'CreatePseudoConsoleHandle'`
- 原因: P/Invoke が `kernel32.dll` 内にラッパー名を探していた
- 対応: `DllImport` に `EntryPoint = "CreatePseudoConsole"` と `EntryPoint = "ClosePseudoConsole"` を指定

### 2) 同期ハンドルを非同期 `FileStream` で開いた
- 症状: `Handle does not support asynchronous operations`
- 原因: `CreatePipe` のハンドルは同期なのに `isAsync: true` で開いていた
- 対応: `FileStream(..., isAsync: false)` に変更

### 3) 制御シーケンスがそのまま表示される
- 症状: `[?25l[2J...` のような文字列が見える
- 原因: VT/ANSI シーケンスを未解釈で表示していた
- 対応: `CSI` / `OSC` / カーソル移動 / 画面クリアを処理する最小パーサを導入

### 4) パーサ導入後に何も表示されない
- 原因: `OSC` 終端処理で状態遷移が詰まり、通常文字処理に戻れなかった
- 対応: `BEL`、`ESC \`、`ST` の終端を受理し、連続シーケンスでも復帰できるよう修正

## 現在のファイル構成
- ConPTY セッションと Win32 連携: `ConPtySession.cs`
- VT パーサと画面バッファ: `AnsiTerminalBuffer.cs`
- WPF 側の接続とライフサイクル: `MainWindow.xaml.cs`
- WPF レイアウト: `MainWindow.xaml`