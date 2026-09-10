using AssetRipper.Primitives;

namespace S1Atlas.NativeRecovery;

/// <summary>
/// The production <see cref="Il2CppImageCache.Initializer"/>: resets and re-initializes
/// <see cref="LibCpp2IL.LibCpp2IlMain"/>'s global state for the requested image. Mirrors exactly
/// what the LocalGameRequired test fixtures do, but as reusable production code rather than a
/// test-only inline lambda.
/// </summary>
public static class LibCpp2IlImageInitializer
{
    public static Task InitializeAsync(
        string gameAssemblySha256, byte[] gameAssemblyBytes,
        string metadataSha256, byte[] metadataBytes,
        UnityVersion unityVersion,
        CancellationToken cancellationToken)
    {
        LibCpp2IL.LibCpp2IlMain.Reset();
        var initialized = LibCpp2IL.LibCpp2IlMain.Initialize(gameAssemblyBytes, metadataBytes, unityVersion);
        if (!initialized)
        {
            throw new InvalidOperationException(
                "LibCpp2IlMain.Initialize failed to initialize the IL2CPP image.");
        }

        return Task.CompletedTask;
    }
}
