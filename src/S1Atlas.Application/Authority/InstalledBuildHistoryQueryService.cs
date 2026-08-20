using S1Atlas.Core.Builds;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Application.Authority;

public enum InstalledBuildHistoryStatus
{
    IndexedVerified,
    NotIndexed,
    IntegrityFailed
}

public sealed record InstalledBuildHistoryEntry(
    GameBuild Build,
    InstalledBuildHistoryStatus Status,
    InstalledBuildAuthority? Authority,
    string? Message)
{
    public bool IsNavigable => Status == InstalledBuildHistoryStatus.IndexedVerified;
}

public sealed record AdjacentBuildPair(
    InstalledBuildHistoryEntry Before,
    InstalledBuildHistoryEntry After);

public sealed record InstalledBuildHistoryResult(
    IReadOnlyList<InstalledBuildHistoryEntry> Entries,
    IReadOnlyList<InstalledBuildHistoryEntry> NavigableEntries,
    IReadOnlyList<AdjacentBuildPair> AdjacentPairs);

public sealed record SymbolHistoryOccurrence(
    string BuildId,
    string IndexId,
    bool Present,
    string? SymbolId,
    string? QualifiedName,
    string? Signature);

public sealed class InstalledBuildHistoryQueryService
{
    private readonly IAtlasRepository _atlas;
    private readonly IndexQueryService _query;
    private readonly Func<string?, CancellationToken, Task<InstalledBuildAuthority>> _resolve;

    public InstalledBuildHistoryQueryService(
        IAtlasRepository atlas,
        IIndexRepository index,
        IndexQueryService query,
        InstalledBuildAuthorityResolver resolver)
        : this(atlas, index, query, resolver.ResolveAsync)
    {
    }

    public InstalledBuildHistoryQueryService(
        IAtlasRepository atlas,
        IIndexRepository index,
        IndexQueryService query,
        Func<string?, CancellationToken, Task<InstalledBuildAuthority>> resolve)
    {
        _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
        _ = index ?? throw new ArgumentNullException(nameof(index));
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    public async Task<InstalledBuildHistoryResult> GetHistoryAsync(CancellationToken cancellationToken)
    {
        var builds = (await _atlas.ListBuildsAsync(cancellationToken))
            .OrderBy(build => build.FirstSeenAtUtc)
            .ThenBy(build => build.BuildId, StringComparer.Ordinal)
            .ToArray();
        var entries = new List<InstalledBuildHistoryEntry>(builds.Length);
        foreach (var build in builds)
        {
            var authority = await _resolve(build.BuildId, cancellationToken);
            entries.Add(new InstalledBuildHistoryEntry(
                build,
                ToStatus(authority),
                authority,
                authority.Message));
        }

        var navigable = entries.Where(entry => entry.IsNavigable).ToArray();
        var adjacent = navigable
            .Zip(navigable.Skip(1), (before, after) => new AdjacentBuildPair(before, after))
            .ToArray();
        return new InstalledBuildHistoryResult(entries, navigable, adjacent);
    }

    public async Task<IReadOnlyList<SymbolHistoryOccurrence>> GetSymbolOccurrencesAsync(
        string canonicalKey,
        IReadOnlyList<InstalledBuildHistoryEntry> entries,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalKey);
        ArgumentNullException.ThrowIfNull(entries);
        var result = new List<SymbolHistoryOccurrence>();
        foreach (var entry in entries
                     .Where(entry => entry.IsNavigable && entry.Authority?.IndexRun is not null)
                     .OrderBy(entry => entry.Build.FirstSeenAtUtc)
                     .ThenBy(entry => entry.Build.BuildId, StringComparer.Ordinal))
        {
            var authority = entry.Authority!;
            var run = authority.IndexRun!;
            var symbols = await _query.GetCanonicalSymbolsInIndexAsync(
                run,
                CodebaseKind.ScheduleI,
                CodeChannel.Installed,
                canonicalKey,
                cancellationToken);
            var symbol = symbols.FirstOrDefault();
            result.Add(new SymbolHistoryOccurrence(
                entry.Build.BuildId,
                run.IndexId,
                symbol is not null,
                symbol?.SymbolId,
                symbol?.QualifiedName,
                symbol?.Signature));
        }
        return result;
    }

    private static InstalledBuildHistoryStatus ToStatus(InstalledBuildAuthority authority) =>
        authority.Status switch
        {
            InstalledBuildAuthorityStatus.Resolved when authority.IndexRun is not null && authority.IndexId is not null
                => InstalledBuildHistoryStatus.IndexedVerified,
            InstalledBuildAuthorityStatus.ExtractionIntegrityFailure or InstalledBuildAuthorityStatus.IndexBuildMismatch
                => InstalledBuildHistoryStatus.IntegrityFailed,
            _ => InstalledBuildHistoryStatus.NotIndexed
        };
}
