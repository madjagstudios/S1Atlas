namespace S1Atlas.Mcp;

public static class McpToolCatalog
{
    public static IReadOnlyList<string> DiscoverToolNames()
    {
        var assembly = typeof(McpToolCatalog).Assembly;
        return assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic))
            .Where(method => method
                .GetCustomAttributes(inherit: false)
                .Any(attribute => string.Equals(
                    attribute.GetType().Name,
                    "McpServerToolAttribute",
                    StringComparison.Ordinal)))
            .Select(method => method
                .GetCustomAttributes(inherit: false)
                .First(attribute => string.Equals(
                    attribute.GetType().Name,
                    "McpServerToolAttribute",
                    StringComparison.Ordinal)))
            .Select(attribute =>
                attribute.GetType().GetProperty("Name")?.GetValue(attribute) as string)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }
}
