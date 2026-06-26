---
name: publish-nuget
description: Publish the sk0ya.Terminal.Controls NuGet package to nuget.org. Use when asked to release/publish the NuGet package, bump the package version, or "nuget更新/公開". Covers version bump, pack, bundling check, push, and git push.
---

# Publish Terminal NuGet package

Releases `sk0ya.Terminal.Controls` to nuget.org. There is no CI publish workflow
(it was removed), so publishing is manual.

`Terminal.Core` is **no longer published as a standalone package** (`IsPackable=false`).
Its DLL is bundled inside `sk0ya.Terminal.Controls`: the `ProjectReference` uses
`PrivateAssets="all"` and an `IncludeReferencedProjectsInPackage` target folds
`Terminal.Core.dll` into `lib/`, so the packed nuspec has **no** Core dependency.
There is only one package to bump and push.

## Version bump

Bump the single `<Version>` in `src/Terminal.Controls/Terminal.Controls.csproj`
(a patch bump, e.g. `1.0.22 → 1.0.23`, unless told otherwise) regardless of whether
the change was in `Terminal.Core/**` or `Terminal.Controls/**` — Core ships inside
the Controls package now. Leave `Terminal.Core`'s `<Version>` alone; it is unused for packaging.

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

4. **Verify the bundling** in the produced nupkg before pushing — `Terminal.Core.dll`
   must be present in `lib/` and the nuspec must have an **empty** `<dependencies>` group
   (no `sk0ya.Terminal.Core` dependency):
   ```pwsh
   Add-Type -AssemblyName System.IO.Compression.FileSystem
   $z=[System.IO.Compression.ZipFile]::OpenRead("artifacts/packages/sk0ya.Terminal.Controls.<X.Y.Z>.nupkg")
   $z.Entries | ForEach-Object { $_.FullName }   # expect lib/.../Terminal.Core.dll + Terminal.Controls.dll
   $e=$z.Entries | Where-Object { $_.Name -like "*.nuspec" }
   $sr=New-Object System.IO.StreamReader($e.Open()); $sr.ReadToEnd(); $sr.Close(); $z.Dispose()
   ```

5. **Push to nuget.org** — the API key is in `$env:NUGET_API_KEY`. Publishing is irreversible
   (a version cannot be overwritten or re-uploaded), so only run this after the user has asked
   to publish:
   ```pwsh
   dotnet nuget push artifacts/packages/sk0ya.Terminal.Controls.<X.Y.Z>.nupkg `
     --api-key $env:NUGET_API_KEY `
     --source https://api.nuget.org/v3/index.json `
     --skip-duplicate
   ```

6. **Push commits to GitHub**:
   ```pwsh
   git push origin main
   ```

## Notes

- Package URL after indexing (a few minutes): `https://www.nuget.org/packages/sk0ya.Terminal.Controls/<X.Y.Z>`
- `--skip-duplicate` makes a re-run safe if that exact version was already pushed.
- If `$env:NUGET_API_KEY` is unset, stop and ask the user for the key — do not guess.
