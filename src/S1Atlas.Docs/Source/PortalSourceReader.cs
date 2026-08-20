using S1Atlas.Core.Indexing;
using S1Atlas.Docs.Generation;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Docs.Source;

public sealed class PortalSourceReader
{
    private readonly IndexQueryService _query;

    public PortalSourceReader(IndexQueryService query) => _query = query ?? throw new ArgumentNullException(nameof(query));

    public async Task<PortalSourceResult> ReadAsync(
        PortalIndexModel index,
        PortalSymbolModel symbol,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _query.SourceInIndexAsync(
                index.Run,
                index.Codebase,
                index.Channel,
                symbol.SymbolId,
                context: 3,
                cancellationToken);
            if (result.Snippet is not null)
                return new PortalSourceResult(PortalSourceState.Available, result.Snippet, "source available");
            return result.Resolution.Status == SymbolResolutionStatus.Resolved
                ? new PortalSourceResult(PortalSourceState.NoIndexedLocation, null, "source not indexed")
                : new PortalSourceResult(PortalSourceState.Unavailable, null, "source unavailable");
        }
        catch (InvalidDataException)
        {
            return new PortalSourceResult(PortalSourceState.IntegrityFailure, null, "source unavailable (integrity)");
        }
        catch (FileNotFoundException)
        {
            return new PortalSourceResult(PortalSourceState.Unavailable, null, "source unavailable");
        }
    }
}
