using System.Globalization;
using Microsoft.CodeAnalysis;

namespace WpfXaml.LanguageServer.Workspace;

/// <summary>
/// Replaces project references with references to the projects' built assemblies when those are up to date.
/// <para>
/// Compiling a large project from source means Roslyn first has to produce metadata for every project it
/// depends on, which on a 100+ project solution takes many seconds. When a dependency's DLL is newer than all
/// of its sources, reading the DLL gives the same types at a fraction of the cost. Projects with newer sources
/// (being edited, or never built) stay project references, so results are never stale.
/// </para>
/// </summary>
internal static class BuiltReferences
{
    public static Solution Substitute(Solution solution, Func<ProjectId, Compilation?> sourceCompilation, Action<string>? log, out int substitutedProjects)
    {
        var built = new Dictionary<ProjectId, PortableExecutableReference>();
        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
            {
                continue;
            }

            if (IsUpToDate(project, out var reason) is { } output)
            {
                var id = project.Id;
                built[id] = MetadataReference.CreateFromFile(output, documentation: CreateDocumentation(output, () => sourceCompilation(id)));
            }
            else
            {
                log?.Invoke($"{project.Name}: compiled from source ({reason}).");
            }
        }

        substitutedProjects = built.Count;
        if (built.Count == 0)
        {
            return solution;
        }

        var result = solution;
        foreach (var project in solution.Projects)
        {
            foreach (var reference in project.ProjectReferences)
            {
                if (built.TryGetValue(reference.ProjectId, out var assembly))
                {
                    var properties = MetadataReferenceProperties.Assembly.WithAliases(reference.Aliases).WithEmbedInteropTypes(reference.EmbedInteropTypes);
                    result = result.RemoveProjectReference(project.Id, reference)
                        .AddMetadataReference(project.Id, assembly.WithProperties(properties));
                }
            }
        }

        return result;
    }

    /// <summary>The project's output assembly, if it exists and is newer than the project file and every source file.</summary>
    private static string? IsUpToDate(Project project, out string reason)
    {
        if (project.OutputFilePath is not { } output || project.FilePath is null || !File.Exists(output))
        {
            reason = $"no build output at {project.OutputFilePath ?? "(unknown)"}";
            return null;
        }

        var builtAt = File.GetLastWriteTimeUtc(output);
        if (File.GetLastWriteTimeUtc(project.FilePath) > builtAt)
        {
            reason = "project file changed since the last build";
            return null;
        }

        // Generated files in the intermediate directory (obj/, or wherever IntermediateOutputPath points) are
        // rewritten by builds and design-time builds alike; they say nothing about whether the user's code changed.
        string[] generatedDirectories =
        [
            Path.Combine(Path.GetDirectoryName(project.FilePath)!, "obj") + Path.DirectorySeparatorChar,
            .. project.CompilationOutputInfo.AssemblyPath is { } intermediate && Path.GetDirectoryName(intermediate) is { } dir
                ? [dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar]
                : Array.Empty<string>(),
        ];

        foreach (var document in project.Documents)
        {
            if (document.FilePath is not { } path || generatedDirectories.Any(d => path.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) > builtAt)
            {
                reason = $"{Path.GetFileName(path)} changed since the last build";
                return null;
            }
        }

        reason = string.Empty;
        return output;
    }

    private static DocumentationProvider CreateDocumentation(string assemblyPath, Func<Compilation?> sourceCompilation)
    {
        var xml = Path.ChangeExtension(assemblyPath, ".xml");
        return File.Exists(xml) ? XmlDocumentationProvider.CreateFromFile(xml) : new SourceDocumentationProvider(assemblyPath, sourceCompilation);
    }

    /// <summary>
    /// Documentation for a built project assembly without a generated .xml file: read the doc comments from the
    /// project's source, compiling it only when documentation is first asked for.
    /// </summary>
    private sealed class SourceDocumentationProvider(string assemblyPath, Func<Compilation?> sourceCompilation) : DocumentationProvider
    {
        private readonly Lazy<Compilation?> _compilation = new(sourceCompilation);

        protected override string? GetDocumentationForSymbol(string documentationMemberID, CultureInfo preferredCulture, CancellationToken cancellationToken = default)
        {
            var compilation = _compilation.Value;
            return compilation is null ? null : DocumentationCommentId.GetFirstSymbolForDeclarationId(documentationMemberID, compilation)?.GetDocumentationCommentXml(preferredCulture, cancellationToken: cancellationToken);
        }

        public override bool Equals(object? obj) => obj is SourceDocumentationProvider other && string.Equals(other.AssemblyPath, AssemblyPath, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(AssemblyPath);

        private string AssemblyPath => assemblyPath;
    }
}
