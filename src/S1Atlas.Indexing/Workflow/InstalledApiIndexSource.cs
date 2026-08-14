using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Decompilation;

namespace S1Atlas.Indexing.Workflow;

public sealed class InstalledApiIndexSource
{
    private readonly IManagedDecompiler _decompiler;

    public InstalledApiIndexSource(IManagedDecompiler decompiler)
    {
        _decompiler = decompiler ?? throw new ArgumentNullException(nameof(decompiler));
    }

    public Task<ManagedDecompilation?> ReadAsync(DependencyVersion dependency, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        if (!dependency.IsInstalled || string.IsNullOrWhiteSpace(dependency.Path))
            return Task.FromResult<ManagedDecompilation?>(null);
        if (dependency.Kind is not (DependencyKind.S1Api or DependencyKind.S1Mapi))
            throw new ArgumentException("The dependency must be S1API or S1MAPI.", nameof(dependency));
        return ReadCoreAsync(dependency.Path, cancellationToken);
    }

    private async Task<ManagedDecompilation?> ReadCoreAsync(string path, CancellationToken cancellationToken) =>
        await _decompiler.DecompileAsync(path, cancellationToken);
}
