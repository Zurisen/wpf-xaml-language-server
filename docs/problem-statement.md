# WPF XAML Language Server — Problem Statement

*Written on 2026-10-07 to kick off the project, and kept as its original motivation. For what has been built
and how the open questions in section 9 were answered, see the [README](../README.md) and
[architecture.md](architecture.md).*

## 1. Summary

WPF is still one of the most widely used UI frameworks for Windows desktop
applications, and its UI is written in XAML. Yet the only tools that
*understand* WPF XAML — that know which elements, properties, events, resources
and bindings are valid — are closed-source and tied to a single IDE (Visual
Studio, JetBrains Rider).

There is **no maintained, open-source, editor-agnostic language server (LSP)
for WPF XAML.** Developers using Neovim, VS Code, Zed, Helix, Emacs, or AI
coding agents (Claude Code, Copilot agents, etc.) get at best generic XML
support: syntax highlighting and well-formedness checks. A typo in a property
name, a binding path, a resource key or an event-handler name is only found at
build time or, worse, at runtime.

This project would build that language server.

## 2. Motivation

- **Editor freedom.** Teams increasingly mix editors. A C# developer can get
  excellent C# support anywhere through Roslyn's language server, but the
  moment they open a `.xaml` file they are back to plain XML.
- **AI coding agents.** Agents edit XAML files on disk with no feedback loop:
  they can't ask "does `Button` have a `Contnet` property?" or "is
  `{StaticResource PrimaryBrush}` defined?". A language server (or an MCP
  wrapper around one) gives them that feedback.
- **Real-world scale.** The motivating codebase (a commercial WPF application)
  has a 117-project solution, ~720 XAML files, .NET 8 (`net8.0-windows`),
  SDK-style projects, xUnit tests. Today, outside Visual Studio, none of those
  XAML files get type-aware checking.
- **Cross-platform tooling.** WPF only *runs* on Windows, but analysing WPF
  XAML only needs metadata (reference assemblies), which can be read on any OS.
  A language server could run in CI or on Linux/macOS editors.

## 3. Current landscape

As far as we could find (October 2026; worth re-verifying):

| Tool | Open source | WPF-aware | Editor-agnostic (LSP) | Notes |
|---|---|---|---|---|
| Visual Studio XAML language service | No | **Yes** | No | The reference experience: IntelliSense, diagnostics, go-to-definition, designer. Windows + VS only. |
| JetBrains Rider / ReSharper | No | **Yes** | No | Strong XAML support, commercial. |
| VS Code C# Dev Kit / .NET MAUI extension | No | No (MAUI XAML only) | No | XAML IntelliSense exists for MAUI, not WPF. |
| Avalonia language server / AvaloniaVS | **Yes** (MIT) | No (Avalonia dialect) | Partly | Closest prior art. Has a XAML completion engine and metadata loading. |
| DotNet.Meteor XAML language server | **Yes** | No (MAUI) | **Yes** | MAUI XAML LSP based on AvaloniaVS code. |
| Noesis LanguageServer | **Yes** | No (NoesisGUI) | **Yes** | XAML LSP for the NoesisGUI framework. |
| lemminx (Red Hat XML LS) | **Yes** (EPL) | No | **Yes** | Generic XML: well-formedness, completion from XSD, formatting. What WPF users get today. |
| XamlStyler | **Yes** (Apache-2.0) | Formatting only | CLI / VS extension | Opinionated XAML formatter; useful as a formatting backend. |
| csharp-lsp-mcp | **Yes** (MIT) | Partially | No (MCP for AI agents) | XAML validation / binding / resource analysis as MCP tools. Worth studying. |
| JustBarnt/XamlLsp | No license | Claims WPF | Intended | Very early, no README. |

**Gap:** an open-source, LSP-based, WPF-aware XAML language server.

## 4. What "understanding WPF XAML" means

Concrete capabilities, roughly in priority order.

### 4.1 Core (MVP)

1. **Parsing** that tolerates incomplete XAML while typing, including markup
   extensions (`{Binding ...}`, `{StaticResource ...}`, nested extensions).
2. **Namespace resolution:** default WPF namespaces, `xmlns:x`,
   `clr-namespace:...;assembly=...`, and `[XmlnsDefinition]` /
   `[XmlnsPrefix]` attributes declared by libraries.
3. **Type resolution** against:
   - WPF reference assemblies (`Microsoft.WindowsDesktop.App.Ref`),
   - NuGet package assemblies,
   - project references,
   - **types in the same project that may not be compiled yet** (the
     `x:Class` code-behind, user controls, converters). This needs a Roslyn
     compilation, not just built DLLs.
4. **Completion** for elements, attributes (CLR properties, dependency
   properties, attached properties like `Grid.Row`, routed events), enum
   values, `x:` directives, and markup extensions.
5. **Diagnostics:** unknown type, unknown property or event, invalid enum
   value, duplicate `x:Name`, `x:Class` mismatch, malformed markup extension.
6. **Hover:** type and property info with XML documentation.
7. **Go to definition** from XAML into C# (types, properties, event handlers in
   code-behind) and across XAML (resource keys, styles, templates).

### 4.2 Next

8. **Resources:** resolve `StaticResource` / `DynamicResource` keys through
   the element tree, `App.xaml`, `MergedDictionaries`, themes (`Generic.xaml`)
   and pack URIs. Warn on missing keys.
9. **Bindings:** resolve `Path` against `d:DataContext`, `DataTemplate
   DataType`, or `RelativeSource`. Opt-in warnings, since the `DataContext` is
   often only known at runtime.
10. **Code actions:** create an event handler in the code-behind, add a missing
    `xmlns`, extract a style or resource.
11. **Rename / find references:** `x:Name`, resource keys, and C# members used
    from XAML (needs cooperation with the C# language server).
12. **Formatting** (e.g. XamlStyler-compatible), document symbols / outline,
    folding.

### 4.3 Related problem worth solving alongside

WPF's markup compiler generates `.g.cs` files (fields for every `x:Name`,
`InitializeComponent`). Outside Visual Studio, the C# language server only sees
them after a full build, so code-behind shows false "name does not exist"
errors on fresh checkouts or new controls. A design-time generator, or a way
for the XAML server to feed those declarations to Roslyn, would remove this.

## 5. Technical challenges

- **Project system.** Knowing which assemblies and source files belong to a
  XAML file means evaluating MSBuild (SDK-style projects, multi-targeting,
  `Directory.Build.props`, central package management). Options:
  `MSBuildWorkspace`, Buildalyzer, or reusing Roslyn LSP's project loading.
- **Metadata without running WPF.** Reading WPF types must not need the WPF
  runtime: use `System.Reflection.Metadata` / `MetadataLoadContext` or Roslyn
  symbols, so it works on non-Windows machines and in CI.
- **WPF conventions, not just CLR types:** dependency properties
  (`XxxProperty` fields), attached properties (`GetXxx` / `SetXxx`), routed
  events, `[ContentProperty]`, `TypeConverter`s, markup extension classes,
  `x:Static` / `x:Type`, implicit styles, `ControlTemplate` / `TemplateBinding`
  context.
- **Scale and incrementality.** Large solutions (100+ projects, 700+ XAML
  files) with responsive completion (target < 100 ms) and incremental
  re-analysis as files change, including changes made by external tools such
  as AI agents and git.
- **Error tolerance.** Useful results on half-typed markup.
- **Coexisting with the C# language server.** The same symbols live in both
  worlds. Decide whether to run beside Roslyn LSP (separate process, separate
  compilation) or integrate with it (e.g. as a Roslyn LSP extension), and how
  to share work.
- **Dialect variations:** WPF, and possibly later MAUI or Avalonia, without
  becoming a lowest common denominator.

## 6. Prior art to study or reuse

- **Avalonia's IDE tooling** (MIT): `Avalonia.Ide.CompletionEngine`, metadata
  loading, AvaloniaVS. The most directly reusable design.
- **XamlX** (MIT): XAML compiler framework supporting multiple dialects; its
  type system and parser may fit.
- **DotNet.Meteor's XAML language server:** an LSP wrapper around the
  Avalonia-style engine for MAUI.
- **Roslyn language server:** architecture, project loading, and the extension
  model.
- **lemminx:** robust XML editing features (tag closing, formatting,
  folding) as a baseline, or as an example of an LSP built for markup.
- **XamlStyler:** formatting.
- **csharp-lsp-mcp:** XAML binding and resource analysis exposed to AI agents.

## 7. Goals and non-goals

**Goals**

- A standalone language server (LSP over stdio), installable as a `dotnet
  tool`, usable from any LSP-capable editor (Neovim, VS Code, Zed, Helix,
  Emacs) and wrappable as an MCP server for AI agents.
- WPF first: .NET 6+ SDK-style projects. .NET Framework projects as a stretch
  goal.
- Works on real enterprise solutions, not just samples.
- Open source under a permissive license (MIT or Apache-2.0).

**Non-goals (initially)**

- Visual designer or live preview.
- XAML hot reload or runtime binding debugging.
- MAUI, Avalonia, UWP, WinUI (keep the design open to them).
- Reimplementing C# language features (that's Roslyn's job).

## 8. Definition of done for an MVP

On a real solution like the motivating one (117 projects, ~720 XAML files):

- Opening a XAML file in Neovim gives completion for WPF and project types,
  their properties (including attached properties) and events.
- Unknown types, properties and events are flagged as diagnostics.
- Go-to-definition works from XAML to C# types, properties and handlers.
- Hover shows the type or property.
- Completion latency under 100 ms after initial load; initial load in a
  reasonable time for a 100+ project solution.
- Setup instructions and config for Neovim and VS Code.

Public WPF code bases for testing: `dotnet/wpf-samples`, MahApps.Metro,
ModernWpf, WPF UI (lepoco/wpfui), HandyControl.

## 9. Open questions

1. Build on Roslyn (`MSBuildWorkspace` and symbols) or on plain metadata
   reading plus a lightweight project model? The first gives unbuilt
   same-project types for free; the second is lighter and faster.
2. Standalone server, or an extension hosted inside the Roslyn language server
   so both share one compilation?
3. Reuse the Avalonia completion engine or XamlX, or write a WPF-specific
   engine?
4. How to handle `DataContext` for binding checks without false positives?
5. Name: e.g. *wpf-xaml-ls*, *OpenXaml LS*, *XamlSense*.
6. Can this become a community project (WPF community, .NET Foundation) so it
   is maintained beyond one person?
