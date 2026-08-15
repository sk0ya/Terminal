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

### 生ストリームの採取

表示の崩れは ConPTY のバイト列を再生できれば確定できる。`TERMINAL_RAW_CAPTURE` にディレクトリを設定してアプリを起動すると、セッションごとに `raw-<日時>-<pid>.txt` が作られ、**pty の出力をエスケープシーケンスごとそのまま**記録する（セッションログと違い ANSI 除去も秘匿化もしない）。こちらから送った入力とリサイズは ` <IN>…</IN> ` で囲んで同じ流れに混ぜてあるので、再生時に区別できる。未設定なら一切動かない。

### セル格子とフォントの字送り（実測: HackGenNerd Console 14pt / Cascadia Mono 14pt）

**グリフはフォントの advance ではなく、幅テーブルが割り当てたセル数で送る。** 端末の1行は「フォントが持っている字送り」ではなく「セルの並び」であり、両者は一致しない:

| 文字 | 用途 | 幅テーブル | HackGen | Cascadia の解決先 |
| --- | --- | --- | --- | --- |
| `⚠` U+26A0 | 警告行 | 1 | **2.0 セル** | Segoe UI Emoji 2.34 |
| `⎿` U+23BF | ツール結果 | 1 | **2.0 セル** | Yu Gothic UI 1.71 |
| `✓ ✔ ✗ ✘` | 結果マーク | 1 | **2.0 セル** | Segoe UI Emoji 1.39〜2.34 |
| `⏺ ✳ ✻ ✴` | 発言頭・スピナー | 1 | グリフ無し | Segoe UI Emoji 1.68〜2.60 |
| `⏵` U+23F5 | ステータス行 | 1 | **グリフ無し**（フォールバック6書体にも無い） | 同左 |

East Asian Ambiguous を全角で描く日本語フォント（HackGen 等）では、これらが**主フォントに存在する**ためフォールバック判定では検出できない。1セグメントを1つの `FormattedText` に渡すと、そのグリフ以降が行末まで1セル右へずれ、背景・選択・カーソルだけが格子に残る（Claude Code は上記の記号を各行の先頭に置くため被害が大きい）。

対策として `TerminalSurfaceControl` は `GlyphRun` にセル幅を `advanceWidths` として明示的に渡す。書記素クラスタが単一スカラー値でない場合（結合文字列・絵文字シーケンス）と、どの書体にもグリフが無い場合だけ `FormattedText` へ回し、そのクラスタ単独で自セルに描く。セル幅を超えるグリフは**直後の空白セルを借り**、それでも収まらない分だけ等倍縮小する（横だけ潰すと字形が壊れるため）。下線・打ち消し線・上線もグリフではなくセグメントのセル範囲に対して引く。副次的に全画面再描画は 5.35ms → 0.27ms になった（120x40、`DrawText` の再フォーマットが消えるため）。

### 最終桁の遅延折り返し（deferred wrap）

**最終桁へ文字を書いてもカーソルはそこに留まり、折り返しは次の印字文字が行う。** この保留状態を落としてよいのは**カーソルを置く操作と行を編集する操作だけ**で、属性変更・問い合わせ・モード切替は落としてはならない。以前は `DecodeCsi` の先頭で無条件に落としていたため、行幅いっぱいの罫線の直後に `CSI` が1つでも挟まると、折り返すはずの文字が前行の最終セルへ上書きされた。

実害が出たのは Claude Code の入力欄で、pty のバイト列はこうなっている:

```
CSI ?2026h                     ← 同期更新の開始
CSI 1;1H ──…──（行幅ちょうど）  ← 罫線。ここで折り返しが保留になる
CSI ?25l  SGR色  ❯ Try …        ← カーソル非表示・色 → その後に来る ❯ が折り返すはず
CSI ?25h  CSI ?2026l
```

`?2026h` / `?25l` / SGR のどれで落としても `❯` は罫線の最終セルに重なって描かれ、罫線に埋もれて見えなくなる。同時に入力行が1セル右へずれる。

保留を維持する CSI: `m`（SGR・modifyOtherKeys）、`n`（DSR）、`c`（DA）、`i`（Media Copy）、`t`（XTWINOPS）、`q`（カーソル形状・DECSCA・XTVERSION）、`h`/`l`（モード）、`u`（kitty keyboard・SCORC）、`s`（SCOSC。ただし DECSLRM はカーソルをホームへ動かすので除く）。位置を動かすモード（DECCOLM・DECOM・代替画面・save/restore）は各自で保留状態を管理する。`ESC 7` / `ESC 8` を除外しているのと同じ理由。

### 描画・カーソル

- 画像は端末行のセルへアンカーして保持し、既存のスクロールバックと WPF surface の描画サイクルに乗せる。テキストの背面へ描画する。
- **画像はカーソルを一切動かさない。** ConPTY は通常モード（`CreatePseudoConsole` の flags=0）で動作しており、画像シーケンスは ConPTY 自身の画面モデルには反映されないため、ConPTY のカーソルは画像の高さぶん進まない。こちらだけカーソルを進めると両者のモデルがずれ、絶対位置指定（`ESC[…H`）で再描画するプログラムは ConPTY のカーソルを基準に行を計算するため、テキストが画像の上に落ちる。これは2026-06-20に画像対応を一度削除した原因そのものなので、オーバーレイに徹して同期を保つ。
  - 帰結として `imgcat` のようにテキストが流れる用途では、後続のプロンプトが画像の行へ重なって描かれる。ConPTY をパススルーモードで動かせるようになるまでは解消できない。
  - 同じ理由で、各プロトコルのカーソル移動抑制指定（iTerm2 の `doNotMoveCursor`、Kitty の `C=1`）は常時有効と同じ扱いになる。
- 消去との関係：`ED 0/1/2/3` は消した行の画像も削除する。`EL` は削除しない（画像はプロンプト行にアンカーされるため、プロンプト再描画のたびに消えてしまう）。
- Kitty の高度なアニメーション、Unicode placeholder による配置、`a=d` の一部サブモード、iTerm2 のファイル参照形式は未対応。`inline=1` のない `OSC 1337;File=` はファイル転送なので描画しない。壊れた画像データは画面を壊さず無視する。
