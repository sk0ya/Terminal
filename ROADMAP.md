# ConPtyTerminal Roadmap

## 実装済み

- ConPTY 起動・サイズ変更・セッション管理（起動失敗診断、recover、dispose）
- ANSI / VT パーサ（基本 + SGR dim/italic/blink/invisible/strikethrough）
- スクロールバック・カーソル・色・下線・反転・alternate screen
- VT シーケンス: `HTS`, `TBC`, `CHT`, `CBT`, `IRM`, `DECSCUSR`, `OSC 8`, `OSC 52`, `1005`, `1015`, `1048`, `1049`, DEC Special Graphics
- VT 互換: DECRQM, XTWINOPS, XTSAVE/XTRESTORE, DECSCNM, OSC 10/11/12, XTVERSION
- mouse tracking（legacy raw byte / 1006 / DECRQM 状態クエリ）
- grapheme cluster / ZWJ emoji / variation selector / 国旗ペア / combining mark
- East Asian Ambiguous width（`CjkAmbiguousWidthIsWide` 設定で制御）
- `TerminalSurfaceControl` 描画（diff ベース再描画、scroll、選択、検索、コピー）
- カーソル overlay / viewport sizing / render 分離
- WPF input proxy による IME composition 受け取りと candidate window 位置同期
- 修飾キー付き主要キーシーケンス / Ctrl 系 ASCII 制御文字
- セッションロギング（JSONL、ANSI 除去、秘密情報マスク、ZIP 圧縮）
- 自動テスト（parser / buffer / key encoding / mouse / OSC・CSI 応答 / ConPTY smoke test / surface 回帰）
