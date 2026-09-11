# TuckPane

[English](README.md) | [简体中文](README.zh-CN.md)

TuckPane is a desktop file organizer for Windows 10 22H2 and Windows 11 x64. It keeps real files and folders inside compact desktop panes that expand when needed and stay out of the way the rest of the time.

## In-app updates (4.0.0)

Settings now includes **Updates**. TuckPane checks GitHub stable releases at most once every 24 hours after startup and shows a sidebar badge without pop-ups. Manual checks are also available. **Update and restart** downloads and verifies the package, saves open documents and settings, backs up the configuration, updates the current program directory and restarts. Both installed and portable editions are supported.

Organizer files, layouts and storage associations remain in place. Configuration backups are stored under `updates/config-backups` in the existing settings root, including legacy GlassFolder roots. The portable edition retains its existing data locations. Program and user-data directories must be separate. Failed updates leave details in the Updates page; an installer request to restart Windows is reported separately.

Version 4.0.0 brings together in-app updates and the accumulated Dock, pane layout, appearance, note/to-do, and storage improvements described below. Focused automated update checks have passed; actual GUI installation and upgrade behavior has not yet been confirmed by user testing. See the [manual update checklist](docs/UPDATE_HANDTEST.zh-CN.md). The focused check command is `dotnet run --project tests/TuckPane.UpdateChecks -c Release -- --updates`.

## Demo

<p align="center">
  <img src="docs/images/demo-expand-collapse.gif" alt="A TuckPane organizer expanding and collapsing" width="420">
  <img src="docs/images/demo-file-reorder.gif" alt="Files being reordered inside a TuckPane organizer" width="420">
  <br><sub>Left: expand and collapse a pane. Right: drag files to rearrange them inside a pane.</sub>
</p>

## Screenshots

<p align="center">
  <img src="docs/images/organizer-expanded.png" alt="An expanded TuckPane organizer showing files and folders" width="720">
  <br><sub>Expand an organizer only when you need its contents.</sub>
</p>

<p align="center">
  <img src="docs/images/context-menu.png" alt="TuckPane right-click menu with quick organizer actions" width="344">
  <br><sub>Right-click for settings, duplication, mode switching, renaming, storage access, and safe deletion.</sub>
</p>

<p align="center">
  <img src="docs/images/manage-settings.png" alt="TuckPane organizer management settings" width="900">
  <br><sub>Adjust each organizer's grid, mode, theme, entry size, canvas size, and content scale.</sub>
</p>

<p align="center">
  <img src="docs/images/themes.png" alt="TuckPane light acrylic, dark acrylic, solid light, and solid dark themes" width="800">
  <br><sub>Choose between light acrylic, dark acrylic, solid light, and solid dark themes.</sub>
</p>

### Quick actions

Drag files and folders directly into a pane, reveal a Station from a monitor edge, create notes and to-do lists beside real files, hold `Ctrl` and scroll to resize contents, and keep TuckPane running quietly from the system tray.

## Features

- Create ordinary organizer panes in floating or desktop-positioned mode without a product count limit, plus edge-docked Station panes that reveal without taking keyboard focus. Existing Station limits and mode-conversion restrictions remain.
- Nest an ordinary pane one level inside any root pane without moving either pane's storage directory. Icon view shows the contained pane as a live mini-window, while compact-list view uses the TuckPane icon and pane name. Stations remain root-only, and a contained pane or a pane that already contains others cannot be nested again.
- Create rich `.tucknote` files directly in the top level of an organizer directory, with pasted images, seven color themes, optional ruled lines, inline renaming, and saved window placement.
- Create portable `.tucktodo` to-do lists with editing, drag reordering, completion undo, themes, font scaling, inline renaming, and saved window placement.
- Drag files, folders, application shortcuts, Steam `.url` shortcuts, and portable notes between panes or standard Windows targets with negotiated Copy, Move, or Link behavior.
- Resize ordinary panes from every edge or corner: the content panel, icons, labels, row spacing, and padding scale together in both icon and compact-list modes, including always-expanded panes. The title keeps its independent size. Each mode saves its own overall content scale, defaulting to 1 for older settings.
- Click an expanded pane's visible title to rename it in place: Enter or focus loss commits, and Esc cancels. Moving a pressed title starts dragging instead; mouse dragging begins as soon as the DPI-adjusted movement threshold is crossed, without a long-press delay. Touch long-press behavior is unchanged.
- Compact-list left padding to the first icon is reduced to two thirds of its previous total, including the row's inset. Icon-to-label spacing stays unchanged, and padding still follows overall content scaling.
- Dragging a pane into another pane shares file-drop expansion protection and cleanup. Completion, leaving, cancellation, and failure release the temporary protection; the configured collapse delay and always-expanded setting determine what happens next.
- In icon mode, `Ctrl` + wheel scales icons, labels, and spacing. Compact-list mode has an independent whole-row scale, while an ordinary wheel still scrolls the list.
- Both icon and compact-list ordinary wheel input is smooth and row-based. Existing and new organizer names, titles, and item labels use white text.
- The Context menu settings category contains pane-menu visibility switches. Display can hide the collapse indicator while preserving its click area. Existing Dock rename dialogs center in the work area of the screen currently containing the Dock.
- A Station expands only on its configured monitor edge and never reveals or raises peer organizers from another monitor's bottom edge.
- Optionally expand ordinary panes after hovering, collapse them after the pointer leaves, and choose whether only one pane may stay expanded.
- Paste files, create folders, cut items through the Windows clipboard, and move deleted real files to the Recycle Bin.
- Open settings, duplicate an empty pane, switch compatible modes, rename, open its storage directory, or safely delete it. General settings can also unify bottom-name size separately for Floating and Positioned panes.
- Choose Light, Gray, Solid Light, Solid Dark, Frosted Light, or Frosted Dark themes, with English, Simplified Chinese, and Japanese interfaces.
- Run silently from the system tray. Closing the settings window hides it; only **Exit** in the tray menu terminates TuckPane.

## Download

Current version: **4.0.0**. See the [4.0.0 release](https://github.com/ch998244353/TuckPane/releases/tag/v4.0.0) for the complete release notes.

- [TuckPane-4.0.0-win-x64-setup.exe](https://github.com/ch998244353/TuckPane/releases/download/v4.0.0/TuckPane-4.0.0-win-x64-setup.exe): recommended per-user offline installer with Start menu and desktop shortcuts.
- [TuckPane-4.0.0-win-x64-portable.zip](https://github.com/ch998244353/TuckPane/releases/download/v4.0.0/TuckPane-4.0.0-win-x64-portable.zip): extract it and run `00-启动 TuckPane.exe`.
- [SHA256SUMS.txt](https://github.com/ch998244353/TuckPane/releases/download/v4.0.0/SHA256SUMS.txt): SHA-256 checksums for both downloads.

Version 3.0.2 has no in-app updater: install 4.0.0 over the existing installation, or extract the portable package into a separate program directory. Existing data roots are retained. Once on 4.0.0, use **Settings → Updates** for future releases.

Both packages include .NET and the Windows App SDK. The offline installer also carries the Microsoft Edge WebView2 Runtime and installs it only when missing; the portable package uses an existing WebView2 Runtime and leaves the folder and desktop context menus unregistered until enabled. Enabling these menus writes to the current user's registry. The TuckPane installer is currently unsigned, so Windows SmartScreen may show an “Unknown publisher” warning; verify the download with `SHA256SUMS.txt` when needed.

System requirement: Windows 10 22H2 x64, build 19045 or later. On Windows 10, corners, borders, and transparency may follow simpler platform fallbacks; content and interaction remain supported.

## Storage and data

### Create an organizer from the desktop background

Right-click an empty area of the desktop and choose **New organizer** (Chinese: **新建收纳窗**); on Windows 11 this classic menu may appear under **Show more options**. **Settings → System → Desktop context menu** controls this entry independently from the folder menu. New installer builds enable it by default, including upgrades without a saved desktop-menu preference; an explicit off choice is preserved. Portable copies require manual enabling. Automatic startup, repair, and uninstall respect the owning program copy.

The `--create-organizer` command starts or redirects to the single app instance and immediately creates a collapsed floating pane with a default name, default storage location, and 3×3 icon layout on the selected display. It uses existing placement rules and failure feedback without opening a creation settings page.

### Create an organizer from a folder's context menu

This feature is included in 4.0.0.

- **Installer**: enables the menu by default, normally without administrator rights. Disable it under **Settings → System → Folder context menu**; upgrades preserve an explicit off choice.
- **Portable**: extract the complete archive, launch the app, and enable the menu in the same settings page. Downloading or extracting alone does not register it.
- **Windows 10**: right-click one real folder and choose **Create organizer with TuckPane**. **Windows 11**: usually choose **Show more options** first. Real folders on the desktop are supported; backgrounds, multiple selections, and folder shortcuts are not.

The command starts TuckPane if needed or redirects to its running instance. It links the original directory in place and uses the folder's name, floating mode, a collapsed entry, and a 3×3 icon grid. Uniform floating entry size is respected when enabled. No setup dialog, file move, or copy is involved. Repeating the command reports the existing organizer; overlapping, protected or network directories remain restricted. Ordinary panes have no creation count cap, and loading settings retains panes beyond the former limit of 12.

Parent and child folders cannot have separate organizers. Creation failures open a standalone error dialog without opening the console. Folder conflicts include the selected path, existing organizer name, and storage path; choose **Got it** to close the dialog.

Organizer names are independent of folder names. The folder name initializes a new organizer's display name; renaming through the expanded title, organizer menu, or management page only changes that display name and persists across restarts. It does not move directories, rename files, or change note and to-do paths.

After moving a portable copy, launch it from its new location and choose **Repair association**. Explicitly enabling or repairing switches the menu to this copy; ordinary startup does not take ownership from another copy. Disable the menu before deleting a portable folder, because deleting files cannot unregister it. Uninstalling removes only the folder menu still owned by that installation, preserving a later portable takeover. Registration errors appear in settings and can be retried.

The focused `TuckPane.LogicChecks` entries are `--organizer-create-feedback` for failure feedback and `--sep08-fixes organizer-rename` for independent display names. Neither starts windows nor modifies the real registry. Actual Explorer menus, dialogs, and name display are left to user testing; see [PLAN.md](PLAN.md) for the checklist and verification status.

### Save locations

New installations store organizer data under `%USERPROFILE%\TuckPane` and settings/cache under `%LOCALAPPDATA%\TuckPane`. If only legacy GlassFolder data exists, TuckPane continues to use it in place without copying or moving organizer files.

Each new pane uses one directory such as `%USERPROFILE%\TuckPane\Windows\Name-ID`; files are stored directly in that directory. You may instead select an existing dedicated directory as the pane's final storage location, and its current top-level contents appear immediately. TuckPane rejects broad or overlapping locations that could risk unrelated data.

Notes created inside a pane are visible top-level `.tucknote` files. Legacy internal notes migrate one at a time on startup; a failed note is retained and retried later without blocking the others.

Changing the global note theme also updates valid, unopened `.tucknote` files at the top level of registered organizer directories. Nested and unregistered directories are not scanned.

Deleting a pane from its context menu or the management page asks you to **move the whole save folder to Desktop** or **keep its files in their original location**. You can cancel, and the choice is not remembered or controlled by a setting. When moving to Desktop, the pane is removed only after its whole directory moves successfully; a failure or cancellation retains the pane and source directory. Any panes directly contained by the deleted pane return to the Desktop without moving their own storage directories; deletion stops before moving files if there are not enough grid positions for contained positioned panes. Open notes stay open and save to their rebound Desktop paths. Keeping files in place removes only the pane and preserves its directory and note paths. Uninstalling TuckPane does not delete organizer files or settings.

## Build

Install .NET SDK 10.0.400 and Inno Setup 6, then run:

```powershell
.\scripts\build-release.ps1
```

Run the focused automated update checks with:

```powershell
dotnet run --project .\tests\TuckPane.UpdateChecks -c Release -- --updates
```

These checks cover update decisions, downloads, configuration backups, and portable replacement in isolation. They do not run the GUI or an installer. Actual installation and upgrade behavior remains unconfirmed by user testing; see [PLAN.md](PLAN.md) for verification and release status.

## License

TuckPane is licensed under the [MIT License](LICENSE). Third-party runtime notices are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
