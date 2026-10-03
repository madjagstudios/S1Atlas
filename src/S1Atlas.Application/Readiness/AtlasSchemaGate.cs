using S1Atlas.Core.Storage;

namespace S1Atlas.Application.Readiness;

// Guards the read-only hosts against an unusable atlas schema. Read-only:
// it only inspects, never migrates or writes. A Current status is cached
// for the life of the process (the code cannot go backwards); any other
// status is re-checked on every call so upgrading in another terminal
// recovers without a restart.
public sealed class AtlasSchemaGate
{
    private readonly IAtlasSchemaInspector _schemaInspector;
    private volatile AtlasSchemaStatus? _current;

    public AtlasSchemaGate(IAtlasSchemaInspector schemaInspector)
    {
        _schemaInspector = schemaInspector ?? throw new ArgumentNullException(nameof(schemaInspector));
    }

    public async Task<AtlasSchemaStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (_current is { } cached)
        {
            return cached;
        }

        var status = await _schemaInspector.GetStatusAsync(cancellationToken);
        if (status.Kind == AtlasSchemaStatusKind.Current)
        {
            _current = status;
        }

        return status;
    }
}
