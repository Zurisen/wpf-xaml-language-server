namespace WpfXaml.LanguageServer.Protocol;

/// <summary>Conversions between LSP <c>file://</c> URIs and local paths.</summary>
public static class DocumentUris
{
    public static string? ToPath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
        {
            return null;
        }

        // Clients differ in escaping ("file:///c%3A/x" vs "file:///C:/x"); unescape explicitly.
        var path = Uri.UnescapeDataString(parsed.AbsolutePath);
        if (OperatingSystem.IsWindows())
        {
            if (!string.IsNullOrEmpty(parsed.Host))
            {
                return $@"\\{parsed.Host}{path.Replace('/', '\\')}";
            }

            path = path.TrimStart('/').Replace('/', '\\');
            if (path.Length >= 2 && path[1] == ':')
            {
                path = char.ToUpperInvariant(path[0]) + path[1..];
            }
        }

        return path;
    }

    public static string FromPath(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
