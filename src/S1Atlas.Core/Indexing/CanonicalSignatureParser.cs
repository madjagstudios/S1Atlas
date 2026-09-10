namespace S1Atlas.Core.Indexing;

/// <summary>
/// Reverses <see cref="CanonicalSignatureRenderer.RenderMethod"/>: parses a method
/// <c>Signature</c> string back into its declaring type, method name, and parameter types.
/// The return type suffix is intentionally discarded — callers of this parser (symbol identity
/// resolution) only ever need the declaring type, method name, and parameter types to disambiguate
/// overloads.
/// </summary>
public static class CanonicalSignatureParser
{
    /// <summary>
    /// Parses a method signature of the form
    /// <c>{declaringType}::{name}[`arity](param1,param2,...):{returnType}</c> (see
    /// <see cref="CanonicalSignatureRenderer.RenderMethod"/>) into its declaring type, method
    /// name, and parameter types. The generic-arity suffix on the method name (if any) and the
    /// trailing return type are both stripped.
    /// </summary>
    /// <exception cref="FormatException">The signature is not well-formed.</exception>
    public static (string DeclaringTypeFullName, string MethodName, IReadOnlyList<string> ParameterTypeFullNames)
        ParseMethod(string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);

        var typeSeparator = signature.IndexOf("::", StringComparison.Ordinal);
        if (typeSeparator < 0)
        {
            throw new FormatException(
                $"Signature '{signature}' is missing the '::' declaring-type separator.");
        }

        var declaringTypeFullName = signature[..typeSeparator];
        var remainder = signature[(typeSeparator + 2)..];

        var openParen = remainder.IndexOf('(');
        if (openParen < 0)
        {
            throw new FormatException($"Signature '{signature}' is missing the parameter list.");
        }

        var closeParen = remainder.LastIndexOf(')');
        if (closeParen < openParen)
        {
            throw new FormatException($"Signature '{signature}' has an unmatched parameter list.");
        }

        var methodName = StripGenericArity(remainder[..openParen]);
        var parametersText = remainder[(openParen + 1)..closeParen];
        var parameterTypeFullNames = parametersText.Length == 0
            ? []
            : SplitTopLevel(parametersText);

        return (declaringTypeFullName, methodName, parameterTypeFullNames);
    }

    /// <summary>
    /// Strips a trailing generic-arity marker (e.g. <c>`2</c>) that
    /// <see cref="CanonicalSignatureRenderer.RenderMethod"/> appends to a generic method's name.
    /// </summary>
    private static string StripGenericArity(string nameAndGeneric)
    {
        var backtick = nameAndGeneric.LastIndexOf('`');
        if (backtick < 0)
        {
            return nameAndGeneric;
        }

        var suffix = nameAndGeneric[(backtick + 1)..];
        return suffix.Length > 0 && suffix.All(char.IsAsciiDigit)
            ? nameAndGeneric[..backtick]
            : nameAndGeneric;
    }

    /// <summary>
    /// Splits a comma-separated parameter list at top level only, respecting nesting depth for
    /// <c>&lt;&gt;</c> (generic arguments), <c>[]</c> (arrays), and <c>()</c> (tuples), so a
    /// parameter type's own commas (e.g. inside a generic argument list) are not mistaken for
    /// parameter separators. Mirrors <c>CanonicalSignatureRenderer.SplitTopLevel</c>.
    /// </summary>
    private static List<string> SplitTopLevel(string value)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            depth += value[index] switch { '<' or '(' or '[' => 1, '>' or ')' or ']' => -1, _ => 0 };
            if (value[index] == ',' && depth == 0)
            {
                result.Add(value[start..index]);
                start = index + 1;
            }
        }

        result.Add(value[start..]);
        return result;
    }
}
