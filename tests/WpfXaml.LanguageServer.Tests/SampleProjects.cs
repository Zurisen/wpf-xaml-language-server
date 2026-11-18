using System.Diagnostics;

namespace WpfXaml.LanguageServer.Tests;

/// <summary>The sample WPF projects in <c>samples/</c>, built once per test run (Windows only).</summary>
internal static class SampleProjects
{
    private static readonly SemaphoreSlim BuildLock = new(1, 1);
    private static bool _built;

    public static string Directory { get; } = FindDirectory();

    /// <summary>Skips on non-Windows; builds the samples (restore + build) the first time it's called.</summary>
    public static async Task EnsureBuiltAsync()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The samples target net8.0-windows; run on Windows.");
        await BuildLock.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (_built)
            {
                return;
            }

            // MSBuildWorkspace does not restore, and the built-reference path needs the library's DLL.
            using var process = Process.Start(new ProcessStartInfo("dotnet", "build --nologo -v:q")
            {
                WorkingDirectory = Path.Combine(Directory, "SampleWpfApp"),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await output);
            _built = true;
        }
        finally
        {
            BuildLock.Release();
        }
    }

    private static string FindDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples");
            if (System.IO.Directory.Exists(Path.Combine(candidate, "SampleWpfApp")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("samples/SampleWpfApp not found.");
    }
}
