# Architecture

This document records how wpf-xaml-ls is built and the decisions behind it. It answers the open questions in
[PROBLEM_STATEMENT.md](../PROBLEM_STATEMENT.md#9-open-questions).

## Layers

```
WpfXaml.Language        Error-tolerant XAML parser and markup-extension parser. No dependencies.
   ▲
WpfXaml.Analysis        Type system over a Roslyn Compilation, semantic model, diagnostics,
   ▲                    completion, hover, go to definition. Editor- and protocol-agnostic.
WpfXaml.LanguageServer  LSP over stdio (StreamJsonRpc), document store, project system
                        (MSBuildWorkspace), `check` command. Ships as the `wpf-xaml-ls` dotnet tool.
```

Analysis results use offsets and `TextSpan`s; only the server layer knows about LSP positions and URIs. That keeps
the analysis reusable from other hosts, such as an MCP server for AI agents or a Roslyn LSP extension.

### Parser (`WpfXaml.Language`)

- A hand-written scanner rather than `System.Xml`, because it must accept half-typed markup and report exact spans.
- It never throws and always returns a tree. Recovery rules:
  - An unclosed start tag (`<Button Con`) ends at the next `<` and is treated as self-closing.
  - An unterminated attribute value ends at the next `<`.
  - An end tag closes the nearest open element with the same name.
  - A bare `<` produces an element with an empty name, which completion uses as its "element name" context.
- Markup extensions are parsed into their own tree (type name, positional and named arguments, nested extensions).
  Spans are absolute document offsets. Indexers (`Items[a,b]`) and the `{}` escape (`StringFormat={}{0:N2}`) are
  handled.

### Type system (`WpfXaml.Analysis.TypeSystem`)

- `XamlTypeSystem` wraps one Roslyn `Compilation`. It reads `[XmlnsDefinition]` and `[XmlnsPrefix]` from every
  referenced assembly and from the project's own assembly. XAML namespaces resolve to a set of
  (assembly, CLR namespace) pairs.
- `clr-namespace:X;assembly=Y` resolves against the compilation's references, including type forwarders such as
  `mscorlib` → `System.Runtime`.
- `XamlTypeInfo` adds the WPF conventions on top of the CLR types:
  - instance properties and events, including inherited ones;
  - attachable properties (`GetX`/`SetX`) and attachable events (`AddXHandler`);
  - routed events attached through their `XEvent` field;
  - `[ContentProperty]`, `[RuntimeNameProperty]`, markup extensions (the `Extension` suffix is optional), templates.

### Semantic model and features

- `XamlSemanticModel` binds each element and attribute lazily and caches the result. One model exists per document
  version and type system.
- Diagnostics come from the same bindings. `mc:Ignorable` namespaces (such as `d:`) are skipped.
- Errors for unknown names include "Did you mean …?" suggestions based on edit distance. These help humans, and they
  help AI agents in particular.
- Hover and go to definition share one resolver (`XamlTargetResolver`) that answers "what is under the caret".

## Decisions

### 1. Roslyn compilations, not a lightweight metadata reader

Types from the same project that haven't been compiled yet are a hard requirement: `x:Class` code-behind, user
controls, converters. A Roslyn `Compilation` provides them for free, it also reads referenced assemblies as metadata
(no WPF runtime, so it works on Linux and in CI), and it gives XML documentation and source locations for go to
definition.

The cost is memory and the time of the first compilation. The project system (below) keeps that down. On the
117-project motivating solution:

| Step | Time |
|---|---|
| Load all projects (design-time builds) | ~22–30 s |
| First diagnostics for a file in the largest project | ~2.4 s (generator-free compilation) |
| Complete diagnostics, with source generators | ~10 s, replacing the first ones |
| Completion after warm-up | 10–15 ms median |
| Analyze all 719 XAML files with `check` | ~15 s |

### 2. A standalone server, not a Roslyn LSP extension

A standalone process works with every editor today and keeps the dependency surface small. The analysis layer only
needs a `Compilation`, so hosting it inside the Roslyn language server (to share one compilation with C#) is still
possible later, without a rewrite.

### 3. A WPF-specific engine, informed by Avalonia's

Avalonia's completion engine and XamlX target other dialects and their own metadata model. A WPF-specific engine
over Roslyn symbols turned out small. The Avalonia design (namespace mapping, completion contexts) was the
reference.

### 4. Bindings and `DataContext` (not implemented yet)

The plan is opt-in: resolve `Path` against `d:DataContext`, `DataTemplate DataType`, or `x:DataType`, and stay
silent when the context is unknown.

### Project system

- The server discovers a `.slnx`, `.sln` or `.csproj` under the workspace root, or uses `initializationOptions.solution`.
  It then loads it in the background with `MSBuildWorkspace`, which runs design-time builds in its own BuildHost
  process.
- Until the load finishes, and for files outside any project, the server uses a framework-only compilation built
  from the WPF targeting pack (`Microsoft.WindowsDesktop.App.Ref`). In that mode, names from project or
  third-party namespaces are not reported, because the server can't know them. The same fallback applies to a
  project that loads without any WPF references (not restored, or a legacy .NET Framework project whose reference
  assemblies aren't installed).
- **Which project owns a XAML file is known exactly.** The server passes
  `CustomAfterMicrosoftCommonTargets=WpfXamlLs.DesignTime.targets` to its design-time builds. That target copies
  each project's evaluated `Page` and `ApplicationDefinition` items into `AdditionalFiles`, which Roslyn reports per
  project. Globs, `Remove`, linked files and conditions are all handled by MSBuild itself.
  - A file in a loaded project's folder but missing from its `Page` items is excluded from the build. It gets
    syntax checks plus an informational `XLS9001` note.
  - Projects that report no XAML items fall back to directory ownership.
  - A known limitation: this global property overrides a `CustomAfterMicrosoftCommonTargets` that a repository sets
    itself.
- **Projects are loaded on demand.** If the nearest `.csproj` above an opened XAML file isn't in the loaded
  solution, it is loaded into the same workspace.
- **Built references.** When a referenced project's output DLL is newer than its project file and every
  non-generated source file, the reference is read from the DLL instead of compiled from source. On the motivating
  solution this applies to 114 of 117 projects. Two details keep this transparent:
  - Hover documentation comes from the project's `.xml` docs, or else from its source, compiled lazily.
  - Go to definition maps DLL symbols back to the project's source.
- **Compilation in two passes.** Source generators are most of the cost of compiling a large project. So the
  first diagnostics come from a compilation without analyzers/generators. The complete compilation runs in the
  background and refreshes diagnostics when it's ready. `check` always waits for the complete one.
- `.cs` changes reach the server through `workspace/didChangeWatchedFiles` and are applied to the in-memory
  solution. A changed project becomes "not up to date" by its timestamps, so it is compiled from source again.
  Changes to project files trigger a reload.
- NuGet audit is disabled during loads, because it would query (often private) feeds every time.

## Known gaps / next steps

- **Rebuild detection.** A rebuild that makes a DLL up to date again is noticed when the next `.cs` or project
  change invalidates the snapshot, not immediately. Watching output assemblies would fix that.
- **Resources.** Resolve keys across `App.xaml`, `MergedDictionaries`, themes and pack URIs, and warn on missing
  keys.
- **Bindings.** See decision 4.
- **Code actions.** Generate an event handler, add a missing `xmlns`.
- **Rename and find references** for `x:Name` and resource keys.
- **Generated code.** Feed the `x:Name` fields and `InitializeComponent` (normally in `.g.cs`) to the C# side.
- **Formatting, document symbols, folding.**
- **Clients.** A VS Code extension and an MCP wrapper.
