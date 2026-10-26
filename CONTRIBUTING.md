# Contributing to R2Cmd

Help improve R2Cmd by reporting a reproducible problem, suggesting a workflow improvement, or submitting a focused code or documentation change.

## Report a bug

Check the [existing issues](https://github.com/axmsrn/r2cmd/issues) and the [latest release](https://github.com/axmsrn/r2cmd/releases/latest) first. Then [open an issue](https://github.com/axmsrn/r2cmd/issues/new) with:

- Your R2Cmd version and Windows version.
- The steps that reproduce the problem.
- What you expected and what happened instead.
- Whether the files are local, on a network share, inside an archive, or accessed over SSH.
- The exact error message and a screenshot when useful.

## Suggest a feature

Describe the task you want to accomplish, your current workaround, and how the proposed change would help. Check the [roadmap](README.md#roadmap) and existing issues for related requests.

## Submit a change

1. Fork the [public repository](https://github.com/axmsrn/r2cmd).
2. Clone your fork and create a branch for one focused change.
3. Make the change using the existing project conventions.
4. Validate the affected behavior and open a pull request describing the problem, the change, and your checks.

```powershell
git clone https://github.com/YOUR-USERNAME/r2cmd.git
cd r2cmd
git switch -c fix/describe-your-change
```

## Build and validation

Application development requires Windows 10/11 x64 and the .NET 10 SDK. The SDK family is configured in `global.json`.

```powershell
# Build the Windows application; treat warnings as errors
dotnet build R2Cmd.csproj -c Release -warnaserror

# Run the application
dotnet run --project R2Cmd.csproj

# Run archive regression checks
dotnet run --project Tests/R2Cmd.ArchiveChecks.csproj -c Release -warnaserror
```

Archive checks can also run on Linux with the .NET 10 SDK. GitHub Actions runs these checks on Windows and Linux and builds the application on Windows.

For a self-contained executable, use the [publish command in the README](README.md#build-from-source).

Before submitting:

- Confirm the application builds without warnings when code is changed.
- Check the affected workflow, including cancellation and error handling for file operations.
- For UI changes, check the light and dark themes.
- Add or update focused regression checks when a behavior change needs coverage.
- Keep documentation and user-facing labels consistent with the implementation.

For documentation-only changes, check links, examples, and formatting; an application build is not required.

## Code conventions

- Use English for comments and interface text.
- Follow existing naming: `PascalCase` for types and methods, `camelCase` for local variables, and `_camelCase` for private fields.
- Keep blocking filesystem and SSH work off the UI thread.
- Preserve cancellation, overwrite choices, and error reporting when changing file operations.
- Keep changes focused. Reuse existing patterns and avoid unrelated refactoring or new dependencies.

Use descriptive commit messages, such as `fix: preserve unsent SSH edits` or `docs: clarify archive transfers`. Reference a related issue when applicable.

## Publishing a release

Release automation is defined in `.github/workflows/release.yml` and runs only in the public `axmsrn/r2cmd` repository.

1. Update `ver.md` with the changes included in the release.
2. Set a new tag in `.github/release-version.txt` and update `<Version>` in `R2Cmd.csproj` to the corresponding numeric version.
3. Push the prepared source to the public repository's `main` branch.

Changing the release-version file starts Windows publishing and archive checks on Windows and Linux. Once both platforms pass, the workflow uploads the ZIP and SHA-256 checksum to a draft release and publishes it as the latest release. The workflow can also be started manually from GitHub Actions with a version input. Each published build uses a new version.

## License

Contributions are distributed under the project's existing [non-commercial license](LICENSE). Read its terms before submitting a contribution.

For licensing questions, contact [axmsrn@gmail.com](mailto:axmsrn@gmail.com).
