# WPF XAML Language Server for VS Code

Language support for **WPF XAML** in VS Code, powered by [wpf-xaml-ls](https://github.com/Zurisen/wpf-xaml-language-server).
It knows WPF's types and your project's own code, so you get:

- **Completion:**
  - elements, attributes, attached properties and values;
  - markup extensions;
  - resource keys;
  - binding paths, against your `d:DataContext` view model;
  - closing tags.
- **Diagnostics:**
  - unknown types, properties and events;
  - invalid values;
  - missing resources;
  - binding paths that don't exist;
  - event handlers missing from the code-behind.

  Each comes with a "Did you mean …?" suggestion where there is one. Problems are reported only when they're
  certain.
- **Quick fixes:**
  - apply a suggestion in one action;
  - generate a missing event handler in the code-behind;
  - add a missing `xmlns`.
- **Hover:** with the XML documentation.
- **Go to definition:** into C# types, properties and handlers, and into the XAML file that defines a resource.

It loads your solution with MSBuild, so it sees your project's types even before they're built.

## Requirements

- The .NET 10 SDK, or the .NET 10 runtime plus the SDK your projects use.
- The language server, installed as a .NET tool:

  ```
  dotnet tool install -g WpfXaml.LanguageServer --prerelease
  ```

- Projects restored (`dotnet restore`, or a build) so they can be loaded.

Open a folder that contains your solution (`.sln` or `.slnx`) or project, then open any `.xaml` file.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `wpfXamlLs.serverPath` | `wpf-xaml-ls` | Command that starts the server. |
| `wpfXamlLs.solution` | found automatically | The `.sln`, `.slnx` or `.csproj` to load, relative to the workspace folder. |
| `wpfXamlLs.loadProjects` | `true` | Load projects with MSBuild; when off, only WPF framework types are known. |
| `wpfXamlLs.frameworkVersion` | highest installed | WPF targeting pack used before a project loads (for example `8`). |
| `wpfXamlLs.trace.server` | `off` | Log the LSP messages in the **WPF XAML** output channel. |

Commands: **WPF XAML: Restart Language Server** and **WPF XAML: Show Log**.

## Feedback

This is an early alpha. Please [report issues](https://github.com/Zurisen/wpf-xaml-language-server/issues), especially
diagnostics that are wrong.
