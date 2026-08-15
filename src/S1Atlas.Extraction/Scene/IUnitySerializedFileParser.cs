namespace S1Atlas.Extraction.Scene;

public interface IUnitySerializedFileParser
{
    Task<IReadOnlyList<ParsedSceneContainer>> ParseAsync(
        IReadOnlyList<VerifiedSceneContainer> containers,
        CancellationToken cancellationToken);
}
