---
name: publish-nuget
description: Publish the sk0ya.Terminal.Controls (and optionally sk0ya.Terminal.Core) NuGet package to nuget.org. Use when asked to release/publish the NuGet package, bump the package version, or "nuget更新/公開". Covers version bump, pack, dependency check, push, and git push.
---

# Publish Terminal NuGet package

Releases `sk0ya.Terminal.Controls` to nuget.org. There is no CI publish workflow
(it was removed), so publishing is manual. `Terminal.Core` is a separate package
(`sk0ya.Terminal.Core`) that `Terminal.Controls` depends on via a `ProjectReference`,
which `dotnet pack` turns into a package dependency.

## When to bump which package

- Change only in `src/Terminal.Controls/**` → bump **Controls** only. Leave `Terminal.Core`'s
  version as-is; the packed Controls nuspec will keep depending on the already-published Core.
- Change in `src/Terminal.Core/**` → bump **both** Core and Controls, and publish Core first
  (Controls' dependency must point at a version that already exists on nuget.org).

Versions live in the `<Version>` element of each csproj:
- `src/Terminal.Controls/Terminal.Controls.csproj`
- `src/Terminal.Core/Terminal.Core.csproj`

Use a patch bump (e.g. `1.0.16 → 1.0.17`) unless told otherwise.

## Steps

1. **Sanity check** — confirm the working tree is committed and tests pass:
   ```pwsh
   dotnet test tests/Terminal.Tests/Terminal.Tests.csproj -c Debug --nologo
   ```

2. **Bump version** — edit the `<Version>` in the relevant csproj(s), then commit:
   ```pwsh
   git commit -am "Bump Terminal.Controls package to <X.Y.Z>"
   ```
   (Match the existing commit-message style: `Bump <Package> package to <X.Y.Z>`.)

3. **Pack (Release)** into `artifacts/packages`:
   ```pwsh
   dotnet pack src/Terminal.Controls/Terminal.Controls.csproj -c Release -o artifacts/packages --nologo
   ```

4. **Verify the dependency** in the produced nuspec points at a published Core version
   before pushing (guards against shipping a package that references an unpublished Core):
   ```pwsh
   Add-Type -AssemblyName System.IO.Compression.FileSystem
   $z=[System.IO.Compression.ZipFile]::OpenRead("artifacts/packages/sk0ya.Terminal.Controls.<X.Y.Z>.nupkg")
   $e=$z.Entries | Where-Object { $_.Name -like "*.nuspec" }
   $sr=New-Object System.IO.StreamReader($e.Open()); $sr.ReadToEnd(); $sr.Close(); $z.Dispose()
   ```
   Confirm `<dependency id="sk0ya.Terminal.Core" version="..." />` is a version already on nuget.org.

5. **Push to nuget.org** — the API key is in `$env:NUGET_API_KEY`. Publishing is irreversible
   (a version cannot be overwritten or re-uploaded), so only run this after the user has asked
   to publish:
   ```pwsh
   dotnet nuget push artifacts/packages/sk0ya.Terminal.Controls.<X.Y.Z>.nupkg `
     --api-key $env:NUGET_API_KEY `
     --source https://api.nuget.org/v3/index.json `
     --skip-duplicate
   ```
   If publishing Core too, push its nupkg first with the same command.

6. **Push commits to GitHub**:
   ```pwsh
   git push origin main
   ```

## Notes

- Package URL after indexing (a few minutes): `https://www.nuget.org/packages/sk0ya.Terminal.Controls/<X.Y.Z>`
- `--skip-duplicate` makes a re-run safe if that exact version was already pushed.
- If `$env:NUGET_API_KEY` is unset, stop and ask the user for the key — do not guess.
