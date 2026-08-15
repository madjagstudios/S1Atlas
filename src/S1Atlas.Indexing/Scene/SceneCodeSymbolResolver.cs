using S1Atlas.Core.Indexing;
using S1Atlas.Core.Scenes;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Scene;

namespace S1Atlas.Indexing.Scene;

public sealed record SceneCodeBuildAuthority(string ExtractionId, string BuildId);

public sealed record SceneCodeSymbolResolution(
    string? RawAssemblyName,
    string? RawNamespace,
    string? RawClassName,
    string? NormalizedQualifiedName,
    string? SymbolId,
    string? CodeIndexId,
    SceneResolutionStatus Status);

public sealed class SceneCodeSymbolResolver
{
    private const string ScheduleOneAssemblyName = "Assembly-CSharp";
    private readonly IIndexRepository _indexRepository;
    private readonly Func<string, CancellationToken, Task<SceneCodeBuildAuthority?>> _authorityResolver;

    public SceneCodeSymbolResolver(IIndexRepository indexRepository)
        : this(indexRepository, CreateAuthorityResolver(indexRepository))
    {
    }

    public SceneCodeSymbolResolver(
        IIndexRepository indexRepository,
        Func<string, CancellationToken, Task<SceneCodeBuildAuthority?>> authorityResolver)
    {
        _indexRepository = indexRepository ?? throw new ArgumentNullException(nameof(indexRepository));
        _authorityResolver = authorityResolver ?? throw new ArgumentNullException(nameof(authorityResolver));
    }

    public async Task<SceneCodeSymbolResolution> ResolveAsync(
        string expectedBuildId,
        string expectedValidatedExtractionId,
        string codeIndexId,
        ParsedMonoScriptData script,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedBuildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedValidatedExtractionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeIndexId);
        ArgumentNullException.ThrowIfNull(script);
        cancellationToken.ThrowIfCancellationRequested();

        var raw = new SceneCodeSymbolResolution(
            script.AssemblyName,
            script.Namespace,
            script.ClassName,
            null,
            null,
            null,
            SceneResolutionStatus.UnresolvedText);

        if (!IsScheduleOneAssembly(script.AssemblyName) ||
            string.IsNullOrWhiteSpace(script.Namespace) ||
            string.IsNullOrWhiteSpace(script.ClassName))
        {
            return raw;
        }

        var completedIndex = await _indexRepository.GetCompletedIndexAsync(codeIndexId, cancellationToken);
        if (completedIndex is null)
            return raw with { Status = SceneResolutionStatus.NotIndexed };

        var codeSnapshot = await _indexRepository.GetCodeSnapshotAsync(completedIndex.SnapshotId, cancellationToken);
        if (codeSnapshot is null ||
            codeSnapshot.Codebase != CodebaseKind.ScheduleI ||
            codeSnapshot.Channel != CodeChannel.Installed ||
            !string.Equals(codeSnapshot.SourceIdentity, expectedValidatedExtractionId, StringComparison.Ordinal))
        {
            return raw with { Status = SceneResolutionStatus.Unavailable };
        }

        var buildAuthority = await _authorityResolver(codeSnapshot.SourceIdentity, cancellationToken);
        if (buildAuthority is null ||
            !string.Equals(buildAuthority.ExtractionId, expectedValidatedExtractionId, StringComparison.Ordinal) ||
            !string.Equals(buildAuthority.BuildId, expectedBuildId, StringComparison.Ordinal))
        {
            return raw with { Status = SceneResolutionStatus.Unavailable };
        }

        var normalizedQualifiedName = CanonicalSignatureRenderer.RenderType(
            script.Namespace.Trim() + "." + script.ClassName.Trim());
        var typeIdentity = SymbolIdentity.Create(
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            SymbolKind.Type,
            normalizedQualifiedName);
        var symbol = await _indexRepository.GetCompletedSymbolByCanonicalKeyAsync(
            codeIndexId,
            typeIdentity.CanonicalKey,
            cancellationToken);

        var exact = symbol
            .Where(candidate =>
                string.Equals(candidate.SnapshotId, codeSnapshot.SnapshotId, StringComparison.Ordinal) &&
                string.Equals(candidate.CanonicalKey, typeIdentity.CanonicalKey, StringComparison.Ordinal) &&
                string.Equals(candidate.Kind, SymbolKind.Type.ToString(), StringComparison.Ordinal))
            .ToArray();
        return exact.Length switch
        {
            0 => raw with
            {
                NormalizedQualifiedName = normalizedQualifiedName,
                Status = SceneResolutionStatus.NotIndexed
            },
            1 => raw with
            {
                NormalizedQualifiedName = normalizedQualifiedName,
                SymbolId = exact[0].SymbolId,
                CodeIndexId = codeIndexId,
                Status = SceneResolutionStatus.Resolved
            },
            _ => raw with
            {
                NormalizedQualifiedName = normalizedQualifiedName,
                Status = SceneResolutionStatus.Ambiguous
            }
        };
    }

    private static bool IsScheduleOneAssembly(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var trimmed = value.Trim();
        if (trimmed.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];
        return string.Equals(trimmed, ScheduleOneAssemblyName, StringComparison.OrdinalIgnoreCase);
    }

    private static Func<string, CancellationToken, Task<SceneCodeBuildAuthority?>> CreateAuthorityResolver(
        IIndexRepository indexRepository)
    {
        if (indexRepository is not IValidatedExtractionRepository extractionRepository)
            return (_, _) => Task.FromResult<SceneCodeBuildAuthority?>(null);

        return async (extractionId, cancellationToken) =>
        {
            var extraction = await extractionRepository.GetValidatedExtractionAsync(extractionId, cancellationToken);
            return extraction is null
                ? null
                : new SceneCodeBuildAuthority(extraction.ExtractionId, extraction.BuildId);
        };
    }
}
