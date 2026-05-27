# ConPtyTerminal Roadmap

## 目的
このリポジトリを「ConPTY 上で一般的な CLI / TUI を安定して扱える WPF ターミナル」まで育てる。

## 現在地
以下は実装済み。

- ConPTY 起動とサイズ変更
- quoted command line 解析
- ANSI / VT の基本パーサ
- スクロールバック、カーソル、色、下線、反転
- alternate screen の基本
- application cursor / application keypad
- bracketed paste / focus report / mouse tracking の基本
- `HTS`, `TBC`, `CHT`, `CBT`, `IRM`, `DECSCUSR`
- `OSC 8`, `OSC 52`, `1005`, `1015`, `1048`, `1049`
- DEC Special Graphics
- grapheme cluster / ZWJ emoji / variation selector / 国旗ペア / combining mark の基本対応
- `TerminalSurfaceControl` による描画、スクロール、選択、検索、コピー
- cursor overlay と viewport sizing / render 分離
- スクロールバック閲覧中の自動追従抑制
- 修飾キー付きの主要キーシーケンス
- Ctrl 系の主要 ASCII 制御文字入力
- legacy mouse を含む raw byte / text protocol の入力エンコード
- WPF input proxy による IME composition の受け取りと candidate / composition window の位置同期
- stalled startup 検知と recover
- session smoke test を含む自動テスト

以下はまだ不足している。

- IME の実機検証と細部調整
- 高頻度更新に対する実測チューニング
- Unicode 幅計算の厳密性
- VT シーケンスの網羅性
- ConPTY 起動失敗時の診断と回復導線

## 優先順位
### Phase 1: 入力の完成度を上げる
最優先。日本語入力と TUI 操作性に直結する。

進捗:

- [x] IME composition の受け取り
- [x] 変換中テキストと確定テキストの取り扱い整理
- [x] `RichTextBox` 依存の入力制約の見直し
- [x] Alt / Ctrl / Shift 組み合わせの主要キーシーケンス整理
- [x] legacy mouse の raw byte 送信
- [ ] 日本語 IME の実機検証と細部調整
- [ ] `vim`, `less`, `fzf` を使った継続確認

完了条件:

- 日本語 IME で入力、変換、確定が破綻しない
- `vim`, `less`, `fzf` の基本操作が通る
- 修飾キー付き操作で想定外の文字混入が起きない

### Phase 2: 描画とパフォーマンスを作り直す
表示面の `RichTextBox` / `FlowDocument` 依存は外れ、`TerminalSurfaceControl` ベースに移行済み。残りは実負荷でのチューニング。

進捗:

- [x] 全 document 再生成をやめる
- [x] terminal 表示面を custom surface に置き換える
- [x] カーソル描画を overlay 化する
- [x] viewport の更新と render の責務を分離する
- [ ] 大量ログ / `vim` 相当の高頻度更新で実測する
- [ ] スクロールバック量が増えても操作感を維持する
- [ ] visible range / 再描画量の最適化を進める

完了条件:

- `vim` や大量ログ出力で UI が目立って固まらない
- 出力更新中でもスクロール、選択、コピーが破綻しない

### Phase 3: Unicode の正確性を上げる
表示品質の要。基本対応は入ったが、幅計算はまだヒューリスティック実装。

進捗:

- [x] grapheme cluster 単位の基本処理
- [x] ZWJ emoji
- [x] variation selector
- [x] 国旗ペア / combining mark の基本処理
- [x] East Asian ambiguous width
- [x] combining mark の境界条件
- [x] 文字幅とマウス座標計算の厳密な整合

完了条件:

- 絵文字、全角、結合文字でカーソル位置が大きくずれない
- 表示と hit testing の列計算が一致する

### Phase 4: VT / xterm 互換性を詰める
TUI の相性改善フェーズ。

進捗:

- [x] `HTS`, `TBC`, `CHT`, `CBT`
- [x] `IRM`
- [x] `DECSCUSR`
- [x] `OSC 8`
- [x] `1005`, `1015`
- [x] `1048`, `1049` を含む save / restore の基本
- [x] SGR dim (2/22), italic (3/23), blink (5/6/25), invisible (8/28), strikethrough (9/29)
- [ ] save / restore 周辺の細かい互換
- [ ] 追加の DEC private mode
- [ ] 追加 mouse mode の検証

完了条件:

- `vim`, `less`, `git log`, `htop` 相当の主要操作で互換問題が減る
- 代表的な xterm 前提アプリで致命的な表示崩れが残らない

### Phase 5: セッション層を強化する
ConPTY セッションの起動・終了・復旧をより堅牢にする。

進捗:

- [x] raw byte write を含む text / binary 入力経路を ConPTY session に持たせる
- [x] startup stall 検知と recover
- [x] 終了処理と dispose / force unlock の見直し
- [ ] 起動失敗時の診断導線
- [ ] signal 戦略の深掘り
- [ ] 復旧時の状態引き継ぎ

完了条件:

- ConPTY 起動失敗時に原因を切り分けしやすい
- 入出力経路が text / binary の両方で安定する

### Phase 6: テストと検証基盤を入れる
継続改善の前提。主要な自動テスト基盤は導入済みで、残りは実機検証の厚み付け。

進捗:

- [x] parser / buffer 操作テスト
- [x] key encoding / mouse encoding テスト
- [x] OSC / CSI 応答テスト
- [x] ConPTY smoke test
- [x] surface / viewport / overlay 回帰テスト
- [ ] IME 実機検証の継続
- [ ] 実アプリ互換の回帰ケース拡充

完了条件:

- 主要な制御シーケンスと入力変換に回帰テストがある
- 変更時に壊れた箇所を自動検知できる

## 直近でやる順番
1. Phase 1 の IME 実機検証と TUI 操作確認を詰める
2. Phase 2 の高頻度更新 / 大量ログで実測してボトルネックを潰す
3. Phase 3 の ambiguous width と hit testing 整合を改善する
4. Phase 4 の残り VT / xterm 互換性を `vim` / `less` / `fzf` 基準で詰める
5. Phase 5 / 6 の recover / dispose 周辺の回帰ケースを増やす

## 検証対象アプリ
最低限、以下で継続確認する。

- `cmd.exe`
- `powershell`
- `pwsh`
- `vim`
- `less`
- `fzf`
- `git log --decorate --graph`

### Phase 7: セッションロギングを実装する

**目的**: プロンプト（ユーザー入力）と LLM の応答を後から解析できるよう記録する。ログは人間と LLM の両方が読む。

ターミナル側で ConPTY パイプを流れる全データを取るため、Codex / Claude Code / Copilot CLI など CLI が変わっても対応できる。

#### 保存するもの

| 項目 | 内容 |
|---|---|
| input | ユーザーが入力したテキスト（プロンプト） |
| output | アプリが出力したテキスト（LLM 応答）。**ANSI エスケープ除去済み** |
| メタデータ | セッションID、実行コマンド、cwd、開始/終了時刻、終了コード |

raw bytes（ANSI シーケンス込み）は保存しない。解析の邪魔になるため。

#### 保存形式: JSONL（1イベント1行）

1セッション = 1ファイル。人間が `cat` で読め、LLM にそのまま渡せる。

```jsonc
{"ts":"2026-04-21T14:03:01.123+09:00","sid":"abc123","event":"session_start","tool":"claude-code","command":"claude","cwd":"C:\\Projects\\Terminal","pid":1234,"cols":220,"rows":50}
{"ts":"2026-04-21T14:03:02.001+09:00","sid":"abc123","event":"input","text":"explain this function\n"}
{"ts":"2026-04-21T14:03:03.412+09:00","sid":"abc123","event":"output","text":"This function takes a list and returns...\n"}
{"ts":"2026-04-21T14:03:10.008+09:00","sid":"abc123","event":"session_end","exit_code":0,"duration_ms":8887}
```

#### 秘密情報マスク

保存前に以下をマスクする。会話の流れは残り、キー自体は残らない。

- `sk-` で始まるトークン（OpenAI 系）
- `ghp_`, `github_pat_`（GitHub PAT）
- `Bearer ` に続くトークン
- `Authorization:` ヘッダー値
- `-----BEGIN PRIVATE KEY-----` ブロック

#### ログ保存先

`%APPDATA%\ConPtyTerminal\logs\sessions\<project>\YYYY-MM-DD\<session_id>.jsonl`

- `<project>` は cwd の末尾フォルダ名（例: `C:\Projects\Terminal` → `Terminal`）
- 同名プロジェクトが複数ある場合でも、cwd のフルパスは JSONL 内の `session_start` に残るので区別できる
- ツールはフォルダではなく JSONL 内の `tool` フィールドで識別する（同一プロジェクトで複数ツールを使い分けるケースに対応）

#### 圧縮

当日のフォルダは JSONL のまま保持する。アプリ起動時に前日以前のフォルダを zip に圧縮する。記録は削除しない。

```
logs/sessions/Terminal/
  2026-04-19.zip     ← 圧縮済み
  2026-04-20.zip     ← 圧縮済み
  2026-04-21/        ← 当日はフォルダのまま
    <session_id>.jsonl
```

#### アーキテクチャ

`LoggingTerminalSession : ITerminalSession` が `ConPtySession` をラップするデコレータ。`ITerminalSession` を変更しないため既存コードへの影響なし。

```
ConPtySession
    ↑ wraps
LoggingTerminalSession  →  ISessionLogger  →  SessionLogWriter (JSONL file)
                                          ↗  SecretRedactor (input/output をマスク)
```

#### ファイル構成

```
Logging/
  ISessionLogger.cs         ← ロガー抽象
  SessionLogWriter.cs       ← JSONL ファイル書き込み
  SessionLogEvent.cs        ← イベント型 (record)
  SecretRedactor.cs         ← 秘密情報マスク
  LoggingTerminalSession.cs ← ITerminalSession デコレータ
```

#### TerminalAppSettings 拡張

```csharp
bool EnableSessionLogging { get; set; }   // 既定: true
string? SessionLogDirectory { get; set; } // null = デフォルトパス
```

#### 実装ステップ

進捗:

- [x] **Step 1**: `SessionLogEvent` / `ISessionLogger` / `SessionLogWriter` を作る
  - JSONL 書き込み（UTF-8 BOM なし、改行 LF）
  - 日付フォルダ＋セッション単位ファイルで保存
- [x] **Step 2**: `SecretRedactor` を作る
  - 正規表現ベースのパターンマスク
  - input / output 双方に適用
  - 単体テストを書く
- [x] **Step 3**: `LoggingTerminalSession` デコレータを作る
  - `Write(string)` / `Write(byte[])` への入力は Enter（`\r` or `\n`）で確定するまでバッファリングし、1プロンプト = 1 input イベントとして記録する
  - `OutputReceived` イベントで output（ANSI 除去済み）を記録。output の先頭に input の echo が含まれる場合は除去する
  - `Exited` イベントで session_end を記録
  - セッション開始時に session_start を書く（tool, command, cwd, pid, cols, rows）
  - ログ書き込みエラーはターミナルのエラーとして出力する（ターミナル本体の動作は止めない）
- [x] **Step 4**: `TerminalAppSettings` に `EnableSessionLogging` を追加し、`MainWindow` でデコレータを組み込む
- [x] **Step 5**: アプリ起動時に前日以前のフォルダを zip 圧縮する処理を実装する
- [x] **Step 6**: テストを書く（SecretRedactor の網羅、JSONL 形式の正確さ、デコレータの透過性）

完了条件:

- Claude Code / Codex のセッションが自動的に JSONL として残る
- input / output の両方が ANSI 除去済みテキストで記録される
- APIキー等の秘密情報が保存前にマスクされる
- ロギングを無効化できる
- `LoggingTerminalSession` を外しても既存の動作が変わらない（デコレータの透過性）

## 直近でやる順番
1. Phase 1 の IME 実機検証と TUI 操作確認を詰める
2. Phase 2 の高頻度更新 / 大量ログで実測してボトルネックを潰す
3. Phase 3 の ambiguous width と hit testing 整合を改善する
4. Phase 4 の残り VT / xterm 互換性を `vim` / `less` / `fzf` 基準で詰める
5. Phase 5 / 6 の recover / dispose 周辺の回帰ケースを増やす
6. **Phase 7 のセッションロギングを実装する**

## 検証対象アプリ
最低限、以下で継続確認する。

- `cmd.exe`
- `powershell`
- `pwsh`
- `vim`
- `less`
- `fzf`
- `git log --decorate --graph`
- `claude` (Claude Code)
- `codex` (OpenAI Codex CLI)
- `gh copilot` (GitHub Copilot CLI)

## メモ
- 現在は ConPTY 前提の実装に寄せている。起動失敗時の診断と recover 導線は継続改善対象。
- terminal 表示面からは `RichTextBox` / `FlowDocument` 依存を外した。残るのは高頻度更新時の実測チューニングと旧描画資産の整理。
- 互換性は「仕様追加」だけでなく「既存挙動の検証」を伴うため、今後はテスト追加を優先する。
- Phase 7 のロギングは `ITerminalSession` デコレータとして実装するため、既存コードに影響を与えない。目的はプロンプトと LLM 応答の解析。ANSI 除去済みテキストのみ保存し、raw bytes は残さない。
