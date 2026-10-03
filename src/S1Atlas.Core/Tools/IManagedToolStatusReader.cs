namespace S1Atlas.Core.Tools;

/// <summary>
/// Read-only managed-tool inspection. Unlike the install service status
/// query, implementations must not persist verification rows: readiness
/// probes never write to the atlas.
/// </summary>
public interface IManagedToolStatusReader
{
    Task<IReadOnlyList<ManagedToolStatus>> GetStatusesAsync(
        CancellationToken cancellationToken);
}
