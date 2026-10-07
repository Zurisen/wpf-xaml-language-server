using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace WpfXaml.Analysis.Framework;

/// <summary>
/// Locates the .NET and WPF reference assemblies (targeting packs) on disk, without needing WPF itself.
/// Looks in the dotnet installation's <c>packs</c> folder and in the NuGet package cache, so it also works
/// on Linux/macOS when the WindowsDesktop targeting pack was restored with <c>EnableWindowsTargeting</c>.
/// </summary>
public static class FrameworkReferences
{
    private const string NetCorePack = "Microsoft.NETCore.App.Ref";
    private const string WindowsDesktopPack = "Microsoft.WindowsDesktop.App.Ref";

    private static readonly ConcurrentDictionary<string, MetadataReference> ReferenceCache = new(StringComparer.OrdinalIgnoreCase);

    public static string? FindDotnetRoot()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.GetDirectoryName(Environment.ProcessPath) is { } processDir && File.Exists(Path.Combine(processDir, DotnetExecutable)) ? processDir : null,
            FindOnPath(),
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
        }
        else
        {
            candidates.AddRange(["/usr/share/dotnet", "/usr/local/share/dotnet", "/usr/lib/dotnet", "/opt/homebrew/share/dotnet"]);
        }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"));

        return candidates.FirstOrDefault(c => c is not null && Directory.Exists(Path.Combine(c, "packs", NetCorePack)));
    }

    /// <summary>
    /// Returns the reference assembly paths for a WPF app targeting <c>net{major}.0</c>
    /// (the highest available version when <paramref name="preferredMajor"/> is null).
    /// Empty when no WindowsDesktop targeting pack is found.
    /// </summary>
    public static IReadOnlyList<string> FindWpfReferencePaths(int? preferredMajor = null, string? dotnetRoot = null)
    {
        dotnetRoot ??= FindDotnetRoot();
        var desktop = FindPack(WindowsDesktopPack, preferredMajor, dotnetRoot);
        if (desktop is null)
        {
            return [];
        }

        var netCore = FindPack(NetCorePack, desktop.Value.Major, dotnetRoot);
        if (netCore is null)
        {
            return [];
        }

        return Directory.GetFiles(netCore.Value.RefDir, "*.dll")
            .Concat(Directory.GetFiles(desktop.Value.RefDir, "*.dll"))
            .ToList();
    }

    /// <summary>Creates (and caches) a metadata reference, attaching XML documentation found next to the assembly.</summary>
    public static MetadataReference GetReference(string path) =>
        ReferenceCache.GetOrAdd(path, static p =>
        {
            var xml = Path.ChangeExtension(p, ".xml");
            var documentation = File.Exists(xml) ? XmlDocumentationProvider.CreateFromFile(xml) : null;
            return MetadataReference.CreateFromFile(p, documentation: documentation);
        });

    /// <summary>A compilation with only framework references: the fallback when no project is loaded.</summary>
    public static CSharpCompilation CreateFrameworkCompilation(IEnumerable<string> referencePaths, IEnumerable<SyntaxTree>? sources = null, string assemblyName = "XamlFrameworkOnly") =>
        CSharpCompilation.Create(
            assemblyName,
            sources,
            referencePaths.Select(GetReference),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private static string DotnetExecutable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";

    private static string? FindOnPath()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, DotnetExecutable);
                if (File.Exists(candidate))
                {
                    var resolved = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
                    return Path.GetDirectoryName(resolved);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }

        return null;
    }

    private static (int Major, string RefDir)? FindPack(string packName, int? major, string? dotnetRoot)
    {
        var roots = new List<string>();
        if (dotnetRoot is not null)
        {
            roots.Add(Path.Combine(dotnetRoot, "packs", packName));
        }

        var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        roots.Add(Path.Combine(nugetRoot, packName.ToLowerInvariant()));

        var found = new List<(Version Version, int Major, string RefDir)>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var versionDir in Directory.GetDirectories(root))
            {
                var versionText = Path.GetFileName(versionDir).Split('-')[0];
                if (!Version.TryParse(versionText, out var version))
                {
                    continue;
                }

                var refDir = Path.Combine(versionDir, "ref", $"net{version.Major}.0");
                if (Directory.Exists(refDir) && (major is null || version.Major == major))
                {
                    found.Add((version, version.Major, refDir));
                }
            }
        }

        return found.Count == 0 ? null : found.MaxBy(f => f.Version) is var best ? (best.Major, best.RefDir) : null;
    }
}
