using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Tools;

/// <summary>
/// Read-only managed-tool inspection over the repository tool definitions:
/// the same platform filtering and tool-ID ordering as the install service
/// status query, without persisting verification rows.
/// </summary>
public sealed class ReadOnlyManagedToolStatusReader : IManagedToolStatusReader
{
    private readonly IToolDefinitionProvider _definitionProvider;
    private readonly Func<ResolvedToolDefinition, CancellationToken, Task<ManagedToolStatus>> _inspectAsync;
    private readonly string _platform;

    public ReadOnlyManagedToolStatusReader(
        IToolDefinitionProvider definitionProvider,
        Func<ResolvedToolDefinition, CancellationToken, Task<ManagedToolStatus>> inspectAsync,
        string platform)
    {
        _definitionProvider = definitionProvider ?? throw new ArgumentNullException(nameof(definitionProvider));
        _inspectAsync = inspectAsync ?? throw new ArgumentNullException(nameof(inspectAsync));
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        _platform = platform;
    }

    public async Task<IReadOnlyList<ManagedToolStatus>> GetStatusesAsync(
        CancellationToken cancellationToken)
    {
        var definitions = _definitionProvider.GetAll()
            .Where(definition => string.Equals(
                definition.Definition.Platform,
                _platform,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(
                definition => definition.Definition.ToolId,
                StringComparer.Ordinal)
            .ToArray();

        var statuses = new List<ManagedToolStatus>(definitions.Length);
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            statuses.Add(await _inspectAsync(definition, cancellationToken));
        }

        return statuses;
    }
}
