using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;

namespace S1Atlas.Core.Storage;

public interface IAtlasRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task SaveSnapshotAsync(
        EnvironmentSnapshot snapshot,
        CancellationToken cancellationToken);

    Task<EnvironmentSnapshot?> GetCurrentSnapshotAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GameBuild>> ListBuildsAsync(
        CancellationToken cancellationToken);
}
