## ✨ What's New

### Viewer & Editor

- **Ctrl+W** closes the viewer (F3) and the built-in editor (F4). Plain **W** in the viewer still toggles word wrap.
- Markdown: rendered view in the viewer and a live preview in the editor (**Ctrl+Shift+V**).
- Editor and viewer follow theme changes without reopening.
- Fixed: choosing "Save" when closing the editor with unsaved changes raised "Cannot set Visibility… while a window is closing".
- Fixed: after saving and closing the editor, the main window dropped behind other applications (e.g. VS Code).
- Fixed: reopening an SSH file after a failed upload could overwrite local edits. Reopening now stops if pending changes cannot be uploaded, preserving the local copy and keeping change monitoring active.

### SSH & Bitwarden

- Added SSH agent authentication, including **Bitwarden**, alongside authentication with a private key file.
- Agent authentication uses keys held by the agent without exporting private keys to disk.
- Fixed: **Ctrl+D** inside a remote program such as `cat` or Python could unexpectedly disconnect the entire SSH session. Disconnection now follows the actual shell exit.

### Delete

- No more upfront counting pass: deleting large trees (e.g. node_modules) starts immediately.
- Progress bar in the status bar while deleting.
- Rows leave the list at a steady pace instead of in jumps; folder watching is paused during the delete.
- Items that need administrator rights ask for permission and continue elevated instead of failing.
- Fixed: "Specified method is not supported" error during delete.
- Fixed: deleting a symbolic link over SSH could follow the link and delete its target's contents. Deletion now removes the link itself and preserves its target.

### Copy, Move & Archives

- Items are unmarked one by one as each finishes copying or moving.
- Copying to protected folders asks for administrator permission and continues elevated.
- Copy files and folders directly from archives to SSH servers, including nested and empty folders.
- Archive-to-SSH transfers stream data without temporary extracted files and support overwrite prompts, skipping, cancellation and progress reporting.
- Drag and drop files out of archives and SSH folders to other applications.
- Enter on a file inside an archive opens it.
- After moving a file with F6 (or when the file under the cursor disappears), the cursor lands on the row above instead of jumping to the top of the list.
- Copying symbolic links now respects overwrite and skip choices. Existing ordinary folders are protected from replacement, and failed link creation leaves destination data intact.
- Closing an operation window now requests cancellation and waits for the operation and resource cleanup to finish, including archive finalization.
- TAR.GZ extraction now streams file contents instead of buffering entire entries in memory, reducing memory use for large archives.
- Improved cancellation during archive extraction, archive scanning and SSH folder-size calculations.
- Internal archive paths now use case-sensitive comparisons, keeping entries such as `Config.json` and `config.json` separate during selection and transfer to SSH.
- Extraction on Windows detects conflicting path spellings instead of silently overwriting entries whose names differ only by case.

### Search

- SSH filename searches now respect the case-sensitivity setting.
- Fixed case handling in filename masks used by remote content searches.
- Fixed: regular expressions such as `alpha|beta` did not work correctly when remote content search used the `grep` fallback.
- Improved cancellation during the remote file-size lookup used by content search.

### Panels

- **Shift+↑ / Shift+↓** toggles the current file's yellow mark and moves the cursor, following the existing Insert-key marking behavior.
- Fixed: Shift+arrow selection produced gray rows, but copying included only the file under the cursor. Keyboard marking now uses the same selection as copy, move and delete.
- Improved cursor movement during rapid key repeats. Holding an arrow at the start or end of the list no longer repeatedly toggles the boundary file's mark.
- The Windows context menu clears marks after a command runs.
- Selection restoration now prefers an exact filename match, keeping case-distinct names separate in SSH folders and archives.

### Appearance

- New copper-and-amber application icon with a transparent background and multiple sizes for Windows.

### Build & Maintenance

- Added automated archive regression checks and GitHub Actions validation on Windows and Linux, including a Windows Release build with warnings treated as errors.
- Source builds can proceed without the optional `Assets/appicon.ico`; when present, the icon is embedded in the executable.
- Updated `.gitignore` to allow `Assets/appicon.ico` to be committed.
- Added SDK configuration targeting the .NET 10 SDK family.
- Removed the unused folder-size cache and obsolete methods.
- Fixed a nullable warning in SSH host-key verification.
- The About window reads the application version from build metadata instead of displaying a fixed version number.
