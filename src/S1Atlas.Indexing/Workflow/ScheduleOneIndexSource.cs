using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Workflow;

public sealed class ScheduleOneIndexSource
{
    private readonly IManagedDecompiler _decompiler;

    public ScheduleOneIndexSource(IManagedDecompiler decompiler)
    {
        _decompiler = decompiler ?? throw new ArgumentNullException(nameof(decompiler));
    }

    public Task<ManagedDecompilation> ReadAsync(
        PreferredVerifiedExtraction authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var assemblyPath = Path.Combine(authority.Extraction.RootPath, "reconstructed", "Assembly-CSharp.dll");
        return _decompiler.DecompileAsync(assemblyPath, cancellationToken);
    }
}
