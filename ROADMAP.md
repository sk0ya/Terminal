# ConPtyTerminal Roadmap

## 未実装・不足機能

### 高優先度

- **Sixel グラフィクスの描画** — `DCS ... q` はシーケンスを消費して無視しているが、画像は描画しない
- **iTerm2 インライン画像** — `OSC 1337;File=...` およびMultipartFileを消費して無視している
- **Kitty Graphics Protocol** — `APC ESC _ G ... ESC \\` を認識・描画しない

### 中優先度

- **XTGETTCAP / XTSETTCAP** — `DCS +q` / `DCS +p` によるterminfo能力の問い合わせ・設定
- **DECUDK** — `DCS ... |` によるユーザー定義キー
- **CSI拡張の一部** — HPR (`CSI Ps a`)、VPR (`CSI Ps e`)、横スクロール、列挿入・削除など
- **8-bit C1制御文字の一部** — IND (`0x84`)、NEL (`0x85`)、HTS (`0x88`)、RI (`0x8D`) など

### 低優先度

- **OSC 1** — アイコン名の設定
- **SGR高度装飾** — 枠線、囲み、イデオグラム、上付き・下付き文字など
- **APC / PM / SOSの7-bit形式** — C1形式は内容を無視するが、`ESC _` / `ESC ^` / `ESC X` の形式は専用処理していない

## 対応方針・意図的な非対応

- **Sixel グラフィクス** — ConPTY の入出力経路では画像データとテキスト出力の順序・同期を安定して保証できないため実装しない。`DCS ... q` は画面へ文字として漏らさず消費して無視し、DA1でもSixel対応を広告しない
- **インライン画像（OSC 1337 / iTerm2 プロトコル）** — Sixelと同じ理由で実装しない。シーケンスは画面へ漏らさず消費して無視する
