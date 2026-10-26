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
vault. Pass `--unlocked` to open the main window directly for review.

1. **Unlock screen** — plain form, file label and mock diagnostics.
2. **Main window** — permanent WinUI NavigationView pane on the left, entry list and
   detail in one two-pane workspace, integrated title bar and search.
3. **Sync (left footer)** — mock conflict dialog; conflict-copy-first rule.
4. **Settings (left footer)** — theme switch and security toggles (mock only).
5. **New entry / Edit / Move / Delete** — modal flow and row refresh.
6. **Lock (left footer)** — returns to the unlock screen.

## Current layout (v5)

- Integrated dark Mica title bar: app icon + name, centered search, native window buttons.
- Permanent WinUI `NavigationView` pane, always open (no hamburger toggle). Standard
  navigation items, selection treatment, InfoBadge counts, and footer items for
  sync / lock / settings.
- The entry list and detail form one two-pane workspace: only the **top-left corner**
  is rounded, while its right and bottom edges sit flush on the window frame. It
  animates in from the bottom-right after unlock.
- Restrained 1Password-style detail: flat field groups, one accent, no decorative
  cards. Functional actions stay visible: Move/Edit/Delete, copy/reveal/generate
  password, Edit TOTP, custom fields, notes.
- Colour is back: dark Mica surface, accent-blue app/group icons, coloured entry
  avatars, and accent primary actions.

## Notes

- All UI copy is English (M3-10 direction).
- Window is sized for the current 1707×1067 desktop at 150% scaling; resize freely.
- This is a throwaway branch (`test/ui-mock-en`) and is not intended for `main`.
- Known visual review questions: information density, three-column widths, accent
  usage, conflict dialog wording, and whether unlock should be a separate window.
