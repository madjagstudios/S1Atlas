using AssetRipper.Primitives;

namespace S1Atlas.NativeRecovery;

/// <summary>
/// Serializes all access to LibCpp2IL's global static state and caches the currently-loaded
/// image, keyed by (gameAssemblySha256, metadataSha256). Re-initializes the global state only
/// when the requested key differs from the one currently loaded.
/// </summary>
public sealed class Il2CppImageCache : IDisposable
{
    /// <summary>
    /// Performs a defensive reset plus initialize of the global IL2CPP state for the given image.
    /// Invoked by the cache only when the requested key differs from the currently-loaded one.
    /// </summary>
    public delegate Task Initializer(
        string gameAssemblySha256, byte[] gameAssemblyBytes,
        string metadataSha256, byte[] metadataBytes,
        UnityVersion unityVersion,
        CancellationToken cancellationToken);

    private readonly Initializer _initializer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImageKey? _currentKey;
    private bool _disposed;

    public Il2CppImageCache(Initializer initializer)
    {
        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
    }

    public async Task<T> WithImageAsync<T>(
        string gameAssemblySha256, byte[] gameAssemblyBytes,
        string metadataSha256, byte[] metadataBytes,
        UnityVersion unityVersion,
        Func<CancellationToken, Task<T>> body,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var requestedKey = new ImageKey(gameAssemblySha256, metadataSha256);
            if (_currentKey != requestedKey)
            {
                await _initializer(
                    gameAssemblySha256, gameAssemblyBytes,
                    metadataSha256, metadataBytes,
                    unityVersion,
                    cancellationToken).ConfigureAwait(false);
                _currentKey = requestedKey;
            }

            return await body(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private readonly record struct ImageKey(string GameAssemblySha256, string MetadataSha256);
}
