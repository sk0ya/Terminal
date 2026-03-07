# ConPtyTerminal Roadmap

## 目的
このリポジトリを「ConPTY 上で一般的な CLI / TUI を安定して扱える WPF ターミナル」まで育てる。

## 現在地
以下は実装済み。

- ConPTY 起動とサイズ変更
- ANSI / VT の基本パーサ
- スクロールバック、カーソル、色、下線、反転
- alternate screen の基本
- application cursor / application keypad
- bracketed paste / focus report / mouse tracking の基本
- DEC Special Graphics
- wide character の基本対応
- OSC 52 の clipboard set / query
- スクロールバック閲覧中の自動追従抑制
- 修飾キー付きの主要キーシーケンス

以下はまだ不足している。

- IME / 日本語入力の本格対応
- 高頻度更新に耐える描画方式
- Unicode 幅計算の正確性
- VT シーケンスの網羅性
- fallback セッションの実用性
- 自動テスト

## 優先順位

### Phase 1: 入力の完成度を上げる
最優先。日本語入力と TUI 操作性に直結する。

対象:

- IME composition の受け取り
- 変換中テキストと確定テキストの取り扱い整理
- `RichTextBox` 依存の入力制約の見直し
- Alt / Ctrl / Shift 組み合わせの抜け漏れ確認
- legacy mouse の raw byte 送信の仕上げ

完了条件:

- 日本語 IME で入力、変換、確定が破綻しない
- `vim`, `less`, `fzf` の基本操作が通る
- 修飾キー付き操作で想定外の文字混入が起きない

### Phase 2: 描画とパフォーマンスを作り直す
現状は `FlowDocument` を毎回再生成しており、重い。

対象:

- 全 document 再生成をやめる
- 行単位または差分単位で描画更新する
- スクロールバック量が増えても操作感を維持する
- カーソル描画を軽量化する
- viewport の更新と render の責務を分離する

完了条件:

- `vim` や大量ログ出力で UI が目立って固まらない
- 出力更新中でもスクロール、選択、コピーが破綻しない

### Phase 3: Unicode の正確性を上げる
表示品質の要。今はヒューリスティック実装。

対象:

- grapheme cluster 単位の処理
- ZWJ emoji
- variation selector
- East Asian ambiguous width
- combining mark の境界条件
- 文字幅とマウス座標計算の整合

完了条件:

- 絵文字、全角、結合文字でカーソル位置が大きくずれない
- 表示と hit testing の列計算が一致する

### Phase 4: VT / xterm 互換性を詰める
TUI の相性改善フェーズ。

対象:

- `HTS`, `TBC`, `CHT`, `CBT`
- `IRM`
- `DECSCUSR`
- `OSC 8`
- `1005`, `1015` など追加 mouse mode
- save / restore 周辺の細かい互換
- 追加の DEC private mode

完了条件:

- `vim`, `less`, `git log`, `htop` 相当の主要操作で互換問題が減る
- 代表的な xterm 前提アプリで致命的な表示崩れが残らない

### Phase 5: セッション層を強化する
ConPTY 以外の経路も最低限使える状態にする。

対象:

- `ProcessPipeSession` の位置づけ整理
- fallback 時の resize 戦略
- raw byte write を含む入出力の整合
- セッション障害時の復旧と状態引き継ぎ
- 終了処理と dispose の見直し

完了条件:

- ConPTY が使えない環境でも退避動作が明確
- 入出力経路が text / binary の両方で安定する

### Phase 6: テストと検証基盤を入れる
継続改善の前提。

対象:

- parser 単体テスト
- buffer 操作テスト
- key encoding / mouse encoding テスト
- OSC / CSI 応答テスト
- ConPTY smoke test

完了条件:

- 主要な制御シーケンスと入力変換に回帰テストがある
- 変更時に壊れた箇所を自動検知できる

## 直近でやる順番
1. Phase 1 の IME と raw byte mouse を仕上げる
2. Phase 2 の差分描画に着手する
3. Phase 3 の Unicode 幅計算を改善する
4. Phase 4 の VT 互換性を `vim` / `less` / `fzf` 基準で詰める
5. Phase 6 の parser / input encoder テストを追加する

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
- `RichTextBox` ベースは早く動かすには有効だが、長期的には描画責務の分離が必要。
- 互換性は「仕様追加」だけでなく「既存挙動の検証」を伴うため、今後はテスト追加を優先する。
