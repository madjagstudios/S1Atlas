using S1Atlas.Core.Tools;

namespace S1Atlas.Core.Storage;

public interface IToolRepository
{
    Task SaveVerifiedManagedToolAsync(
        ManagedToolInstallation installation,
        ToolInstance toolInstance,
        CancellationToken cancellationToken);

    Task<ManagedToolInstallation?> GetManagedToolAsync(
        string toolId,
        string version,
        string platform,
        CancellationToken cancellationToken);

    Task<ToolInstance?> GetToolInstanceAsync(
        string toolInstanceId,
        CancellationToken cancellationToken);
}
