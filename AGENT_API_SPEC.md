# Terminal — エージェント連携API 仕様書

> 依頼元: **sk0ya.AgentStudio**（AIエージェント搭載のWPFアプリ。`C:\Projects\Workspace`）
> 作成日: 2026-05-31
> 対象: `Terminal.Controls`（`Terminal.Tabs.TerminalTabView`）

---

## 1. 目的 / 背景

AgentStudio では、AIエージェントが **対話型ターミナル（`TerminalTabView`）そのもの**を使って
コマンドを実行し、その**出力と終了コード**を取得したい。
人間が打つのと同じ画面で動くため、AIの操作がそのまま可視化される（理想形）。

現状の `TerminalTabView` の公開APIには「プログラムからコマンドを実行し、出力／終了コードを取得する」
口が無い（`ctor` / `CloseAsync` / `FocusTerminal` / `HeaderTitle` / `WorkingDirectory` のみ）。
そこで暫定的に AgentStudio 側は別プロセス（PowerShell）で実行しているが、これを廃し
**この仕様の API を `TerminalTabView` に追加**して一本化したい。

---

## 2. 朗報：必要な土台はすでに実装済み

このリポジトリのコードを読んだところ、実装に必要な部品はほぼ揃っている：

| 部品 | 場所 | 役割 |
|------|------|------|
| `ITerminalSession.Write(string)` | `Terminal.Core/Sessions/ITerminalSession.cs` | コマンド文字列を流し込める |
| `ITerminalSession.OutputReceived` | 同上 | 出力ストリームを購読できる |
| `ITerminalSession.Exited` | 同上 | プロセス終了（exitコード）を取れる |
| **OSC 133 シェル統合** | `Buffer/AnsiTerminalBuffer.cs` | `ShellCommandZoneReceived` で A/B/C/D を解釈済み |
| `ShellCommandZoneEventArgs` | 同上 (`internal`) | `ZoneType` / `AbsoluteLine` / **`ExitCode`** を保持 |
| `ShellCommandZoneType` | 同上 (`internal`) | `PromptStart`(A)/`CommandStart`(B)/`CommandExecuted`(C)/`CommandDone`(D) |

つまり **「コマンド境界＋終了コード」を取る仕組みは既にある**（`TerminalTabView` 内で
`TerminalBuffer_ShellCommandZoneReceived` が `CommandDone` の `ExitCode` を受け取っている）。
これを使えば確実な `RunCommandAsync` が作れる。

---

## 3. 追加してほしい公開API

`Terminal.Tabs.TerminalTabView` に以下を追加：

```csharp
namespace Terminal.Tabs;

public partial class TerminalTabView
{
    /// <summary>
    /// 対話シェルにコマンドを送信し、完了まで待って結果を返す。
    /// 画面（PTY）上でも通常どおり実行・表示される。
    /// </summary>
    /// <remarks>
    /// 同時に1つだけ実行可能（既に実行中なら InvalidOperationException か、内部でキュー）。
    /// セッション未起動時は IsStarted=false の結果、もしくは例外。
    /// </remarks>
    public Task<TerminalCommandResult> RunCommandAsync(
        string command,
        CancellationToken cancellationToken = default);

    /// <summary>シェル統合（OSC133）が有効で RunCommandAsync が完了検知できる状態か。</summary>
    public bool IsShellIntegrationActive { get; }
}

public readonly record struct TerminalCommandResult(
    string Command,
    string Output,     // コマンド出力（ANSIエスケープ除去済みプレーンテキスト推奨）
    int ExitCode,      // OSC133 D の値。取れない場合は -1
    bool Completed);   // 正常に完了検知できたか（false=タイムアウト/未統合）
```

### 公開範囲の変更
`RunCommandAsync` を実装する上で、現在 `internal` の以下を **`public` に昇格**するか、
あるいは内部利用に留めて結果型だけ公開するか、どちらでも可（後者を推奨）：
- 推奨：`ShellCommandZoneType` / `ShellCommandZoneEventArgs` は `internal` のまま、
  `RunCommandAsync` の内部実装だけがそれらを参照し、外には `TerminalCommandResult` のみ公開。

---

## 4. 推奨実装

### 4-1. シェル統合ベース（第一候補・最も確実）
`TerminalTabView` 内に実行コンテキストを持たせ、`ShellCommandZoneReceived` で境界を取る：

1. `RunCommandAsync(cmd)`:
   - 実行中フラグを立て、`TaskCompletionSource<TerminalCommandResult>` を用意。
   - `_session.Write(cmd + "\r")` で送信。
   - 出力収集を開始（下記）。
2. 出力収集：`_session.OutputReceived`（または `FlushPendingOutput` 経路）で、
   **`CommandExecuted`(C) を受けてから `CommandDone`(D) までの間**のテキストを蓄積。
   - 蓄積テキストから ANSI エスケープを除去してプレーン化（既存のバッファ処理を流用可）。
   - もしくは `AnsiTerminalBuffer` の該当行範囲（`AbsoluteLine` C→D）からプレーン行を抽出。
3. `CommandDone`(D) 受信時：`ExitCode` を確定し、`TaskCompletionSource` を完了。
4. `CancellationToken`：キャンセル時は ``(Ctrl+C) 相当を送って中断、`Completed=false`。
5. タイムアウト/未統合：一定時間 D が来なければ `Completed=false`, `ExitCode=-1` で返す。

> 既存の `_shellCommandLines` / `TryScrollToAdjacentCommandLine` と同じゾーン情報を使えるはず。

### 4-2. シェル統合が無効な環境のフォールバック（センチネル方式）
ユーザーのシェルが OSC133 を出さない場合に備え、`IsShellIntegrationActive=false` のときは：
- 送信を `"{cmd}; Write-Host \"__ASE_$LASTEXITCODE\"\r"`（PowerShell例）のように
  **完了マーカー＋終了コード**を付与し、出力ストリームから `__ASE_<code>` を検出して完了とみなす。
- マーカー行は結果 `Output` から除去する。

> **実装済み（pwsh のみ）：** `ShellIntegration.PrepareLaunch`（Terminal.Core）がセッション起動時に
> `%LOCALAPPDATA%\Terminal\shell-integration.ps1` を `-NoExit -Command` で dot-source させる
> （prompt ラップで A/B/D、PSConsoleHostReadLine ラップで C を emit）。
> `TerminalTabView.ShellIntegrationInjectionEnabled`（既定 true、設定 `EnableShellIntegrationInjection` 連動）で
> オプトアウト可能。`ShellIntegration.CanInject` / `DefaultScriptPath` で注入可否・スクリプトパスを取得できる。
> `-Command` / `-File` 等を含む非対話起動、powershell.exe・cmd・Git Bash には注入しない
> （センチネル方式にフォールバック）。

---

## 5. 受け入れ条件（テスト観点）

- [ ] `await view.RunCommandAsync("echo hello")` → `Output` に `hello`、`ExitCode==0`、`Completed==true`
- [ ] 失敗コマンド（例 `cmd /c exit 3` 相当）→ `ExitCode==3`
- [ ] 実行中も**画面に通常どおり表示**される（人間が見て分かる）
- [ ] 長時間コマンドを `CancellationToken` でキャンセル → 中断され `Completed==false`
- [ ] 複数回連続実行しても結果が混ざらない（直列実行 or キューイング）
- [ ] シェル統合無効環境でもフォールバックで `ExitCode` が取れる
- [ ] `IsShellIntegrationActive` が実態を反映

---

## 6. AgentStudio 側の対応（こちらで実施）

API が入れば、AgentStudio の `TerminalService`（`AgentStudio.Services`）を数行差し替えるだけ：

```csharp
// 現在：独立 Process で実行 → 変更後：可視ターミナルで実行
public async Task<CommandResult> RunCommandAsync(string command, CancellationToken ct)
{
    var r = await _view.RunCommandAsync(command, ct);   // ← これに置き換え
    var result = new CommandResult(r.Command, r.Output, r.ExitCode, CurrentDirectory, r.ExitCode == 0);
    CommandExecuted?.Invoke(this, result);
    return result;
}
```

`ITerminalService` 抽象は既に `Task<CommandResult> RunCommandAsync(string, CancellationToken)` の形なので、
本体ロジックへの影響は無し。`run_command` ツール（AIの手足）からそのまま使われる。

---

## 7. 補足・相談したい点

1. **作業ディレクトリの動的変更**：現在 `WorkingDirectory` は起動時指定。
   ワークスペースのフォルダを開いた時に**実行中セッションのCWDを追従**させたい
   （`cd` を送る／再起動する等、どれが望ましいか）。
2. 出力の形式：`Output` は ANSI 除去済みプレーンテキストを希望（AIに渡すため）。
   生バイトが要るケースは無い。
3. まずは 4-1（シェル統合ベース）だけで十分。フォールバックは後追いでも可。

以上。質問があれば AgentStudio 側の設計書 `C:\Projects\Workspace\docs\設計書.md` も参照ください。
