using System.Diagnostics;
using WpfXaml.LanguageServer.Protocol;

namespace WpfXaml.LanguageServer.Tests;

/// <summary>
/// End-to-end over LSP with real MSBuild loading of <c>samples/</c>:
/// SampleWpfLib (control library) and SampleWpfApp (app referencing it, with an excluded Legacy folder).
/// Slow (build + design-time builds), so the scenarios share one session.
/// </summary>
public class ProjectLoadingTests
{
    [Fact]
    public async Task Samples_LoadOnDemand_UseBuiltReferences_AndRespectExcludedPages()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The samples target net8.0-windows; run on Windows.");
        var samples = FindSamplesDirectory();
        await BuildAsync(Path.Combine(samples, "SampleWpfApp"));

        // Only the library is configured, so the app project must be loaded when its XAML is opened.
        await using var client = await TestClient.StartAsync(new { solution = "SampleWpfLib/SampleWpfLib.csproj" }, samples);

        var xamlPath = Path.Combine(samples, "SampleWpfApp", "MainWindow.xaml");
        var uri = DocumentUris.FromPath(xamlPath);
        var text = (await File.ReadAllTextAsync(xamlPath, TestContext.Current.CancellationToken))
            .Replace("Click=\"OnOkClick\"", "Click=\"OnMissingClick\"", StringComparison.Ordinal);
        await client.OpenAsync(uri, text);

        // Only with the app project loaded can the server see code-behind and report the missing handler.
        var published = await client.WaitForDiagnosticsAsync(uri, d => d.Diagnostics.Any(x => x.Code == "XLS1008"), TimeSpan.FromMinutes(3));
        Assert.Single(published.Diagnostics);

        var lines = text.Split('\n');
        Position At(string marker, int offset = 2)
        {
            var line = Array.FindIndex(lines, l => l.Contains(marker, StringComparison.Ordinal));
            return new Position(line, lines[line].IndexOf(marker, StringComparison.Ordinal) + offset);
        }

        // A type from the app's own source.
        var badge = await client.RequestAsync<Location[]>("textDocument/definition", Request(uri, At("BadgeControl")));
        Assert.EndsWith("BadgeControl.cs", DocumentUris.ToPath(Assert.Single(badge).Uri), StringComparison.OrdinalIgnoreCase);

        // A type from the library, read from its built DLL: definition still lands in source, hover shows source docs.
        var tag = await client.RequestAsync<Location[]>("textDocument/definition", Request(uri, At("TagControl")));
        Assert.EndsWith("TagControl.cs", DocumentUris.ToPath(Assert.Single(tag).Uri), StringComparison.OrdinalIgnoreCase);
        var hover = await client.RequestAsync<Hover>("textDocument/hover", Request(uri, At("TagControl")));
        Assert.Contains("A rounded label for tagging items.", hover.Contents.Value);

        // Hover on a project property shows its XML docs.
        var count = await client.RequestAsync<Hover>("textDocument/hover", Request(uri, At("Count=", 1)));
        Assert.Contains("The number shown in the badge.", count.Contents.Value);

        // A XAML file the project excludes (<Page Remove="Legacy\**" />): no semantic errors, just a note.
        var legacyPath = Path.Combine(samples, "SampleWpfApp", "Legacy", "OldDialog.xaml");
        var legacyUri = DocumentUris.FromPath(legacyPath);
        await client.OpenAsync(legacyUri, await File.ReadAllTextAsync(legacyPath, TestContext.Current.CancellationToken));
        var legacy = await client.WaitForDiagnosticsAsync(legacyUri, d => d.Diagnostics.Any(x => x.Code == "XLS9001"));
        Assert.Equal(3, Assert.Single(legacy.Diagnostics).Severity);
    }

    private static TextDocumentPositionParams Request(string uri, Position position) =>
        new(new TextDocumentIdentifier(uri), position);

    private static string FindSamplesDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples");
            if (Directory.Exists(Path.Combine(candidate, "SampleWpfApp")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("samples/SampleWpfApp not found.");
    }

    /// <summary>MSBuildWorkspace does not restore, and the built-reference path needs the library's DLL.</summary>
    private static async Task BuildAsync(string projectDir)
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", "build --nologo -v:q")
        {
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await output);
    }
}
