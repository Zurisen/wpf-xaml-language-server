# Security policy

## Reporting a vulnerability

Please **don't** open a public issue for security problems. Report them privately through GitHub:
[Security → Report a vulnerability](https://github.com/Zurisen/wpf-xaml-language-server/security/advisories/new).

Include what you found, how to reproduce it, and the version (`wpf-xaml-ls --version`). You should get a first
response within a week.

## Scope

wpf-xaml-ls runs locally, on code you open in your editor. Relevant issues include:

- the server executing code or writing files outside what MSBuild design-time builds normally do;
- crafted XAML, project files or LSP messages that crash the server in a way an attacker could exploit;
- the server reading or sending data beyond the workspace it was started for.

Note that loading a project runs MSBuild design-time builds, the same as any .NET IDE does. Opening an untrusted
project in an editor with this server is as risky as opening it in Visual Studio or VS Code with C# Dev Kit.

## Supported versions

Only the latest release is supported while the project is in alpha.
