using System.Reflection;
using WpfXaml.LanguageServer;

string? logFile = null;
var logLevel = LogLevel.Info;
var check = false;
var frameworkOnly = false;
string? checkTarget = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "check" when i == 0:
            check = true;
            logLevel = LogLevel.Warning;
            break;
        case "--framework-only":
            frameworkOnly = true;
            break;
        case "--version":
            Console.WriteLine(typeof(XamlLanguageServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
            return 0;
        case "--help" or "-h":
            Console.WriteLine("""
                wpf-xaml-ls: language server for WPF XAML

                Usage:
                  wpf-xaml-ls [--stdio]               Run the language server over stdin/stdout.
                  wpf-xaml-ls check [path]            Print diagnostics for all XAML files under a folder,
                                                      solution or project (exit code 1 on errors).

                Options:
                  --log-file <path>   Also write the log to a file.
                  --log-level <level> debug | info | warning | error (default: info).
                  --framework-only    check: don't load projects, use WPF framework types only.
                  --version           Print the version.
                """);
            return 0;
        case "--log-file" when i + 1 < args.Length:
            logFile = args[++i];
            break;
        case "--log-level" when i + 1 < args.Length && Enum.TryParse<LogLevel>(args[i + 1], ignoreCase: true, out var level):
            logLevel = level;
            i++;
            break;
        default:
            if (check && !args[i].StartsWith('-'))
            {
                checkTarget = args[i];
            }

            break;
    }
}

if (check)
{
    using var checkLog = new ServerLog(logLevel, logFile);
    return await CheckCommand.RunAsync(checkTarget, frameworkOnly, checkLog);
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
