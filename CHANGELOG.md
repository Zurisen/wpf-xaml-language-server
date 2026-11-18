# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- **VS Code extension** (`editors/vscode`, versioned and released separately with `vscode-v*` tags). It starts the
  server for `.xaml` files, exposes its settings, and is tested end to end in VS Code in CI.

## [0.1.0-alpha] - first release

### Added

- **Parsing:** an error-tolerant XAML parser and markup-extension parser with exact source spans.
- **Type system:** WPF types over Roslyn compilations: `XmlnsDefinition` and `clr-namespace` namespaces, attached
  properties and events, routed events, content properties, markup extensions. No WPF runtime is needed.
- **Diagnostics:**
  - unknown types, members, directives, markup extensions and prefixes, with suggestions;
  - invalid enum and Boolean values;
  - read-only properties;
  - duplicate names;
  - missing event handlers;
  - unresolved assemblies.
- **Resources:**
  - lookup through element resources, `ResourceDictionary`, `MergedDictionaries` and `Source` files (relative,
    rooted and pack URIs, across projects), and `App.xaml`, including the applications that reference a library;
  - missing-key (`XLS1014`) and missing-dictionary (`XLS1013`) warnings.
- **Bindings:**
  - data context inference from `d:DataContext`, `DataTemplate DataType`, `ItemsSource` items and `DataContext`
    declarations;
  - `ElementName`, `RelativeSource` and `Source`;
  - binding path (`XLS1015`) and `TemplateBinding` (`XLS1016`) warnings, reported only when certain.
- **Editor features:**
  - completion for elements, attributes, values, markup extensions, resource keys, binding paths and closing tags;
  - hover with XML documentation;
  - go to definition into C#, other XAML files and binding paths;
  - quick fixes: one-action fixes for every "Did you mean …?" diagnostic, generating a missing event handler in the
    code-behind, and adding the `xmlns` for an undeclared prefix or an unimported type.
- **Project system:**
  - MSBuildWorkspace loading;
  - exact XAML ownership from `Page` items, with excluded files getting syntax checks only (`XLS9001`);
  - on-demand loading of projects outside the solution;
  - built DLLs for up-to-date project references;
  - a generator-free first compilation for fast first diagnostics.
- **Tools and editors:**
  - the `wpf-xaml-ls check` command for CI and agents;
  - an MCP server (`wpf-xaml-ls mcp`) for AI agents, with five read-only tools: `xaml_check`, `xaml_fix`,
    `xaml_describe_type`, `xaml_resources` and `xaml_binding_context`;
  - Neovim configuration (`editors/nvim`), tested next to lemminx;
  - a release workflow publishing the `wpf-xaml-ls` tool to nuget.org.
