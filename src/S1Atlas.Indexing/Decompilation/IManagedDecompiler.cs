using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Decompilation;

public interface IManagedDecompiler
{
    Task<ManagedDecompilation> DecompileAsync(
        string assemblyPath,
        CancellationToken cancellationToken);
}
