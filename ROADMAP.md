# ConPtyTerminal Roadmap

## 未実装・不足機能

### 高優先度

- **iTerm2 インライン画像** — 対応済み・実機で描画確認。PNG/JPEG の `OSC 1337;File=...` をセル位置へ描画する
- **Sixel グラフィクスの描画** — デコーダ・描画は実装済みだが **ConPTY がシーケンスを破棄するため実機では表示されない**（下記）
- **Kitty Graphics Protocol** — 同上。実装済みだが ConPTY に破棄される

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
- **ESC互換シーケンス** — `ESC %` character-set selection など未実装の7bit legacy controls
- **XTGETTCAPの実環境連携** — 現在はxterm-256color相当の固定capabilityを返すため、実際のterminfo/capability設定との同期が必要
- **DCS拡張** — DECUDK以外のDEC/HP系DCS（例: DECDLD、XTGETXRES）

## 対応方針・意図的な非対応

### ConPTY が通すプロトコル（実測: Windows 11 23H2 / build 22631）

pty へ各シーケンスを流し、ConPTY が再出力するストリームを直接観測した結果:

| プロトコル | ConPTY 通過 |
| --- | --- |
| `OSC 1337;File=`（iTerm2） | **通る**（64KB ペイロードでも欠落なし） |
| `DCS ... q`（Sixel） | **破棄される** |
| `APC ESC _ G ...`（Kitty） | **破棄される** |

実機で画像を出す最小手順（PowerShell、実測で動作確認済み）。**1行で書く** — 複数行の関数定義は貼り付け時に PowerShell の継続ブロックへ吸い込まれるため:

```powershell
function Show-Image { param([Parameter(Mandatory)][string]$Path,[int]$Width=40,[int]$Height=20); $e=[char]27; $d=[Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path $Path))); [Console]::Out.Write($e+']1337;File=inline=1;width='+$Width+';height='+$Height+':'+$d+[char]7); [Console]::Out.Write("`n"*$Height) }
```

末尾の `"`n"*$Height` は必須。画像はカーソルを動かさないので、画像が占める行はスクリプト側で送る。この改行は ConPTY も認識するため、両者のカーソルがそろって進む。

`wezterm imgcat` は `CSI 14 t`（ピクセル寸法）の応答を待つため現状ハングする。中優先度の XTWINOPS 表示系拡張を実装すれば使えるようになる見込み。

Sixel と Kitty はこちらのパーサに到達しないため、実装があっても実機では表示されない。`CreatePseudoConsole` に `PSEUDOCONSOLE_PASSTHROUGH_MODE`(8) を渡しても当該ビルドでは挙動が変わらなかった。実機で画像を出すには `chafa -f iterm` や `wezterm imgcat` など **iTerm2 形式で出力するツールを使う**。Sixel/Kitty を通すには、Sixel を実装した新しい conhost/conpty（Windows Terminal 同梱の OpenConsole 等）へ差し替える必要がある — 未検証。

### 代替画面と Claude Code（実測: claude.exe 2.1.233 / pwsh 7.6.3）

ConPTY は `CSI ?1049h` を**通さず自前で処理する**。代替バッファは conhost 側にあり、こちらのパーサには一切届かない。そのため全画面アプリの検出はタイトル（`OSC 0`）に頼っている（擬似代替画面）。実測したタイトルの流れ:

```
C:\Program Files\...\pwsh.exe   ← シェル
claude → ✳ Claude Code → ◐ Claude Code → ◑ Say hi in one word → ◐ Say hi… → ✳ Say hi…   ← 実行中
（空）→ C:\Program Files\...\pwsh.exe   ← 終了
```

**Claude Code は作業中にウィンドウタイトルを作業内容へ書き換え、先頭のスピナー字形（✳ ◐ ◑ …）を回す。** 「Claude らしくないタイトルになった＝終了」と判定すると、まだ描画中のアプリの下で主画面を復元してしまい、以降の絶対位置指定がすべて別の行に落ちる（画像を出していると、プロンプトやカーソルが画像の帯に重なって見える）。終了の判定は**タイトルが空になる**か、**起動前のシェルのタイトルへ戻る**ことで行う（後者は異常終了時の復帰も兼ねる）。

終了時、ConPTY は保存していた主画面ビューポートを**同じ行位置へ1行ずつ描き直してから**タイトルを落とす。したがってテキストは ConPTY 側が復元し、画像はこちらのアンカー行に残る — 両者は一致する。

### 描画・カーソル

- 画像は端末行のセルへアンカーして保持し、既存のスクロールバックと WPF surface の描画サイクルに乗せる。テキストの背面へ描画する。
- **画像はカーソルを一切動かさない。** ConPTY は通常モード（`CreatePseudoConsole` の flags=0）で動作しており、画像シーケンスは ConPTY 自身の画面モデルには反映されないため、ConPTY のカーソルは画像の高さぶん進まない。こちらだけカーソルを進めると両者のモデルがずれ、絶対位置指定（`ESC[…H`）で再描画するプログラムは ConPTY のカーソルを基準に行を計算するため、テキストが画像の上に落ちる。これは2026-06-20に画像対応を一度削除した原因そのものなので、オーバーレイに徹して同期を保つ。
  - 帰結として `imgcat` のようにテキストが流れる用途では、後続のプロンプトが画像の行へ重なって描かれる。ConPTY をパススルーモードで動かせるようになるまでは解消できない。
  - 同じ理由で、各プロトコルのカーソル移動抑制指定（iTerm2 の `doNotMoveCursor`、Kitty の `C=1`）は常時有効と同じ扱いになる。
- 消去との関係：`ED 0/1/2/3` は消した行の画像も削除する。`EL` は削除しない（画像はプロンプト行にアンカーされるため、プロンプト再描画のたびに消えてしまう）。
- Kitty の高度なアニメーション、Unicode placeholder による配置、`a=d` の一部サブモード、iTerm2 のファイル参照形式は未対応。`inline=1` のない `OSC 1337;File=` はファイル転送なので描画しない。壊れた画像データは画面を壊さず無視する。
