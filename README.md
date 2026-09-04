# TuckPane

[English](README.md) | [简体中文](README.zh-CN.md)

TuckPane is a desktop file organizer for Windows 10 22H2 and Windows 11 x64. It keeps real files and folders inside compact desktop panes that expand when needed and stay out of the way the rest of the time.

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

- Create up to 12 ordinary organizer panes in floating or desktop-positioned mode, plus edge-docked Station panes that reveal without taking keyboard focus. Existing Station panes cannot be converted to or from the other modes.
- Nest an ordinary pane one level inside any root pane without moving either pane's storage directory. Icon view shows the contained pane as a live mini-window, while compact-list view uses the TuckPane icon and pane name. Stations remain root-only, and a contained pane or a pane that already contains others cannot be nested again.
- Create rich `.tucknote` files directly in the top level of an organizer directory, with pasted images, seven color themes, optional ruled lines, inline renaming, and saved window placement.
- Create portable `.tucktodo` to-do lists with editing, drag reordering, completion undo, themes, font scaling, inline renaming, and saved window placement.
- Drag files, folders, application shortcuts, Steam `.url` shortcuts, and portable notes between panes or standard Windows targets with negotiated Copy, Move, or Link behavior.
- Resize the expanded canvas proportionally from every edge or corner. Canvas size and item layout are saved automatically.
- In icon mode, `Ctrl` + wheel scales icons, labels, and spacing. Compact-list mode has an independent whole-row scale, while an ordinary wheel still scrolls the list.
- Both icon and compact-list ordinary wheel input is smooth and row-based. Organizer labels support Automatic, White, and Black; Automatic chooses the higher-contrast color against the theme tint.
- A Station expands only on its configured monitor edge and never reveals or raises peer organizers from another monitor's bottom edge.
- Optionally expand ordinary panes after hovering, collapse them after the pointer leaves, and choose whether only one pane may stay expanded.
- Paste files, create folders, cut items through the Windows clipboard, and move deleted real files to the Recycle Bin.
- Open settings, duplicate an empty pane, switch compatible modes, rename, open its storage directory, or safely delete it. General settings can also unify bottom-name size separately for Floating and Positioned panes.
- Choose Light, Gray, Solid Light, Solid Dark, Frosted Light, or Frosted Dark themes, with English, Simplified Chinese, and Japanese interfaces.
- Run silently from the system tray. Closing the settings window hides it; only **Exit** in the tray menu terminates TuckPane.

## Download

Current version: **3.0.2**. See the [Latest Release](https://github.com/ch998244353/TuckPane/releases/latest) for the complete release notes.

- [TuckPane-3.0.2-win-x64-setup.exe](https://github.com/ch998244353/TuckPane/releases/download/v3.0.2/TuckPane-3.0.2-win-x64-setup.exe): recommended per-user offline installer with Start menu and desktop shortcuts.
- [TuckPane-3.0.2-win-x64-portable.zip](https://github.com/ch998244353/TuckPane/releases/download/v3.0.2/TuckPane-3.0.2-win-x64-portable.zip): extract it and run `00-启动 TuckPane.exe`.
- [SHA256SUMS.txt](https://github.com/ch998244353/TuckPane/releases/download/v3.0.2/SHA256SUMS.txt): SHA-256 checksums for both downloads.

Both packages include .NET and the Windows App SDK. The offline installer also carries the Microsoft Edge WebView2 Runtime and installs it only when missing; the portable package does not modify the system and uses an existing WebView2 Runtime. The TuckPane installer is currently unsigned, so Windows SmartScreen may show an “Unknown publisher” warning; verify the download with `SHA256SUMS.txt` when needed.

System requirement: Windows 10 22H2 x64, build 19045 or later. On Windows 10, corners, borders, and transparency may follow simpler platform fallbacks; content and interaction remain supported.

## Storage and data

New installations store organizer data under `%USERPROFILE%\TuckPane` and settings/cache under `%LOCALAPPDATA%\TuckPane`. If only legacy GlassFolder data exists, TuckPane continues to use it in place without copying or moving organizer files.

Each new pane uses one directory such as `%USERPROFILE%\TuckPane\Windows\Name-ID`; files are stored directly in that directory. You may instead select an existing dedicated directory as the pane's final storage location, and its current top-level contents appear immediately. TuckPane rejects broad or overlapping locations that could risk unrelated data.

Notes created inside a pane are visible top-level `.tucknote` files. Legacy internal notes migrate one at a time on startup; a failed note is retained and retried later without blocking the others.

Changing the global note theme also updates valid, unopened `.tucknote` files at the top level of registered organizer directories. Nested and unregistered directories are not scanned.

**Move organizer files to Desktop when deleting** is enabled by default. The pane is removed only after its whole directory moves successfully; a failure or cancellation retains the pane and source directory. Any panes directly contained by the deleted pane return to the Desktop without moving their own storage directories; deletion stops before moving files if there are not enough grid positions for contained positioned panes. Open notes stay open and save to their rebound Desktop paths. Turn the setting off to remove only the pane while leaving its directory and files in place. Uninstalling TuckPane does not delete organizer files or settings.

## Build

Install .NET SDK 10.0.400 and Inno Setup 6, then run:

```powershell
.\scripts\build-release.ps1
```

Run the focused logic regression checks with:

```powershell
dotnet run --project .\tests\TuckPane.LogicChecks\TuckPane.LogicChecks.csproj -c Release -p:Platform=x64
```

## License

TuckPane is licensed under the [MIT License](LICENSE). Third-party runtime notices are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
