# keepassity UI mock (test branch)

Static English WinUI 3 prototype for visual direction review. **No Rust core, no
KDBX access, no network.** Data is fake and lives only in memory.

## Run

```powershell
cd apps/windows/Keepassity.Mock
dotnet build -c Debug
.\bin\Debug\net8.0-windows10.0.19041.0\win-x64\Keepassity.Mock.exe
```

The app starts on the unlock screen. Any non-empty password unlocks the mock
vault. Review:

1. **Unlock screen** — branding, recent database card, error/diagnostic placement.
2. **Main window** — three columns (groups / entries / detail), search, TOTP card,
   footer sync/auto-lock status.
3. **Sync status** (top bar) — mock conflict dialog; three choices, copy-first rule.
4. **Settings** (gear) — theme switch and security toggles (mock only).
5. **New entry / Edit / Delete** — modal flow and row refresh.
6. **Lock** (top-right lock icon) — returns to unlock screen.

## Current layout (v4)

- Integrated Mica title bar: app name + centered search + native window buttons.
- Permanent left navigation: vault file, groups, and bottom-left sync/lock/settings.
- The middle entry list and right detail live in one floating two-pane workspace with
  a soft shadow; it animates in from the bottom-right after unlock.
- Restrained 1Password-style detail: flat field groups, one accent only for selection,
  no decorative cards or badges. Functional actions stay visible: Move/Edit/Delete,
  copy/reveal/generate password, Edit TOTP, custom fields, notes.
- No hamburger: the left navigation is always visible.

## Notes

- All UI copy is English (M3-10 direction).
- Window is sized for the current 1707×1067 desktop at 150% scaling; resize freely.
- This is a throwaway branch (`test/ui-mock-en`) and is not intended for `main`.
- Known visual review questions: information density, three-column widths, accent
  usage, conflict dialog wording, and whether unlock should be a separate window.
