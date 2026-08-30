using S1Atlas.Core.Storage;

namespace S1Atlas.Storage.Sqlite;

public sealed partial class ReadOnlySqliteAtlasRepository
{
    public Task SaveNativeRecoveryAsync(
        NativeRecoveryRecord record,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("S1Atlas MCP is read-only.");

    public Task<NativeRecoveryRecord?> GetNativeRecoveryAsync(
        string recoveryId,
        CancellationToken cancellationToken) =>
        WithConnectionAsync(
            connection => NativeRecoverySqlite.GetByIdAsync(
                connection,
                recoveryId,
                cancellationToken),
            cancellationToken);

    public Task<IReadOnlyList<NativeRecoveryRecord>> GetNativeRecoveriesAsync(
        NativeRecoveryRequest request,
        CancellationToken cancellationToken) =>
        WithConnectionAsync(
            connection => NativeRecoverySqlite.GetMatchingAsync(
                connection,
                request,
                cancellationToken),
            cancellationToken);
}
