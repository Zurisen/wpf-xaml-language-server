using System.Collections.Concurrent;
using System.Diagnostics;
using WpfXaml.Analysis.Semantics;
using WpfXaml.Language;
using WpfXaml.Language.Syntax;
using WpfXaml.LanguageServer.Workspace;

namespace WpfXaml.LanguageServer;

/// <summary>
/// <c>wpf-xaml-ls check [path]</c>: loads a solution or project and prints diagnostics for every XAML file,
/// in MSBuild format. For CI and for agents that want feedback without an editor. Exit code 1 when errors are found.
/// </summary>
internal static class CheckCommand
{
    public static async Task<int> RunAsync(string? target, bool frameworkOnly, ServerLog log)
    {
        var root = target is not null && Directory.Exists(target) ? Path.GetFullPath(target) : Directory.GetCurrentDirectory();
        var explicitFile = target is not null && File.Exists(target) ? Path.GetFullPath(target) : null;
        var targets = ProjectSystem.DiscoverLoadTargets(explicitFile, root);
        if (explicitFile is not null)
        {
            root = Path.GetDirectoryName(explicitFile)!;
        }

        using var projects = new ProjectSystem(log, loadOnDemand: !frameworkOnly, waitForGenerators: true);
        var stopwatch = Stopwatch.StartNew();
        if (!frameworkOnly)
        {
            if (targets.Count == 0)
            {
                log.Warning($"No solution or project found under {root}; checking with framework types only.");
            }

            projects.StartLoading(targets);
            await projects.WaitForLoadsAsync();
        }

        var files = EnumerateXamlFiles(root).ToList();
        if (!frameworkOnly)
        {
            // Files whose project isn't in the solution trigger loading that project; let those finish first.
            foreach (var path in files)
            {
                projects.EnsureOwnerLoading(path);
            }

            await projects.WaitForLoadsAsync();
        }

        var loadTime = stopwatch.Elapsed;
        var results = new ConcurrentBag<(string Path, XamlDocument Document, IReadOnlyList<XamlDiagnostic> Diagnostics)>();
        var excluded = 0;
        stopwatch.Restart();

        await Parallel.ForEachAsync(files, async (path, cancellationToken) =>
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken);
            var document = XamlDocument.Parse(text);
            var context = await projects.GetContextAsync(path, cancellationToken);
            if (context.Status == XamlFileStatus.ExcludedFromProject)
            {
                Interlocked.Increment(ref excluded);
                results.Add((path, document, document.Diagnostics));
                return;
            }

            var diagnostics = context.Types is null ? document.Diagnostics : new XamlSemanticModel(document, context.Types).GetDiagnostics(cancellationToken);
            results.Add((path, document, diagnostics));
        });

        var errors = 0;
        var warnings = 0;
        foreach (var (path, document, diagnostics) in results.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var diagnostic in diagnostics.OrderBy(d => d.Span.Start))
            {
                var position = document.Lines.GetPosition(diagnostic.Span.Start);
                var severity = diagnostic.Severity == XamlDiagnosticSeverity.Error ? "error" : "warning";
                Console.WriteLine($"{Path.GetRelativePath(root, path)}({position.Line + 1},{position.Character + 1}): {severity} {diagnostic.Code}: {diagnostic.Message}");
                if (diagnostic.Severity == XamlDiagnosticSeverity.Error)
                {
                    errors++;
                }
                else
                {
                    warnings++;
                }
            }
        }

        Console.WriteLine();
        var excludedNote = excluded > 0 ? $" ({excluded} excluded from their project's build: syntax only)" : string.Empty;
        Console.WriteLine($"{files.Count} XAML files{excludedNote}, {errors} errors, {warnings} warnings. Load {loadTime.TotalSeconds:F1}s, analysis {stopwatch.Elapsed.TotalSeconds:F1}s.");
        return errors > 0 ? 1 : 0;
    }

    private static IEnumerable<string> EnumerateXamlFiles(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(root, "*.xaml", options)
            .Where(p => !p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".git" or ".vs"));
    }
}
