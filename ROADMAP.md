# ConPtyTerminal Roadmap

## 目的
このリポジトリを「ConPTY 上で一般的な CLI / TUI を安定して扱える WPF ターミナル」まで育てる。

## 現在地
以下は実装済み。

- ConPTY 起動とサイズ変更
- compatibility session (`ProcessPipeSession`) と quoted command line 解析
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
- stalled startup 検知、recover、compatibility mode への退避
- session smoke test を含む自動テスト

以下はまだ不足している。

- IME の実機検証と細部調整
- 高頻度更新に対する実測チューニング
- Unicode 幅計算の厳密性
- VT シーケンスの網羅性
- fallback セッションの resize / signal 戦略の深掘り

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
- [ ] East Asian ambiguous width
- [ ] combining mark の境界条件
- [ ] 文字幅とマウス座標計算の厳密な整合

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
- [ ] save / restore 周辺の細かい互換
- [ ] 追加の DEC private mode
- [ ] 追加 mouse mode の検証

完了条件:

- `vim`, `less`, `git log`, `htop` 相当の主要操作で互換問題が減る
- 代表的な xterm 前提アプリで致命的な表示崩れが残らない

### Phase 5: セッション層を強化する
ConPTY 以外の経路も最低限使える状態にする。

進捗:

- [x] `ProcessPipeSession` を compatibility mode として切り分ける
- [x] raw byte write を含む text / binary 入力経路を両 session に持たせる
- [x] startup stall 検知と compatibility mode への recover
- [x] 終了処理と dispose / force unlock の見直し
- [ ] fallback 時の resize 戦略
- [ ] signal 戦略の深掘り
- [ ] 復旧時の状態引き継ぎ

完了条件:

- ConPTY が使えない環境でも退避動作が明確
- 入出力経路が text / binary の両方で安定する

### Phase 6: テストと検証基盤を入れる
継続改善の前提。主要な自動テスト基盤は導入済みで、残りは実機検証の厚み付け。

進捗:

- [x] parser / buffer 操作テスト
- [x] key encoding / mouse encoding テスト
- [x] OSC / CSI 応答テスト
- [x] ConPTY / compatibility smoke test
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
5. Phase 5 / 6 の fallback resize / recover 周辺の回帰ケースを増やす

## 検証対象アプリ
最低限、以下で継続確認する。

- `cmd.exe`
- `powershell`
- `pwsh`
- `vim`
- `less`
- `fzf`
- `git log --decorate --graph`

## メモ
- `ProcessPipeSession` は本格 terminal 実装の代替にはならない。用途は互換モードまたは切り分け用と割り切る。
- terminal 表示面からは `RichTextBox` / `FlowDocument` 依存を外した。残るのは高頻度更新時の実測チューニングと旧描画資産の整理。
- 互換性は「仕様追加」だけでなく「既存挙動の検証」を伴うため、今後はテスト追加を優先する。
