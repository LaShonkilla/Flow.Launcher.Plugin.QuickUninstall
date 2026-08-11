# Quick Uninstall

A minimal Flow Launcher plugin for uninstalling classic Windows programs, Microsoft Store/MSIX apps, and Steam games.

## Usage

- `un` — list all apps, sorted A → Z
- `un -` — list all apps, sorted Z → A
- `un date` — newest date → oldest date
- `un -date` — oldest date → newest date
- `un size` — largest → smallest
- `un -size` — smallest → largest
- Add search text after a sort command, e.g. `un size chrome` or `un date steam`.

Press Enter or Right Arrow on an app to open the same in-Flow confirmation menu. No is the first/default option and returns to the same result list; Yes starts the native uninstaller.

## Install

Run `build-and-install.ps1`, then restart Flow Launcher.
