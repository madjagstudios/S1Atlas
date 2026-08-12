namespace S1Atlas.Core.Discovery;

public interface IScheduleOneLocator
{
    Task<ScheduleOneInstallation?> LocateAsync(
        string? overridePath,
        CancellationToken cancellationToken);
}
