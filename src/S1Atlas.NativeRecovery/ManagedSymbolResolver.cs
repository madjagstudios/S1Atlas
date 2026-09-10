namespace S1Atlas.NativeRecovery;

/// <summary>
/// Represents a candidate native method retrieved from the IL2CPP library.
/// </summary>
public sealed record NativeMethodCandidate(
    ulong MethodPointer,
    long MethodOffsetInFile,
    ulong Rva,
    string DeclaringTypeFullName,
    string MethodName,
    IReadOnlyList<string> ParameterTypeFullNames,
    bool IsStatic = false);

/// <summary>
/// Abstraction for looking up native methods by declaring type and method name.
/// Implementations return all candidates matching the criteria, including overloads.
/// </summary>
public interface IIl2CppMethodLookup
{
    /// <summary>
    /// Finds all native methods matching the declaring type full name and method name.
    /// </summary>
    /// <param name="declaringTypeFullName">The full name of the declaring type.</param>
    /// <param name="methodName">The method name.</param>
    /// <returns>A read-only list of matching native method candidates (may be empty).</returns>
    IReadOnlyList<NativeMethodCandidate> FindByTypeAndName(string declaringTypeFullName, string methodName);
}

/// <summary>
/// Enumeration of possible symbol resolution outcomes.
/// </summary>
public enum SymbolResolution
{
    /// <summary>Exactly one matching native method was found.</summary>
    Resolved,

    /// <summary>No matching native methods were found.</summary>
    NotFound,

    /// <summary>Multiple indistinguishable native methods were found (ambiguous).</summary>
    Ambiguous
}

/// <summary>
/// Represents a successfully resolved S1Atlas managed symbol to a native method.
/// </summary>
public sealed record ResolvedNativeSymbol(
    string SymbolId,
    ulong MethodPointer,
    long MethodOffsetInFile,
    ulong Rva,
    string ManagedName,
    bool IsStatic = false);

/// <summary>
/// The result of attempting to resolve an S1Atlas managed symbol to a native method.
/// </summary>
public sealed record SymbolResolutionResult(
    SymbolResolution Kind,
    ResolvedNativeSymbol? Symbol,
    string? Detail);

/// <summary>
/// Resolves S1Atlas managed symbols to native methods using parameter-type matching
/// and overload disambiguation.
/// </summary>
public static class ManagedSymbolResolver
{
    /// <summary>
    /// Attempts to resolve a managed symbol to a native method.
    /// </summary>
    /// <param name="symbolId">The S1Atlas symbol identifier.</param>
    /// <param name="declaringTypeFullName">The full name of the declaring type.</param>
    /// <param name="methodName">The method name.</param>
    /// <param name="parameterTypeFullNames">The parameter type full names.</param>
    /// <param name="lookup">The IL2CPP method lookup service.</param>
    /// <returns>
    /// A resolution result indicating success (Resolved), ambiguity (Ambiguous),
    /// or failure (NotFound).
    /// </returns>
    public static SymbolResolutionResult Resolve(
        string symbolId,
        string declaringTypeFullName,
        string methodName,
        IReadOnlyList<string> parameterTypeFullNames,
        IIl2CppMethodLookup lookup)
    {
        // Get candidate methods from the lookup
        var candidates = lookup.FindByTypeAndName(declaringTypeFullName, methodName);

        // Filter candidates by parameter types
        var matchedCandidates = candidates
            .Where(c => ParameterTypesMatch(c.ParameterTypeFullNames, parameterTypeFullNames))
            .ToList();

        // Determine resolution outcome based on matched candidate count
        return matchedCandidates.Count switch
        {
            0 => new SymbolResolutionResult(SymbolResolution.NotFound, null, "No candidate matched the parameter types."),
            1 => ResolveToNativeMethod(symbolId, matchedCandidates[0]),
            _ => new SymbolResolutionResult(
                SymbolResolution.Ambiguous,
                null,
                $"{matchedCandidates.Count} candidates matched the parameter types.")
        };
    }

    /// <summary>
    /// Determines if two parameter type lists match after normalization.
    /// Normalization includes: trimming whitespace and replacing IL2CPP nested-type
    /// separators (/ and +) with dots.
    /// </summary>
    private static bool ParameterTypesMatch(
        IReadOnlyList<string> candidateParameterTypes,
        IReadOnlyList<string> requestedParameterTypes)
    {
        if (candidateParameterTypes.Count != requestedParameterTypes.Count)
        {
            return false;
        }

        for (int i = 0; i < candidateParameterTypes.Count; i++)
        {
            var normalizedCandidate = NormalizeParameterType(candidateParameterTypes[i]);
            var normalizedRequested = NormalizeParameterType(requestedParameterTypes[i]);

            if (normalizedCandidate != normalizedRequested)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Normalizes a parameter type name by trimming and replacing nested-type separators.
    /// </summary>
    private static string NormalizeParameterType(string parameterType)
    {
        // Trim whitespace
        var trimmed = parameterType.Trim();

        // Replace IL2CPP nested-type separators with dots
        var normalized = trimmed
            .Replace('/', '.')
            .Replace('+', '.');

        return normalized;
    }

    /// <summary>
    /// Creates a resolved symbol from a matched candidate.
    /// </summary>
    private static SymbolResolutionResult ResolveToNativeMethod(
        string symbolId,
        NativeMethodCandidate candidate)
    {
        var managedName = NativeNameNormalizer.ManagedName(
            candidate.DeclaringTypeFullName,
            candidate.MethodName);

        var resolved = new ResolvedNativeSymbol(
            symbolId,
            candidate.MethodPointer,
            candidate.MethodOffsetInFile,
            candidate.Rva,
            managedName,
            candidate.IsStatic);

        return new SymbolResolutionResult(SymbolResolution.Resolved, resolved, null);
    }
}
