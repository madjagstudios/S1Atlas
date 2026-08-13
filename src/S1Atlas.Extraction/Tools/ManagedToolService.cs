using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Tools;

public sealed class ManagedToolService
{
    private readonly IToolDefinitionProvider _definitionProvider;
    private readonly ManagedToolInstallationValidator _validator;
    private readonly IToolInstaller _installer;
    private readonly IToolRepository _repository;
    private readonly string _platform;

    internal ManagedToolService(
        IToolDefinitionProvider definitionProvider,
        ManagedToolInstallationValidator validator,
        IToolInstaller installer,
        IToolRepository repository,
        string platform,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(definitionProvider);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        _definitionProvider = definitionProvider;
        _validator = validator;
        _installer = installer;
        _repository = repository;
        _platform = platform;
        _ = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<ManagedToolStatus>> GetStatusesAsync(
        string? toolId,
        CancellationToken cancellationToken)
    {
        var definitions = toolId is null
            ? _definitionProvider.GetAll()
                .Where(definition => string.Equals(
                    definition.Definition.Platform,
                    _platform,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(
                    definition => definition.Definition.ToolId,
                    StringComparer.Ordinal)
                .ToArray()
            : [_definitionProvider.GetRequired(toolId, _platform)];

        var statuses = new List<ManagedToolStatus>(definitions.Length);
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await _validator.InspectAsync(
                definition,
                cancellationToken);
            if (status.Status == ToolInstallationStatus.Verified &&
                status.Installation is not null)
            {
                var toolInstance = ManagedToolInstanceFactory.Create(
                    definition,
                    status.Installation);
                await _repository.SaveVerifiedManagedToolAsync(
                    status.Installation,
                    toolInstance,
                    cancellationToken);
            }

            statuses.Add(status);
        }

        return statuses;
    }

    public async Task<ToolInstallResult> InstallAsync(
        string toolId,
        bool repair,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        var definition = _definitionProvider.GetRequired(toolId, _platform);
        var outcome = await _installer.InstallAsync(
            definition,
            repair,
            cancellationToken);
        if (outcome.Installation.Status != ToolInstallationStatus.Verified)
        {
            throw new ToolOperationException(
                "ToolInstallationFailed",
                "The managed tool installer returned an unverified installation.");
        }

        var toolInstance = ManagedToolInstanceFactory.Create(
            definition,
            outcome.Installation);
        await _repository.SaveVerifiedManagedToolAsync(
            outcome.Installation,
            toolInstance,
            cancellationToken);

        return new ToolInstallResult(
            outcome.Installation,
            toolInstance,
            outcome.WasAlreadyVerified,
            outcome.Repaired,
            outcome.QuarantinePath);
    }
}
