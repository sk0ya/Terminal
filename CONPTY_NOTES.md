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

### 5) `CreateProcessW` は成功するのに ConPTY 側が無出力で固まる
- 症状: `CreateProcessW` 自体は成功するが、ConPTY 出力が 0 バイトのままになり、`cmd.exe` が `0xC0000142` で落ちることがある
- 原因: `UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE)` に `HPCON` そのものではなく、`IntPtr` 変数の参照を渡していた
- 対応: `lpValue` には `HPCON` 値そのものを渡す。C# の P/Invoke でも `ref IntPtr` ではなく `IntPtr` を使う

### 6) ConPTY を付けても子プロセスの標準入出力が親コンソールに流れる
- 症状: 子プロセスの表示や入力が ConPTY パイプに来ず、親コンソール側に出たり、対話が不安定になる
- 原因: `STARTUPINFOEX` で Pseudoconsole を付けても、`STARTF_USESTDHANDLES` を立てずに起動すると標準ハンドル複製の影響を受けることがある
- 対応: `STARTF_USESTDHANDLES` を設定し、`hStdInput` / `hStdOutput` / `hStdError` は `NULL`、`CreateProcessW` の `bInheritHandles` は `FALSE` にする

### 7) ConPTY 側パイプを閉じるタイミングが早すぎると起動が不安定になる
- 症状: 初期化直後にハングや無出力が発生し、再現が安定しない
- 原因: `CreatePseudoConsole` 用に渡した ConPTY 側の read/write ハンドルを、子プロセス生成前に閉じていた
- 対応: `CreateProcessW` 成功後に ConPTY 側ハンドルを閉じる

### 8) `cmd.exe /K chcp 65001 > nul` は ConPTY 既定コマンドとして不適切
- 症状: 単発コマンド (`cmd.exe /c echo ...`) は動くのに、対話モードだけ入力が返ってこない
- 原因: `cmd.exe /K chcp 65001 > nul` を初期コマンドにすると、この構成では `cmd.exe` の対話状態が崩れる
- 対応: 既定コマンドは `cmd.exe /K` にする。UTF-8 化が必要なら、ConPTY 上での `chcp` 依存を前提にしない別設計を検討する

## 今回の切り分けで有効だった確認方法
- ネイティブ最小実装と C# 実装で同じコマンドを実行し、WPF 固有の問題か ConPTY 起動条件の問題かを分離した
- `cmd.exe /c echo ...` の単発実行で ConPTY 自体の生成可否を確認した
- `cmd.exe /K` と `cmd.exe /K chcp 65001 > nul` を比較して、API 問題と起動コマンド問題を分離した
- 失敗時の終了コード `0xC0000142` を手掛かりに、無効な Pseudoconsole handle の可能性を優先して潰した

## 現在のファイル構成
- ConPTY セッションと Win32 連携: `ConPtySession.cs`
- VT パーサと画面バッファ: `AnsiTerminalBuffer.cs`
- WPF 側の接続とライフサイクル: `MainWindow.xaml.cs`
- WPF レイアウト: `MainWindow.xaml`
