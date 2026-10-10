using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Workflow;

public sealed record GameAssemblyDecompilation(string FileName, ManagedDecompilation Decompilation);

public sealed class ScheduleOneIndexSource
{
    private readonly IManagedDecompiler _decompiler;

    public ScheduleOneIndexSource(IManagedDecompiler decompiler)
    {
        _decompiler = decompiler ?? throw new ArgumentNullException(nameof(decompiler));
    }

    public async Task<IReadOnlyList<GameAssemblyDecompilation>> ReadGameAssembliesAsync(
        PreferredVerifiedExtraction authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var reconstructedRoot = Path.Combine(authority.Extraction.RootPath, "reconstructed");
        var assemblies = new List<GameAssemblyDecompilation>();
        foreach (var assemblyPath in GameAssemblySet.Select(reconstructedRoot))
        {
            assemblies.Add(new GameAssemblyDecompilation(
                Path.GetFileName(assemblyPath),
                await _decompiler.DecompileAsync(assemblyPath, cancellationToken)));
        }
        return assemblies;
    }

    public Task<ManagedDecompilation> ReadInteropAsync(
        string assemblyPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        return _decompiler.DecompileAsync(assemblyPath, cancellationToken);
    }
}
