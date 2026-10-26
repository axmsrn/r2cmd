# R2Cmd

**Local files and Linux servers, together on Windows.**

A portable dual-pane file manager with built-in SSH/SFTP, a terminal, and a text editor. Move files, edit server configurations, and run shell commands from one application.

[![Download for Windows](https://img.shields.io/badge/Download-Windows%20x64-c47c38?style=for-the-badge)](https://github.com/axmsrn/r2cmd/releases/latest)
[![Windows 10 and 11](https://img.shields.io/badge/Windows-10%20%2F%2011-555555?style=for-the-badge)](https://github.com/axmsrn/r2cmd/releases/latest)
[![License](https://img.shields.io/badge/License-Non--commercial-59775a?style=for-the-badge)](LICENSE)

**One EXE. No installation. No separate .NET runtime required.**

[Download](https://github.com/axmsrn/r2cmd/releases/latest) · [What's new](ver.md) · [Keyboard shortcuts](#keyboard-shortcuts) · [Report a bug](https://github.com/axmsrn/r2cmd/issues/new)

<p align="center">
  <img src="screenshots/screenshot2.jpg" alt="R2Cmd with local files on the left and Midnight Commander running in an SSH terminal on the right" width="1000">
</p>

*Local files on the left, Midnight Commander running in the SSH terminal on the right.*

<details>
<summary>More screenshots: the editor and light theme</summary>

**Built-in editor with syntax highlighting**

<p align="center">
  <img src="screenshots/screenshot.jpg" alt="R2Cmd built-in text editor with C# syntax highlighting" width="1000">
</p>

**Local and SSH folders in the light theme**

<p align="center">
  <img src="screenshots/screenshot3.jpg" alt="R2Cmd light theme showing a local Windows drive alongside an SSH server folder" width="1000">
</p>

</details>

## Why R2Cmd

Built for personal servers, home labs, and everyday file management with familiar commander-style shortcuts.

| Your task | How R2Cmd helps |
| --- | --- |
| Move files between Windows and a server | Browse local and SSH/SFTP folders side by side. Use **F5** to copy and **F6** to move. |
| Switch between files and shell commands | Open local PowerShell/CMD or a remote SSH shell in the integrated terminal. |
| Upload selected files from an archive | Browse the archive and copy directly to an SSH folder, without temporary extracted files. |
| Edit a server configuration | Use the built-in editor or your preferred external editor; saved changes are uploaded back to the server. |
| Connect with keys stored in an agent | Use SSH agent authentication, including **Bitwarden**, without exporting private keys to disk. |
| Run without installing | Unpack the Windows x64 release and launch `R2Cmd.exe`. |

## Download and run

1. [Download the latest Windows x64 ZIP](https://github.com/axmsrn/r2cmd/releases/latest) from the release assets.
2. Extract it to a folder and run **`R2Cmd.exe`**.
3. Open a local folder, or select **NET** to add an SSH connection. Open the destination in the other panel and press **F5** to copy.

On the first connection to an SSH server, R2Cmd asks you to confirm its host-key fingerprint. Agent authentication requires your SSH agent to be running and available to the application.

Free for personal, educational, and other non-commercial use. Commercial use requires prior written permission; see [License](#license).

See the [release notes](https://github.com/axmsrn/r2cmd/releases) for the changes included in each published build.

## More features

- **Viewer and editor:** syntax highlighting, Markdown rendering and live preview, word wrap, and light/dark theme support.
- **Archives:** browse and extract ZIP, TAR, TAR.GZ, 7z, and RAR archives; create ZIP files. TAR.GZ extraction streams file contents to limit memory use.
- **Search:** find names or text locally and over SSH, with multiple filename masks and case-sensitivity options. Remote search runs on the server using `find`, `rg`, or `grep`.
- **Windows integration:** native context menus, local Recycle Bin support, network shares, and administrator prompts for protected file operations.
- **File operations:** progress reporting, overwrite/skip prompts, cancellation, and drag and drop, including dragging files from archives and SSH folders to other applications.
- **Panels:** live updates for local folders, quick search, favorites, background folder-size calculation, light/dark themes, and adjustable UI scale.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| **Tab** | Switch file panels |
| **F3 / F4** | View / edit a file |
| **F5 / F6** | Copy / move |
| **F7** | Create a folder |
| **F8 / Delete** | Delete; local items use the Recycle Bin where supported |
| **Shift+Delete** | Delete permanently |
| **F2 / Shift+F6** | Rename |
| **Insert / Shift+↑ / Shift+↓** | Mark files for an operation |
| **Alt+F5** | Create a ZIP archive |
| **Alt+F7** | Search by name or content |
| **Ctrl+D** | Open favorites |
| **Ctrl+R** | Refresh the active panel |
| **Ctrl+W** | Close the viewer or editor |
| **Ctrl+Shift+V** | Toggle Markdown preview in the editor |

File-operation shortcuts apply while a file panel has focus; the terminal and text editors keep their own input shortcuts.

## Settings

Settings are stored in `%APPDATA%\R2Cmd\settings.json`; trusted SSH host keys are stored in `%APPDATA%\R2Cmd\known_hosts`.

The executable is portable; settings are kept in your Windows user profile. Close R2Cmd before editing the settings file manually.

<details>
<summary>Useful settings</summary>

| Setting | Purpose |
| --- | --- |
| `IsDarkTheme` | Choose the dark or light theme |
| `CustomEditorPath` | Use your preferred external editor |
| `TerminalShellPath` | Choose a local shell; empty means automatic detection |
| `HardwareRendering` | Enable GPU rendering; software rendering is the default |
| `UiZoom` | Adjust the interface scale |

</details>

## Build from source

Requires **Windows 10/11 x64**, **.NET 10 SDK**, and Git. The SDK is needed only for building the application.

<details>
<summary>Build and publish commands</summary>

```powershell
git clone https://github.com/axmsrn/r2cmd.git
cd r2cmd

# Debug build
dotnet build R2Cmd.csproj -c Debug

# Self-contained single-file release
dotnet publish R2Cmd.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=false `
  -o publish
```

Output: `publish\R2Cmd.exe`.

Compression is disabled here for lower memory use. Set `EnableCompressionInSingleFile=true` for a smaller executable.

The build embeds `Assets/appicon.ico` when the file is present.

Run the archive regression checks with:

```powershell
dotnet run --project Tests/R2Cmd.ArchiveChecks.csproj -c Release
```

GitHub Actions builds the application on Windows and runs archive checks on Windows and Linux. See [CONTRIBUTING.md](CONTRIBUTING.md) for development and validation guidelines.

</details>

## Roadmap

- Multiple folder tabs within each file panel.
- FTP/FTPS support.

Have a use case these do not cover? [Suggest a feature](https://github.com/axmsrn/r2cmd/issues/new) and describe the task you want to accomplish.

## Feedback and support

[Report a bug](https://github.com/axmsrn/r2cmd/issues/new) with your R2Cmd version, Windows version, and steps to reproduce it. Feedback about SSH workflows, archive handling, and keyboard navigation is especially useful.

Code and documentation contributions are welcome; start with [CONTRIBUTING.md](CONTRIBUTING.md). If R2Cmd is useful to you, a GitHub star or sharing it with another user helps people discover it. You can also [support development on Boosty](https://boosty.to/axmsrn).

## License

R2Cmd uses a [non-commercial license](LICENSE). Personal, educational, and other non-commercial use, modification, and redistribution are permitted under its terms.

Commercial use, including internal use within a for-profit organization, requires prior written permission. Contact [axmsrn@gmail.com](mailto:axmsrn@gmail.com) for permission or licensing questions.
