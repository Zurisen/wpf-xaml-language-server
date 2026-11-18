using WpfXaml.LanguageServer;
using WpfXaml.LanguageServer.Mcp;

string? logFile = null;
var logLevel = LogLevel.Info;
string? command = args.Length > 0 && args[0] is "check" or "mcp" ? args[0] : null;
var frameworkOnly = false;
string? target = null;
string? solution = null;
for (var i = command is null ? 0 : 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--framework-only":
            frameworkOnly = true;
            break;
        case "--version":
            Console.WriteLine(ServerVersion.Current);
            return 0;
        case "--help" or "-h":
            Console.WriteLine("""
                wpf-xaml-ls: language server for WPF XAML

                Usage:
                  wpf-xaml-ls [--stdio]               Run the language server over stdin/stdout.
                  wpf-xaml-ls check [path]            Print diagnostics for all XAML files under a folder,
                                                      solution or project (exit code 1 on errors).
                  wpf-xaml-ls mcp [folder]            Run as an MCP server over stdin/stdout, for AI agents.
                                                      Loads the solution or projects found under the folder
                                                      (default: the current directory).

                Options:
                  --solution <path>   mcp: load this .sln/.slnx/.csproj instead of discovering one.
                  --log-file <path>   Also write the log to a file.
                  --log-level <level> debug | info | warning | error (default: info; warning for check and mcp).
                  --framework-only    check: don't load projects, use WPF framework types only.
                  --version           Print the version.
                """);
            return 0;
        case "--log-file" when i + 1 < args.Length:
            logFile = args[++i];
            break;
        case "--solution" when i + 1 < args.Length:
            solution = args[++i];
            break;
        case "--log-level" when i + 1 < args.Length && Enum.TryParse<LogLevel>(args[i + 1], ignoreCase: true, out var level):
            logLevel = level;
            i++;
            break;
        default:
            if (command is not null && !args[i].StartsWith('-'))
            {
                target = args[i];
            }

            break;
    }
}

if (command is not null && !args.Contains("--log-level"))
{
    logLevel = LogLevel.Warning;
}

if (command == "check")
{
    using var checkLog = new ServerLog(logLevel, logFile);
    return await CheckCommand.RunAsync(target, frameworkOnly, checkLog);
}

if (command == "mcp")
{
    using var mcpLog = new ServerLog(logLevel, logFile);
    var root = Path.GetFullPath(target ?? Directory.GetCurrentDirectory());
    return await McpServerHost.RunAsync(new McpOptions(root, solution), mcpLog);
}

// stdout carries the protocol: take the raw streams, then route anything else written to Console.Out to stderr.
var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
Console.SetOut(Console.Error);

using var log = new ServerLog(logLevel, logFile);
using var server = XamlLanguageServer.Create(input, output, log);
server.Start();
await server.Completion;
log.Info("Exiting.");
return server.ShutdownRequested ? 0 : 1;
