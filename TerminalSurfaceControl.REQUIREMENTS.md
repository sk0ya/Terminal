# TerminalSurfaceControl Requirements

## Goal

`RichTextBox` / `FlowDocument` 依存の terminal 描画をやめて、`nvim` のような高頻度な画面更新でもちらつきとレイアウト負荷を抑えられる専用 surface に置き換える。

## Scope

- `TerminalTabView` の描画面を `TerminalSurfaceControl` に置き換える
- terminal の表示、スクロール、選択、検索、コピーの基盤を `FlowDocument` 非依存にする
- 既存の session 層と `AnsiTerminalBuffer` は再利用する
- IME 用の `TerminalInputProxy` は当面維持する

## Non-Goals

- session 実装の置き換え
- VT 互換性の全面改修
- Unicode 幅判定の完全実装

## Functional Requirements

### R1. Rendering

- `AnsiTerminalBuffer.TerminalRenderSnapshot` から直接描画できる
- 行単位で背景色、前景色、太字、下線を反映できる
- terminal 全体を `FlowDocument` に変換しない

Status: `implemented`

### R2. Scrolling

- `ScrollViewer` と連携できる
- 垂直・水平スクロールが動作する
- `TerminalTabView` の follow/pinned の判定に必要な offset / extent / viewport を提供できる

Status: `implemented`

### R3. Cursor / Geometry

- 現在の terminal cursor のセル位置から overlay 用の座標を取得できる
- 入力 proxy 配置に必要な文字セルサイズを取得できる
- マウス座標を terminal 上のセルへ変換できる

Status: `implemented`

### R4. Selection / Copy

- マウスドラッグで範囲選択できる
- 選択範囲のテキストをコピーできる
- 選択描画を surface 内で行える

Status: `implemented`

Notes:

- 選択範囲は surface 独自 range で管理する
- text element 単位の幅推定を使うため、複雑な grapheme cluster の完全一致は今後の改善対象

### R5. Find

- 現在表示している terminal text に対して件数カウントできる
- 次 / 前の一致へ移動できる
- 一致範囲を選択状態として可視化できる

Status: `implemented`

### R6. Hyperlinks

- OSC 8 由来の hyperlink を描画上で保持できる
- click で navigation を発火できる

Status: `implemented`

### R7. Integration

- `TerminalTabView` から `RichTextBox` 固有 API を除去する
- 既存の入力、マウス送信、スクロール復元、font 変更と共存できる

Status: `implemented`

## Implementation Notes

- 初期実装は WPF の custom drawing と `IScrollInfo` を使う
- Unicode の幅判定は現行 buffer のヒューリスティックに揃えるか、それに近い簡易実装を許容する
- 選択と検索は `FlowDocument` の `TextPointer` ではなく surface 内の独自 range を使う

## Acceptance

- [x] build が通る
- [x] 既存の terminal 起動と基本入力が壊れない
- [x] `Find`, `Copy`, `Scroll`, `Zoom` が surface 上で引き続き動く
- [x] `RichTextBox` / `FlowDocument` 依存を terminal 表示面から除去した
- [ ] `nvim` / `vim` 系の体感改善は実機確認が必要
