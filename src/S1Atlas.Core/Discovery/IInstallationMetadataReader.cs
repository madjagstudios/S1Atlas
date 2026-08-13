using S1Atlas.Core.Environment;

namespace S1Atlas.Core.Discovery;

public interface IInstallationMetadataReader
{
    Task<InstallationObservation> ReadAsync(
        ScheduleOneInstallation installation,
        CancellationToken cancellationToken);
}
