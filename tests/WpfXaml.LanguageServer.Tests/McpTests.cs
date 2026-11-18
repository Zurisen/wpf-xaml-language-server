using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WpfXaml.LanguageServer.Mcp;

namespace WpfXaml.LanguageServer.Tests;

/// <summary>The MCP server (<c>wpf-xaml-ls mcp</c>) on the sample projects: over the real protocol, and the tools directly.</summary>
public class McpTests
{
    private const string MainWindow = "SampleWpfApp/MainWindow.xaml";

    [Fact]
    public async Task Protocol_ListsToolsAndChecksUnsavedContent()
    {
        await SampleProjects.EnsureBuiltAsync();
        var server = Path.Combine(AppContext.BaseDirectory, "wpf-xaml-ls.dll");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "wpf-xaml-ls",
            Command = "dotnet",
            Arguments = [server, "mcp", SampleProjects.Directory],
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["xaml_binding_context", "xaml_check", "xaml_describe_type", "xaml_fix", "xaml_resources"], tools.Select(t => t.Name).Order());

        var content = (await File.ReadAllTextAsync(Path.Combine(SampleProjects.Directory, MainWindow), TestContext.Current.CancellationToken))
            .Replace("FontWeight=\"Bold\"", "FontWieght=\"Bold\"", StringComparison.Ordinal);
        var result = await client.CallToolAsync("xaml_check", new Dictionary<string, object?> { ["path"] = MainWindow, ["content"] = content },
            cancellationToken: TestContext.Current.CancellationToken);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

        Assert.Contains("error XLS1003: 'FontWieght' is not a property or event of 'TextBlock'. Did you mean 'FontWeight'?", text);
        Assert.Contains("fix: Change to 'FontWeight'", text);
        Assert.Contains("1 error(s), 0 warning(s)", text);
    }

    [Fact]
    public async Task Tools_FixDescribeResourcesAndBindingContext()
    {
        await SampleProjects.EnsureBuiltAsync();
        using var workspace = new XamlWorkspace(new McpOptions(SampleProjects.Directory, null), new ServerLog(LogLevel.Error));
        var tools = new XamlTools(workspace);
        var ct = TestContext.Current.CancellationToken;

        var original = await File.ReadAllTextAsync(Path.Combine(SampleProjects.Directory, MainWindow), ct);
        var broken = original.Replace("{Binding Greeting}", "{Binding Greting}", StringComparison.Ordinal)
            .Replace("Click=\"OnOkClick\"", "Click=\"OnMissing\"", StringComparison.Ordinal);

        // Fix: the binding typo is fixed in the returned content; the handler fix edits code-behind, so it's only listed.
        var fixedText = await tools.FixAsync(MainWindow, broken, ct);
        Assert.Contains("Applied 1 fix(es):", fixedText);
        Assert.Contains("{Binding Greeting}", fixedText);
        Assert.Contains("Generate event handler 'OnMissing' in MainWindow.xaml.cs", fixedText);

        var badge = await tools.DescribeTypeAsync("local:BadgeControl", MainWindow, ct);
        Assert.Contains("SampleWpfApp.BadgeControl : Control", badge);
        Assert.Contains("xmlns: clr-namespace:SampleWpfApp", badge);
        Assert.Contains("Count: int", badge);
        Assert.Contains("Summary: Shows a small count badge.", badge);

        var grid = await tools.DescribeTypeAsync("Grid", null, ct);
        Assert.Contains("Content property: Children", grid);
        Assert.Contains("Grid.Row: int", grid);

        var lines = original.Split('\n');
        var buttonLine = Array.FindIndex(lines, l => l.Contains("<Button", StringComparison.Ordinal)) + 1;

        var resources = await tools.ResourcesAsync(MainWindow, buttonLine, 10, ct);
        Assert.Contains("AccentBrush: SolidColorBrush", resources);
        Assert.Contains("LibAccentBrush: SolidColorBrush", resources);
        Assert.Contains("Inverse: local:InverseBooleanConverter (this file:", resources);

        var context = await tools.BindingContextAsync(MainWindow, buttonLine, 10, ct);
        Assert.Contains("Data context at <Button>: SampleWpfApp.MainViewModel (or a subclass), from d:DataContext.", context);
        Assert.Contains("IsBusy: bool", context);

        var folder = await tools.CheckAsync(".", null, ct);
        Assert.Contains("no problems found", folder);
    }
}
