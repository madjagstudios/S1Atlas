using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.NativeRecovery;

/// <summary>
/// The real, index-repository-backed <see cref="ISymbolIdentityResolver"/>: resolves each
/// requested symbol id to its managed identity by loading the completed
/// <see cref="IndexSymbolRecord"/> and parsing its <c>Signature</c> with
/// <see cref="CanonicalSignatureParser"/>. Read-only: only ever reads from the index repository.
/// </summary>
public sealed class IndexSymbolIdentityResolver(IIndexRepository indexRepository) : ISymbolIdentityResolver
{
    private readonly IIndexRepository _indexRepository =
        indexRepository ?? throw new ArgumentNullException(nameof(indexRepository));

    public async Task<IReadOnlyDictionary<string, ManagedSymbolDescriptor?>> ResolveAsync(
        string indexId, IReadOnlyList<string> symbolIds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentNullException.ThrowIfNull(symbolIds);

        var result = new Dictionary<string, ManagedSymbolDescriptor?>(StringComparer.Ordinal);
        foreach (var symbolId in symbolIds)
        {
            var record = await _indexRepository
                .GetCompletedSymbolByIdAsync(indexId, symbolId, cancellationToken)
                .ConfigureAwait(false);
            result[symbolId] = ToDescriptor(record);
        }

        return result;
    }

    private static ManagedSymbolDescriptor? ToDescriptor(IndexSymbolRecord? record)
    {
        if (record is null ||
            !string.Equals(record.Kind, nameof(SymbolKind.Method), StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var (declaringTypeFullName, methodName, parameterTypeFullNames) =
                CanonicalSignatureParser.ParseMethod(record.Signature);
            return new ManagedSymbolDescriptor(declaringTypeFullName, methodName, parameterTypeFullNames);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
