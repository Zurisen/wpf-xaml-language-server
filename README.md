# wpf-xaml-ls

[![CI](https://github.com/Zurisen/wpf-xaml-language-server/actions/workflows/ci.yml/badge.svg)](https://github.com/Zurisen/wpf-xaml-language-server/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

An open-source, editor-agnostic language server (LSP) for **WPF XAML**. It understands WPF types, properties,
attached properties, events, markup extensions, resources, bindings and your own project's code. Use it in Neovim,
VS Code, Zed, Helix or Emacs, or from CI and AI agents.

> **Status: early alpha (0.1).** It works on real solutions (see below), but expect rough edges, and
> report them. See [docs/problem-statement.md](docs/problem-statement.md) for the motivation and
> [docs/architecture.md](docs/architecture.md) for the design and roadmap.

## Features

- **Diagnostics:**
  - unknown types, properties, attached properties and events, with "Did you mean …?" suggestions;
  - invalid enum and Boolean values;
  - read-only properties;
  - duplicate `x:Name` (templates get their own namescope);
  - event handlers missing from the code-behind;
  - unknown markup extensions and their arguments;
  - undeclared prefixes;
  - unreferenced `clr-namespace` assemblies;
  - malformed XML and malformed markup extensions.
- **Completion:**
  - element names from every `xmlns` in scope, and property elements (`<Grid.RowDefinitions>`);
  - attributes (properties, events, attached properties, `x:` directives, `d:` design-time attributes);
  - enum, Boolean and named values (`Colors`, `Brushes`, `FontWeights`, `Cursors`, …);
  - `TargetType` and other types;
  - `Setter.Property` from the style's `TargetType`;
  - event handlers from the code-behind;
  - markup extensions and their arguments (`{Binding Mode=…}`, `{RelativeSource …}`, `{x:Type …}`);
  - resource keys;
  - binding paths;
  - closing tags;
  - `xmlns` URIs.
- **Resources:**
  - `StaticResource` and `DynamicResource` keys resolve through the element tree, `ResourceDictionary` and
    `MergedDictionaries`, then `App.xaml`, including `Source` files in other projects (relative, `/Folder/…`,
    `pack://application:,,,/Assembly;component/…`).
  - Completion lists every key in scope, and go to definition and hover work across files.
  - Warnings for missing `StaticResource` keys and for missing dictionary `Source` files. A warning is only given
    when every dictionary in scope could be read.
- **Bindings:**
  - Binding paths resolve against the data context the XAML declares: `d:DataContext`, an implicit
    `DataTemplate DataType`, an `ItemTemplate`'s `ItemsSource` items, `DataContext="{Binding …}"` or
    `<X.DataContext>`.
  - `ElementName`, `RelativeSource` (`Self`, `AncestorType`, `TemplatedParent`) and `Source` are understood.
  - Completion works segment by segment (`Customer.Address.|`), and hover and go to definition work on each segment.
  - Warnings for path segments that can't exist. Nothing is reported when the data context is unknown, or when a
    subclass could have the property.
  - `{TemplateBinding}` is checked against the template's `TargetType`.
- **Quick fixes (code actions):**
  - every "Did you mean …?" diagnostic fixes with one action: types (start and end tag), properties, attached
    properties, `x:` directives, markup extensions and their arguments, enum values, resource keys, binding paths
    and `TemplateBinding` properties;
  - **generate event handler:** adds a method with the event's signature to the code-behind;
  - **add `xmlns`:** for an undeclared prefix, or a type found in a project or library namespace that isn't
    imported (reuses an existing prefix when there is one).
- **Hover:** signatures and XML documentation for types, properties, attached properties, events, enum values,
  directives, resource keys and binding paths.
- **Go to definition:** from XAML into C# (project types, properties, event handlers, `x:Class`, `x:Static`
  members, binding paths) and from `{StaticResource}` to its `x:Key`, in whichever file defines it.
- **Project-aware:** loads your `.sln`, `.slnx` or `.csproj` with MSBuild, so it sees project types even before
  they are built. It needs no WPF runtime: it reads reference assemblies.
  - It knows exactly which XAML files each project builds. Files excluded with `<Page Remove>` aren't reported.
  - A project that isn't in the solution is loaded when you open one of its XAML files.
  - Up-to-date project references are read from their built DLLs, for speed.
- **`check` command:** prints diagnostics for a whole solution in MSBuild format, for CI or agents.
- **MCP server for AI agents:** checks, fixes, type descriptions, resources and binding contexts as tools (see
  [AI agents](#ai-agents-mcp)).

**Diagnostics aim for zero false positives.** Anything the server can't know statically is not reported: a data
context set in code, a resource dictionary from a NuGet package, a property a subclass might add. The rules are
calibrated against a 117-project commercial WPF solution with 719 XAML files. On that solution:

- Projects load in about 25 s.
- Opening a file in the largest project gives diagnostics in about 2.5 s (more complete ones follow a few seconds
  later).
- Completion takes 10–15 ms after warm-up.

### Known limitations

- **Requirements:** a .NET 10 SDK, and projects restored before the server can load them (`dotnet restore`, or a
  build).
- **Windows-first:** analysis runs anywhere, but loading `net*-windows` projects is tested on Windows only.
- **Not checked yet:**
  - non-string resource keys (`{x:Type …}` implicit styles, `ComponentResourceKey`) and theme dictionaries;
  - bindings written as elements (`<Binding/>` inside `MultiBinding`), converters and value types;
  - .NET Framework (non-SDK) projects, which fall back to framework types.
- **Missing features:** rename, find references, formatting, document symbols and folding. See the
  [roadmap](docs/architecture.md#known-gaps--next-steps).

## Install

Requires the .NET 10 SDK, or the .NET 10 runtime plus the SDK your projects use. Install the tool from NuGet
(releases are prereleases while the project is in alpha):

```bash
dotnet tool install -g WpfXaml.LanguageServer --prerelease
```

This puts `wpf-xaml-ls` on your PATH. To update later, run `dotnet tool update -g WpfXaml.LanguageServer --prerelease`.

To use an unreleased version, build and install from source:

```bash
dotnet pack src/WpfXaml.LanguageServer -c Release -o artifacts
```

```bash
dotnet tool install -g --add-source artifacts WpfXaml.LanguageServer --prerelease
```

## Editors

### Neovim (0.11+)

Copy [editors/nvim/wpf-xaml-ls.lua](editors/nvim/wpf-xaml-ls.lua) to your config's `after/plugin/` folder:
`~/.config/nvim/after/plugin/` on Linux and macOS, `%LOCALAPPDATA%\nvim\after\plugin\` on Windows. It needs no
plugins and works next to lemminx (the XML language server):

- `.xaml` files keep (or get) the `xml` filetype, so XML highlighting, tree-sitter and lemminx keep working.
- The server starts only for `.xaml` files, rooted at the nearest solution, project or git root.
- lemminx's duplicate XML-syntax diagnostics are hidden in XAML buffers. lemminx still provides formatting,
  folding and the outline.

### VS Code

The **WPF XAML Language Server** extension ([editors/vscode](editors/vscode)) starts the server for `.xaml` files and
exposes its settings. It needs the `wpf-xaml-ls` tool installed (see [Install](#install)).

Install it from the [VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=Zurisen.wpf-xaml-ls)
(search for "WPF XAML Language Server" in the Extensions view), or download the `.vsix` from a `vscode-v*`
[release](https://github.com/Zurisen/wpf-xaml-language-server/releases) and use **Extensions → … → Install from VSIX…**. To build it yourself, run `npm ci` and then
`npm run package` in `editors/vscode`.

### AI agents (MCP)

`wpf-xaml-ls mcp` runs the same analysis as an [MCP](https://modelcontextprotocol.io) server, so coding agents can
check and understand XAML while they edit it. It loads the solution found under its working directory (or
`--solution <path>`), and offers five read-only tools:

| Tool | What it answers |
|---|---|
| `xaml_check` | Problems in a file (or its unsaved content), a folder or a solution, each with its quick fixes. |
| `xaml_fix` | The file's content with the obvious fixes applied (it doesn't write the file). |
| `xaml_describe_type` | How a type is used in XAML: xmlns, content property, settable properties, events, attached properties. |
| `xaml_resources` | The resource keys available at a position. |
| `xaml_binding_context` | The data context at a position, and the properties a binding can use there. |

With Claude Code, run this in your solution's folder:

```bash
claude mcp add wpf-xaml -- wpf-xaml-ls mcp
```

Other clients use the usual MCP configuration, for example in `.mcp.json`:

```json
{ "mcpServers": { "wpf-xaml": { "command": "wpf-xaml-ls", "args": ["mcp"] } } }
```

### Initialization options

| Option | Default | Meaning |
|---|---|---|
| `solution` | discovered | Path to the `.sln`, `.slnx` or `.csproj` to load (relative to the root). |
| `loadProjects` | `true` | `false` uses WPF framework types only (no MSBuild). |
| `frameworkVersion` | highest installed | Major version of the WPF targeting pack used before a project loads, or without one. |

## Command line

```bash
wpf-xaml-ls check path/to/App.sln
```

This prints `file(line,col): error XLS1003: …` for every XAML file and exits with code 1 if there are errors.

```bash
wpf-xaml-ls --help
```

## Diagnostic codes

| Code | Meaning |
|---|---|
| XLS0001 | XML syntax error |
| XLS0002 | Markup extension syntax error |
| XLS1001 | Undeclared namespace prefix |
| XLS1002 | Unknown type |
| XLS1003 | Unknown property or event |
| XLS1004 | Invalid value (enum, Boolean) |
| XLS1005 | Duplicate name in namescope |
| XLS1006 | Unknown markup extension |
| XLS1007 | Read-only property |
| XLS1008 | Event handler not found in code-behind |
| XLS1010 | Unresolved namespace or assembly |
| XLS1011 | Unknown or misplaced `x:` directive |
| XLS1012 | Property element owner doesn't match its parent (warning) |
| XLS1013 | Resource dictionary `Source` file not found (warning) |
| XLS1014 | `StaticResource` key not found (warning) |
| XLS1015 | Binding path segment not found on the data context type (warning) |
| XLS1016 | `TemplateBinding` property not found on the template's `TargetType` (warning) |
| XLS9001 | File is excluded from its project's build; only syntax is checked (information) |

## Contributing

Bug reports, especially false positives, and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for
how to build, test, and report a diagnostic that's wrong. Report security issues privately as described in
[SECURITY.md](SECURITY.md).

| Project | Purpose |
|---|---|
| `src/WpfXaml.Language` | Error-tolerant XAML and markup-extension parser |
| `src/WpfXaml.Analysis` | Roslyn-backed WPF type system and language features |
| `src/WpfXaml.LanguageServer` | LSP host, project loading, `check` command (`wpf-xaml-ls` tool) |

## License

[MIT](LICENSE). This is an independent project, not affiliated with or endorsed by Microsoft. WPF, Windows and
Visual Studio are trademarks of Microsoft.
