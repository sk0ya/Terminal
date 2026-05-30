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

---

## 未実装・不足機能

### VT / ANSI シーケンス拡張

- ~~**アンダーラインスタイル・色** — `SGR 4:1`〜`4:5`（single/double/curly/dashed/dotted）, `SGR 58/59`（アンダーライン色）。現状は単純な on/off のみ~~ **実装済み**
- ~~**オーバーライン** — `SGR 53/55`。多くの TUI アプリが使用~~ **実装済み**
- **左右マージン（DECSLRM）** — `CSI s` / `DECSET 69`。上下スクロール領域は実装済みだが列方向マージンは未対応
- ~~**OSC 4** — カラーパレット（16 色）の上書きとクエリ。テーマ変更やコントラスト調整に必要~~ **実装済み（`rgb:rr/gg/bb` / `#rrggbb` 形式、クエリ応答、ハードリセット時に初期化）**
- ~~**OSC 7** — カレントディレクトリ通知。シェルが設定し、ターミナルが「同じ場所で新規タブ」に利用する標準プロトコル~~ **実装済み（Working Directory UI と連動）**
- ~~**OSC 9** — デスクトップ通知（Windows Toast）。長時間コマンドの完了通知に活用~~ **実装済み（WPF ポップアップバナー）**
- ~~**OSC 133 / 633** — シェル統合プロトコル（プロンプト・コマンド・出力のゾーンマーキング）。コマンドナビゲーション・意味的選択の前提~~ **実装済み（A/B/C/D マーカーを ShellCommandZoneReceived イベントで通知、Ctrl+Shift+↑/↓ でコマンドナビゲーション）**
- **DCS パーサ** — `ESC P...ST` の状態機械が未実装。Sixel / DECRQSS / DECUDK などが通らない
- **Sixel グラフィクス** — `DCS ...q` による画素画像インライン表示。Yazi や gnuplot などが使用
- **DECCOLM（mode 3）** — 80/132 列切り替え。一部の全画面アプリが発行する
- **SGR マウスピクセルモード（1016）** — セル単位でなくピクセル座標で報告するマウスモード

### キーボード入力

- **Kitty キーボードプロトコル** — `DECSET 2048` + CSI u ベースの高精度キーエンコード。Neovim 0.10+、Ghostty、WezTerm が標準採用。Shift/Ctrl+Enter 等の区別が可能になる
- **XTerm modifyOtherKeys** — `CSI > 4;2m` モード。Emacs・Vim が利用する拡張修飾キーシーケンス
- ~~**テンキー（数字パッド）** — `DECKPAM`/`DECKPNM`（mode 66）の切り替えはフラグのみ存在するが、数字パッドキーの SS3 シーケンス出力が未実装~~ **実装済み**

### テキスト選択・クリップボード

- **矩形選択（ブロック選択）** — Alt+ドラッグで列ブロック選択。カラムコピーに必須
- ~~**ダブルクリック・ワード選択** — 単語境界での自動選択~~ **実装済み**
- ~~**トリプルクリック・行選択** — 1 行全体を選択~~ **実装済み**
- ~~**Shift+矢印キー選択** — キーボードによる範囲拡張~~ **実装済み**
- ~~**マルチライン貼り付け確認** — ブラケットペースト（mode 2004）未対応アプリへの複数行貼り付け前の確認ダイアログ~~ **実装済み（BracketedPaste 無効時に改行含むテキストをキャンセル確認）**

### シェル統合

- ~~**OSC 133/633 コマンドナビゲーション** — OSC 133 実装後に有効化。Ctrl+Shift+↑/↓ で前後のプロンプト行へジャンプ~~ **実装済み**
- ~~**コマンド終了コード表示** — OSC 133 `D;exitCode` を受け取りタブやプロンプト領域に結果を表示~~ **実装済み（非ゼロ終了時にステータスバー表示）**
- **「同ディレクトリで新規タブ」** — OSC 7 から得たパスを新タブ起動に渡す

### UI / UX

- **設定 UI** — 現状 JSON 直接編集のみ。フォント・配色・プロファイル・キーバインドを GUI で変更できる設定画面
- **カラースキーム / テーマ** — OSC 4 と連動した 16 色パレット定義。ダーク/ライト/カスタムテーマの切り替え
- **キーバインドカスタマイズ** — コピー・貼り付け・タブ操作などのショートカットをユーザーが変更可能にする
- **ペイン分割** — 1 タブ内での水平・垂直ペイン分割。tmux 不要のマルチペイン操作
- **タブのドラッグ並べ替え** — タブストリップ上でドラッグ&ドロップ並べ替え
- **ウィンドウ分離** — タブをドラッグして別ウィンドウへ切り離し

### 描画 / レンダリング

- **フォントリガチャ** — FiraCode・Cascadia Code のリガチャ（`->`, `=>`, `!=` 等を合字表示）
- **フォントフォールバック** — 主フォントにないグリフを CJK フォントや絵文字フォントで補完
- **GPU アクセラレーション** — DirectComposition / Direct2D を使った高フレームレート描画。大量テキスト更新時のドロップフレーム解消
- **Mica / Acrylic 背景** — Windows 11 ウィンドウ素材 API を使った半透明背景エフェクト
- **インライン画像（OSC 1337 / iTerm2 プロトコル）** — Sixel と並ぶもう一方の画像表示プロトコル

### パフォーマンス

- **スクロールバック仮想化** — 数万行を超えるバッファの描画・選択を仮想スクロールで省メモリ化
- **パーサスループット** — 大量出力時（`cat` 大ファイル等）のバッファ書き込みを SIMD / バッチ化して CPU 使用率を削減

### アクセシビリティ

- **UI Automation 対応** — Windows Narrator・NVDA 等のスクリーンリーダーがターミナル出力を読み上げられるよう、UIA テキストパターンを実装
- **ハイコントラストテーマ** — Windows システムのハイコントラスト設定に追従した配色
