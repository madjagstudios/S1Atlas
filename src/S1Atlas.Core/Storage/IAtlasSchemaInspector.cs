namespace S1Atlas.Core.Storage;

public enum AtlasSchemaStatusKind
{
    NotCreated,
    Current,
    Behind,
    Ahead,
    Unrecognized,
    Unreadable
}

public sealed record AtlasSchemaStatus(
    AtlasSchemaStatusKind Kind,
    int? AppliedVersion,
    int ExpectedVersion);

/// <summary>
/// Reads the atlas database schema version without migrating, creating, or
/// otherwise writing the database. A missing database reports
/// <see cref="AtlasSchemaStatusKind.NotCreated"/>; a locked or otherwise
/// unreadable one reports <see cref="AtlasSchemaStatusKind.Unreadable"/>.
/// </summary>
public interface IAtlasSchemaInspector
{
    Task<AtlasSchemaStatus> GetStatusAsync(CancellationToken cancellationToken);
}
