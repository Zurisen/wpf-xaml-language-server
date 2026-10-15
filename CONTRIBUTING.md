# Contributing to wpf-xaml-ls

Thanks for helping. Bug reports matter as much as code, and false positives most of all.

## Reporting a wrong diagnostic

The server's main promise is that what it reports is real. If it flags valid XAML, please open a
[bug report](https://github.com/Zurisen/wpf-xaml-language-server/issues/new?template=bug_report.yml) with:

- the diagnostic code and message (e.g. `XLS1015 'Name' is not a property of 'Customer'`);
- a minimal XAML snippet that reproduces it, plus the C# types it uses if they matter (a view model, a custom control);
- why it's valid: it works at run time, the WPF build accepts it, or a link to the WPF behavior;
- if project loading seems involved, a log from `wpf-xaml-ls --log-level debug --log-file wpf-xaml-ls.log`, or from
  `wpf-xaml-ls check <your.sln> --log-level debug`.

Please don't paste proprietary code. A reduced example is better anyway.

## Building and testing

You need the .NET 10 SDK. The tests also use the WPF targeting pack, which is installed with the .NET SDK on Windows;
tests that need it are skipped elsewhere.

```bash
dotnet build WpfXamlLs.slnx
```

```bash
dotnet test --solution WpfXamlLs.slnx
```

The end-to-end test (`ProjectLoadingTests`) builds `samples/` and drives the server over LSP with real MSBuild
loading. It runs on Windows only and takes about a minute.

To try a local build in an editor, point the editor's command at
`src/WpfXaml.LanguageServer/bin/Debug/net10.0/wpf-xaml-ls` (`.exe` on Windows), or pack and install it as a tool
(see the README).

## How the code is organized

| Project | What lives there |
|---|---|
| `src/WpfXaml.Language` | Error-tolerant XAML parser and markup-extension parser. No dependencies. |
| `src/WpfXaml.Analysis` | Type system over Roslyn (`TypeSystem/`), semantic model and diagnostics (`Semantics/`), resources (`Resources/`), bindings (`Bindings/`), completion, hover and go to definition (`Features/`). Editor-agnostic. |
| `src/WpfXaml.LanguageServer` | LSP host (`XamlLanguageServer`), project loading (`Workspace/`), the `check` command. |

[docs/architecture.md](docs/architecture.md) explains the design decisions. Read its "Bindings" and "Resources"
sections before changing what gets reported.

## Rules for diagnostics

- **Report only what is certain.** If the outcome depends on something the server can't see statically (code-behind
  setting a `DataContext`, a NuGet package's resources, a subclass that may add a property), stay silent.
  Completion and hover can still use best guesses.
- **Every new check needs tests for both sides:** XAML that must be reported, and valid XAML that looks similar but
  must not be.
- **Calibrate on real code.** Before proposing a new kind of warning, run `wpf-xaml-ls check` on one or more real
  WPF solutions (yours, or public ones such as MahApps.Metro, ModernWpf, WPF UI or HandyControl). Look at what it
  reports: every false positive points at a WPF behavior the check doesn't model yet. Mention the numbers in the
  pull request.
- Messages say what is wrong and, when possible, suggest the fix ("Did you mean 'Content'?").
- New diagnostic codes go in `DiagnosticCodes` (`src/WpfXaml.Language/XamlDiagnostic.cs`) and in the README table.

## Pull requests

- Keep them focused: one feature or fix per pull request.
- Match the existing style (`.editorconfig`; warnings are errors).
- CI (Windows and Ubuntu) must pass.
- Add an entry to [CHANGELOG.md](CHANGELOG.md) under "Unreleased".

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
