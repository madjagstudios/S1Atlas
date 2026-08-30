using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.NativeRecovery;

public interface INativeBodyRecoveryProvider
{
    Task<NativeRecoveryRecord> RecoverAsync(
        NativeRecoveryRequest request,
        CancellationToken cancellationToken);
}
