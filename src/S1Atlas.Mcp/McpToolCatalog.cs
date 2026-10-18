using System.Reflection;
using ModelContextProtocol.Server;

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

    // Tool name -> argument name -> enum type for every enum-typed (or
    // nullable-enum) tool parameter. The SDK uses the C# parameter name as the
    // JSON argument name, so the keys match request arguments directly.
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, Type>> DiscoverEnumParameters() =>
        typeof(McpToolCatalog).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<McpServerToolAttribute>(inherit: false)))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Attribute?.Name))
            .ToDictionary(
                entry => entry.Attribute!.Name!,
                entry => (IReadOnlyDictionary<string, Type>)entry.Method.GetParameters()
                    .Select(parameter => (parameter.Name, Type: Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType))
                    .Where(parameter => parameter.Name is not null && parameter.Type.IsEnum)
                    .ToDictionary(parameter => parameter.Name!, parameter => parameter.Type, StringComparer.Ordinal),
                StringComparer.Ordinal);
}
