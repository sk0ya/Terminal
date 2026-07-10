# Terminal

Windows 向けの WPF ターミナルエミュレーター。ConPTY（Pseudo Console）を使って `cmd.exe`、`powershell`、`pwsh`、`vim` などの CLI / TUI アプリを動かせます。

## 特徴

- **ConPTY ベース**: Win32 の疑似コンソール API により、ほぼすべての Windows コンソールアプリと互換
- **VT/ANSI 対応**: CSI / OSC シーケンス、カーソル制御、256色、alternate screen、bracketed paste など
- **カスタム描画**: `RichTextBox` / `FlowDocument` に依存しない `TerminalSurfaceControl` による高速な行単位描画
- **タブ管理**: 複数セッションをタブで切り替え、プロファイルごとに起動コマンドを設定可能
- **日本語 IME 対応**: composition window の位置同期と WPF input proxy による IME 入力
- **マウス**: legacy / SGR マウストラッキング、DEC special graphics、OSC 52 クリップボード対応
- **Unicode**: grapheme cluster、ZWJ emoji、variation selector、国旗ペアの基本処理
- **テキスト検索 / 選択 / コピー**: surface 内で完結する範囲選択とクリップボードコピー
- **設定 UI**: フォント、タブ位置、プロファイルを GUI で変更可能

## 動作要件

| 項目 | 要件 |
|------|------|
| OS | Windows 10 1809 以降（ConPTY サポート必須） |
| ランタイム | .NET 9.0 |
| フレームワーク | WPF |

## ビルド

```powershell
dotnet restore Terminal.sln
dotnet build Terminal.sln
dotnet run --project src/Terminal.App/Terminal.App.csproj
```

テストの実行:

```powershell
dotnet test Terminal.sln
```

## プロジェクト構成

```
Terminal/
├── src/Terminal.Core/       # ConPTY session、設定、ログ、Unicode
├── src/Terminal.Controls/   # WPF terminal surface、buffer、tabs、input
├── src/Terminal.App/        # 実行アプリとWindow code-behind
└── tests/Terminal.Tests/    # 単体テストとConPTY smoke test
```

依存方向は `Terminal.App` → `Terminal.Controls` → `Terminal.Core` です。
`sk0ya.Terminal.Controls` NuGetパッケージには`Terminal.Core.dll`も同梱されます。

パッケージ生成:

```powershell
dotnet pack src/Terminal.Controls/Terminal.Controls.csproj -c Release -o artifacts/packages
```

## 検証対象アプリ

以下で継続的に動作確認しています。

- `cmd.exe`
- `powershell` / `pwsh`
- `vim`
- `less`
- `fzf`
- `git log --decorate --graph`

## 開発状況

詳細は [ROADMAP.md](ROADMAP.md) を参照してください。

| フェーズ | 内容 | 状況 |
|----------|------|------|
| Phase 1 | 入力の完成度（IME・修飾キー・マウス） | 進行中 |
| Phase 2 | 差分描画とパフォーマンス改善 | 進行中 |
| Phase 3 | Unicode 幅計算の正確性 | 未着手 |
| Phase 4 | VT / xterm 互換性の強化 | 未着手 |
| Phase 5 | セッション層の強化 | 未着手 |
| Phase 6 | テスト・検証基盤の整備 | 一部完了 |
