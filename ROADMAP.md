# ConPtyTerminal Roadmap

## 未実装・不足機能

### 高優先度

- **Sixel グラフィクスの描画** — `DCS ... q` はシーケンスを消費して無視しているが、画像は描画しない
- **iTerm2 インライン画像** — `OSC 1337;File=...` およびMultipartFileを消費して無視している
- **Kitty Graphics Protocol** — `APC ESC _ G ... ESC \\` を認識・描画しない

### 中優先度

- **DECUDK** — `DCS ... |` によるユーザー定義キー
- **VT52互換モード** — `CSI ? 2 h/l`、VT52形式のカーソル・座標・入力シーケンス
- **Kitty Keyboard Protocolの残り** — alternate key/base layout、KeyUp/KeyRepeat、associated text、Caps/NumLock、IME・キーボードレイアウト連携
- **XTWINOPSの表示系拡張** — `CSI 13/14/16 t` のウィンドウ位置・ピクセル寸法・文字セル寸法報告（WPF実測値との接続が必要）
- **OSC 8のメタデータ** — hyperlink parameters の id/URI属性保持。現在はURIと開閉だけをセルへ反映
- **OSC 52の選択対象分離** — clipboard / primary / secondary を個別に保持・応答。現在は対象文字列を通知するがOSクリップボードは単一

### 低優先度

- **SGR高度装飾** — 枠線、囲み、イデオグラム、上付き・下付き文字など
- **SGR互換属性の細分化** — `SGR 5/6` のslow/rapid blink区別、追加フォント属性、ANSI `SM/RM` のキーボード・印字モード
- **ESC互換シーケンス** — `ESC Z` DECID、`ESC %` character-set selection など未実装の7bit legacy controls
- **XTGETTCAPの実環境連携** — 現在はxterm-256color相当の固定capabilityを返すため、実際のterminfo/capability設定との同期が必要
- **DCS拡張** — DECUDK以外のDEC/HP系DCS（例: DECDLD、XTGETXRES）

## 対応方針・意図的な非対応

- **Sixel グラフィクス** — ConPTY の入出力経路では画像データとテキスト出力の順序・同期を安定して保証できないため実装しない。`DCS ... q` は画面へ文字として漏らさず消費して無視し、DA1でもSixel対応を広告しない
- **インライン画像（OSC 1337 / iTerm2 プロトコル）** — Sixelと同じ理由で実装しない。シーケンスは画面へ漏らさず消費して無視する
