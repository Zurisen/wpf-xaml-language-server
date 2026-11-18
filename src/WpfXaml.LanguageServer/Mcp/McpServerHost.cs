using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WpfXaml.LanguageServer.Mcp;

/// <summary><c>wpf-xaml-ls mcp</c>: the analysis exposed as MCP tools over stdio, for AI agents.</summary>
internal static class McpServerHost
{
    public static async Task<int> RunAsync(McpOptions options, ServerLog log)
    {
        var builder = Host.CreateApplicationBuilder();

        // stdout carries the protocol: everything else goes to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace);
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(log);
        builder.Services.AddSingleton<XamlWorkspace>();
        builder.Services
            .AddMcpServer(server => server.ServerInfo = new() { Name = "wpf-xaml-ls", Version = ServerVersion.Current })
            .WithStdioServerTransport()
            .WithTools<XamlTools>();

        using var host = builder.Build();

        // Start loading projects right away, not on the first tool call.
        _ = host.Services.GetRequiredService<XamlWorkspace>();
        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }
}
