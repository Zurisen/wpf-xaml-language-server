using System.Diagnostics;
using WpfXaml.LanguageServer.Protocol;

namespace WpfXaml.LanguageServer.Tests;

/// <summary>
/// End-to-end: loads <c>samples/SampleWpfApp</c> through MSBuildWorkspace and checks project-aware results.
/// Slow (design-time build), so kept to a single test.
/// </summary>
public class ProjectLoadingTests
{
    [Fact]
    public async Task SampleProject_LoadsAndResolvesProjectTypes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The sample targets net8.0-windows; run on Windows.");
        var sampleDir = FindSampleDirectory();
        await RestoreAsync(sampleDir);

        await using var client = await TestClient.StartAsync(new { loadProjects = true }, sampleDir);

        var xamlPath = Path.Combine(sampleDir, "MainWindow.xaml");
        var uri = DocumentUris.FromPath(xamlPath);
        var text = (await File.ReadAllTextAsync(xamlPath, TestContext.Current.CancellationToken))
            .Replace("Click=\"OnOkClick\"", "Click=\"OnMissingClick\"", StringComparison.Ordinal);
        await client.OpenAsync(uri, text);

        // Only with the project loaded can the server see code-behind and report the missing handler.
        var published = await client.WaitForDiagnosticsAsync(uri, d => d.Diagnostics.Any(x => x.Code == "XLS1008"), TimeSpan.FromMinutes(3));
        Assert.Single(published.Diagnostics);

        // Go to definition on <local:BadgeControl> lands in BadgeControl.cs.
        var lines = text.Split('\n');
        var line = Array.FindIndex(lines, l => l.Contains("<local:BadgeControl", StringComparison.Ordinal));
        var character = lines[line].IndexOf("BadgeControl", StringComparison.Ordinal) + 2;
        var locations = await client.RequestAsync<Location[]>("textDocument/definition", TestClient.Position(uri, line, character));

        var location = Assert.Single(locations);
        Assert.EndsWith("BadgeControl.cs", DocumentUris.ToPath(location.Uri), StringComparison.OrdinalIgnoreCase);

        // Hover on a project property shows its XML docs.
        var count = lines[line].IndexOf("Count", StringComparison.Ordinal) + 1;
        var hover = await client.RequestAsync<Hover>("textDocument/hover", TestClient.Position(uri, line, count));
        Assert.Contains("The number shown in the badge.", hover.Contents.Value);
    }

    private static string FindSampleDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "SampleWpfApp");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("samples/SampleWpfApp not found.");
    }

    /// <summary>MSBuildWorkspace does not restore; make sure project.assets.json exists.</summary>
    private static async Task RestoreAsync(string projectDir)
    {
        if (File.Exists(Path.Combine(projectDir, "obj", "project.assets.json")))
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("dotnet", "restore")
        {
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
    }
}
