namespace S1Atlas.Extraction.Scene;

public interface IUnitySerializedFileParser
{
    /// <summary>
    /// The pinned class database this parser can fall back to for containers with stripped
    /// type trees, or null when none is configured. Part of the scene snapshot identity.
    /// </summary>
    UnityClassDatabaseDescriptor? ClassDatabase => null;

    Task<IReadOnlyList<ParsedSceneContainer>> ParseAsync(
        IReadOnlyList<VerifiedSceneContainer> containers,
        CancellationToken cancellationToken);
}
