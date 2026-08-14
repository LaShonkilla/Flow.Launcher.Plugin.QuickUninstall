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


## Auto-refresh

Quick Uninstall uses a stale-while-revalidate index. Typing `un` displays the existing cached list immediately, while the plugin refreshes installed programs, apps, and Steam games in the background. If the catalog changes, supported Flow Launcher versions re-query the current view without replacing what you are typing. Direct/history searches also trigger a background refresh when the cache is older.
