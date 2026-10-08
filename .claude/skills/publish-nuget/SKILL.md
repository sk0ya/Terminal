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

The version lives in the single `<Version>` in the root `Directory.Build.props` (the
PropertyGroup conditioned on `Terminal.Core`/`Terminal.Controls`), regardless of whether
the change was in `Terminal.Core/**` or `Terminal.Controls/**`. Core ships inside the
Controls package, so both assemblies share that one version. The csproj files do not
carry a `<Version>`.

**Decide the version from nuget.org, not from `Directory.Build.props` alone.** The props
value can be ahead of what was actually published (e.g. a bump commit was made but the
push never happened — this once caused 1.0.40 to be skipped: props said 1.0.40, it was
never published, and blindly bumping produced 1.0.41). Never leave a gap in the published
version sequence.

Fetch the latest published version (the flat-container index includes unlisted versions):
```pwsh
$published = (Invoke-RestMethod https://api.nuget.org/v3-flatcontainer/sk0ya.terminal.controls/index.json).versions
$latest = $published[-1]; $latest
Select-String -Path Directory.Build.props -Pattern '<Version>'
```
Then:
- **props == latest published** → normal case. Patch-bump (e.g. `1.0.22 → 1.0.23`, unless
  told otherwise) and make the bump commit (step 2).
- **props == latest published + one patch, and that version is not in `$published`** → a
  previous bump was never published. **Do not bump again**; reuse the props version as-is
  and skip the bump commit in step 2 (the existing `Bump ... to <X.Y.Z>` commit stays).
- **anything else** (props behind nuget.org, or more than one version ahead) → stop and
  report the mismatch to the user instead of guessing.

Also check `git log origin/main..main` — unpushed `Bump ...` commits are a sign of the
"bumped but never published" case above.

## Steps

1. **Sanity check** — confirm the working tree is committed and tests pass:
   ```pwsh
   dotnet test tests/Terminal.Tests/Terminal.Tests.csproj -c Debug --nologo
   ```

2. **Bump version** — only if the version check above says to bump (skip this step when
   reusing an unpublished props version). Edit the `<Version>` in `Directory.Build.props`, then commit:
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
   (a version cannot be overwritten or re-uploaded). **Invoking this skill IS the request to
   publish** — run this step directly once steps 1–4 pass; do not pause to ask the user for
   confirmation before pushing. (Only stop if a sanity/bundling check actually failed.)
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
