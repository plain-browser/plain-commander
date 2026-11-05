# Contributing to Plain Commander

Thank you for your interest. Bug reports, ideas and pull requests are welcome.

## Before you start

- **Bugs:** open an issue with the version (the title bar shows it), what you did, what you expected and what happened.
  Attach the log from `%LOCALAPPDATA%\PlainCommander\logs` if there is an error (check it for personal paths first).
  `PLAINCOMMANDER_TRACE=1` gives a more detailed log.
- **Features:** open an issue before writing a lot of code. Plain Commander is intentionally a focused file manager;
  see "Design decisions and scope" in the [README](README.md) for what is deliberately left out.
- **Security issues:** do not open a public issue; see [SECURITY.md](SECURITY.md).

## Getting the code to build

See "Building from source" in the [README](README.md). In short, you need the .NET 10 SDK; the core logic and the view
models build and test on Linux, macOS and Windows, and the WinUI application needs Windows 11.

```bash
dotnet test --project tests/PlainCommander.Core.Tests
dotnet test --project tests/PlainCommander.Presentation.Tests
```

## Code layout and rules

- `Core` has no UI and no Win32 types in its public surface; `Presentation` has no WinUI types. Everything that needs the
  operating system sits behind a small interface (see `IDialogService`, `ILauncher`, `IRecycleBin`, `IClipboard`).
  Please keep it that way so the logic stays testable everywhere.
- New behaviour comes with tests: view-model tests on temporary directories (`tests/PlainCommander.Presentation.Tests`)
  and unit tests for the core. Tests that need Windows skip themselves elsewhere (`Assert.SkipUnless`).
- Long-running work belongs in an `IOperation` that reports progress, honours pause and cancel, asks conflicts through
  the prompt interface and records what it did in the undo journal when it can be reversed.
- User-visible text goes to the resource files (`src/PlainCommander.App/Strings/en-US` and `pl-PL`) or to
  `Texts.cs` / `UserMessages.cs`; keep both languages in step (English is enough for a first version of a pull request,
  say so and the Polish text will be added).
- Follow `.editorconfig`; warnings are errors in the build. Keep changes focused: one topic per pull request.

## Pull requests

1. Fork, create a branch from `main`.
2. Make the change with tests; run both test projects.
3. For UI changes, add a screenshot to the description (light and dark theme if colours are involved).
4. Describe what changed and why. Small, reviewable commits are easier to merge.

By contributing you agree that your contribution is licensed under the project's license, the
[Mozilla Public License 2.0](LICENSE).
