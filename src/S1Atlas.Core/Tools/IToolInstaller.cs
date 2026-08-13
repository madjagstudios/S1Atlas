namespace S1Atlas.Core.Tools;

public interface IToolInstaller
{
    Task<ManagedToolInstallOutcome> InstallAsync(
        ResolvedToolDefinition definition,
        bool repair,
        CancellationToken cancellationToken);
}
