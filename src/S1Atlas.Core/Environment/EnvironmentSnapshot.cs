using S1Atlas.Core.Builds;

namespace S1Atlas.Core.Environment;

public sealed record EnvironmentSnapshot(
    GameBuild Build,
    IReadOnlyList<DependencyVersion> Dependencies,
    string AtlasVersion,
    DateTimeOffset CapturedAtUtc);
