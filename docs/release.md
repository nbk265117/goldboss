# Build and release

## Requirements

Windows 10 19041+ x64, .NET 10 SDK (the scripts find it on PATH or in `%USERPROFILE%\.dotnet`; install with
`powershell -c "& ([scriptblock]::Create((irm https://dot.net/v1/dotnet-install.ps1))) -Channel 10.0"`).
Visual Studio is optional (open `DofusCompanion.slnx`, VS 2022 17.13+ or VS 2026).

## Commands

```powershell
.\scripts\build.ps1               # restore + Release build, warnings are errors
.\scripts\test.ps1                # all tests (+ localization check); TRX in artifacts\test-results
.\scripts\publish.ps1             # tests, then self-contained win-x64 publish + zip
.\scripts\publish.ps1 -SkipTests
```

Output: `artifacts\publish\DofusGoldBoss-<version>-win-x64\` and the `.zip` next to it. The folder contains
`DofusGoldBoss.exe`, the runtime, `data\schemas`, `data\seed` (readable copies; the app uses embedded
copies), `README.md` and `LICENSE-NOTICES.md`. No `.pdb`, no secrets, no user data.

Version: `<Version>` in `Directory.Build.props`.

## Smoke test of a package

1. Unzip into an empty folder.
2. `DofusGoldBoss.exe --data-dir <empty temp folder>` (keeps your real data untouched) — no .NET needed.
3. `.\scripts\ui-smoke.ps1` while it runs → expect "All UI checks passed".

Done for 0.1.0 on 17/10/2026: 14/14 checks, warm start 1.6–2.2 s.

## Installer

Not provided yet (a plain folder is enough and leaves no trace outside `%LOCALAPPDATA%\DofusGoldBoss`).
If one is added, use an established tool (WiX v5 or Inno Setup), per-user install without elevation, and
verify install / launch / upgrade / uninstall.

## Upgrade

Replace the application folder. On start the database is migrated (backward-compatible migrations only) and
changed bundled seed packages are re-applied without touching user-edited records.
