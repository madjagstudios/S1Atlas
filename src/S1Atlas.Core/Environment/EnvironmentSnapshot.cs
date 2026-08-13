using S1Atlas.Core.Builds;

namespace S1Atlas.Core.Environment;

public sealed record EnvironmentSnapshot(
    int IdentityVersion,
    GameBuild Build,
    InstallationObservation Installation,
    IReadOnlyList<DependencyVersion> Dependencies,
    string AtlasVersion,
    DateTimeOffset CapturedAtUtc);
