# Context Menu Manager

<img src="assets/icon.png" width="180" align="left" alt="Context Menu Manager">

Turn third-party right-click menu entries on or off in Windows 11, in both the new menu and the classic menu.

App created by [Andy Rostad](https://github.com/arostad).  Released under the [MIT License](https://github.com/arostad/ContextMenuManager/blob/main/LICENSE).

<br clear="all">

![Context Menu Manager](docs/screenshot.png)

## Download

- **[Installer](https://github.com/arostad/ContextMenuManager/releases/latest/download/ContextMenuManagerSetup.exe)** (`ContextMenuManagerSetup.exe`). Installs for your user account only, with no admin prompt. It adds a Start menu shortcut and an entry in Settings > Apps > Installed apps for uninstalling.
- **[Portable](https://github.com/arostad/ContextMenuManager/releases/latest/download/ContextMenuManager.zip)** (`ContextMenuManager.zip`). Unzip it anywhere and run `ContextMenuManager.exe`. Nothing gets installed.

Both versions keep themselves up to date. The app isn't code-signed yet, so Windows SmartScreen may warn you the first time you run it. Choose **More info**, then **Run anyway**.

Uninstalling removes the app and its shortcuts. It keeps your backups, and entries you turned off stay off, so turn them back on before you uninstall if you want them back.

## What it does

- **Classic menu by default.** One switch makes right-click open the full classic menu right away, instead of the Windows 11 menu with "Show more options" at the bottom. Switch it off to go back to the Windows 11 menu.
- **Third-party entries on or off.** Every entry added by other apps is listed with a switch, whether it lives in the Windows 11 menu, the classic menu, or both. Turn one off to hide it and turn it back on to bring it back.
- **Microsoft entries are left alone.** Entries that ship with Windows are not listed, so you can't break the built-in menu by accident.

## How it stays safe

- It only writes to your own user settings (HKEY_CURRENT_USER). It never needs admin rights and never touches system-wide settings.
- It blocks entries instead of deleting them. Turning an entry back on removes only what the app added, so the original registration is untouched.
- Before every change it saves a `.reg` backup to `%LOCALAPPDATA%\ContextMenuManager\backups`. The About window has a link that opens that folder.
- Changes show up in new Explorer windows. Restarting Explorer is offered as a button and only happens after you confirm.

## Requirements

Windows 11, x64.

## Build from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then run this from `src/ContextMenuManager`:

```
dotnet build -c Release -r win-x64
```

Visual Studio isn't needed. The Windows App SDK comes from NuGet and the app builds as a self-contained, unpackaged program.

To build the release files (the installer and the portable zip), install [Inno Setup 6](https://jrsoftware.org/isinfo.php) and run this from the repo root:

```
powershell -ExecutionPolicy Bypass -File build\package.ps1 -OutDir <output folder>
```

## License

[MIT](LICENSE)
