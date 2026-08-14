using System.Text;

namespace S1Atlas.Core.Indexing;

public static class CanonicalSignatureRenderer
{
    private static readonly IReadOnlySet<string> KnownValueTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Boolean", "System.Byte", "System.SByte", "System.Char", "System.Decimal",
        "System.Double", "System.Int16", "System.Int32", "System.Int64", "System.IntPtr",
        "System.UInt16", "System.UInt32", "System.UInt64", "System.UIntPtr", "System.Single",
        "System.DateTime", "System.DateTimeOffset", "System.Guid", "System.TimeSpan"
    };

    private static readonly IReadOnlyDictionary<string, string> PrimitiveAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bool"] = "System.Boolean",
            ["byte"] = "System.Byte",
            ["sbyte"] = "System.SByte",
            ["short"] = "System.Int16",
            ["ushort"] = "System.UInt16",
            ["int"] = "System.Int32",
            ["uint"] = "System.UInt32",
            ["long"] = "System.Int64",
            ["ulong"] = "System.UInt64",
            ["nint"] = "System.IntPtr",
            ["nuint"] = "System.UIntPtr",
            ["char"] = "System.Char",
            ["float"] = "System.Single",
            ["double"] = "System.Double",
            ["decimal"] = "System.Decimal",
            ["string"] = "System.String",
            ["object"] = "System.Object",
            ["void"] = "System.Void"
        };

    public static string RenderType(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var value = text.Trim().Replace("global::", string.Empty, StringComparison.Ordinal);
        var refSuffix = string.Empty;
        foreach (var modifier in new[] { "ref", "in", "out" })
        {
            if (value.StartsWith(modifier + " ", StringComparison.Ordinal) ||
                value.StartsWith(modifier + "\t", StringComparison.Ordinal))
            {
                value = value[(modifier.Length + 1)..];
                refSuffix = "&";
                break;
            }
        }
        value = RemoveWhitespace(value);

        var pointerDepth = 0;
        while (value.EndsWith('*'))
        {
            pointerDepth++;
            value = value[..^1];
        }

        var arraySuffix = new StringBuilder();
        while (value.EndsWith(']'))
        {
            var open = value.LastIndexOf('[');
            if (open < 0) break;
            var rank = value[(open + 1)..^1];
            arraySuffix.Append('[').Append(rank).Append(']');
            value = value[..open];
        }

        var nullable = value.EndsWith('?');
        if (nullable) value = value[..^1];

        var canonical = RenderNamedType(value);
        if (nullable && KnownValueTypes.Contains(canonical))
            canonical = "System.Nullable`1<" + canonical + ">";
        else if (nullable)
            canonical += "?";

        canonical += arraySuffix;
        canonical += new string('*', pointerDepth);
        return canonical + refSuffix;
    }

    public static string RenderMethod(
        string containingType,
        string name,
        string returnType,
        IReadOnlyList<string> parameterTypes,
        int genericArity = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containingType);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var generic = genericArity == 0 ? string.Empty : "`" + genericArity;
        var parameters = string.Join(",", parameterTypes.Select(RenderType));
        return $"{RenderType(containingType)}::{name}{generic}({parameters}):{RenderType(returnType)}";
    }

    private static string RenderNamedType(string value)
    {
        if (value.StartsWith('(') && value.EndsWith(')'))
        {
            var parts = SplitTopLevel(value[1..^1], ',');
            return "System.ValueTuple<" + string.Join(',', parts.Select(RenderType)) + ">";
        }

        var genericStart = value.IndexOf('<');
        if (genericStart >= 0 && value.EndsWith('>'))
        {
            var name = value[..genericStart];
            var arguments = value[(genericStart + 1)..^1];
            var parts = SplitTopLevel(arguments, ',');
            name = NormalizeName(name);
            if (!name.Contains('`')) name += "`" + parts.Count;
            return name + "<" + string.Join(',', parts.Select(RenderType)) + ">";
        }

        return NormalizeName(value);
    }

    private static string NormalizeName(string value) =>
        PrimitiveAliases.TryGetValue(value, out var alias) ? alias : value.Replace('/', '+');

    private static string RemoveWhitespace(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    private static List<string> SplitTopLevel(string value, char separator)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            depth += value[index] switch { '<' or '(' or '[' => 1, '>' or ')' or ']' => -1, _ => 0 };
            if (value[index] == separator && depth == 0)
            {
                result.Add(value[start..index]);
                start = index + 1;
            }
        }
        result.Add(value[start..]);
        return result;
    }
}
