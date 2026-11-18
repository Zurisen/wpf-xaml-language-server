using System.Reflection;

namespace WpfXaml.LanguageServer;

internal static class ServerVersion
{
    /// <summary>The package version, without the source revision suffix.</summary>
    public static string Current { get; } =
        typeof(ServerVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
