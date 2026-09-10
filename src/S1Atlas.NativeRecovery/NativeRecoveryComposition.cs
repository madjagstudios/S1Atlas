using AssetRipper.Primitives;
using S1Atlas.Core.Discovery;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.NativeRecovery;

namespace S1Atlas.NativeRecovery;

/// <summary>
/// Composes the real, index-backed native-recovery pieces (<see cref="IndexSymbolIdentityResolver"/>,
/// <see cref="Il2CppImageCache"/>, <see cref="ScheduleOneNativeImageSource"/>,
/// <see cref="LibCpp2IlAdapterFactory"/>) into a ready-to-use <see cref="NativeRecoveryWorkflow"/>.
/// A thin holder: it defers all I/O (locating the install, reading the library pins) to its async
/// methods, so constructing it is cheap and side-effect free. Consumed by the (not-yet-added)
/// <c>recover-native-body</c> command, alongside the separately-composed
/// <see cref="NativeRecoveryExecutionContextFactory"/>.
/// </summary>
public sealed class NativeRecoveryComposition
{
    /// <summary>
    /// The Unity version LibCpp2IL is pinned against for native recovery — matches the scene
    /// indexing gate's supported version.
    /// </summary>
    public const string SupportedUnityVersion = "2022.3.62f2";

    private readonly IIndexRepository _indexRepository;
    private readonly IScheduleOneLocator _locator;
    private readonly string _librariesConfigPath;

    public NativeRecoveryComposition(
        IIndexRepository indexRepository,
        IScheduleOneLocator locator,
        string librariesConfigPath)
    {
        _indexRepository = indexRepository ?? throw new ArgumentNullException(nameof(indexRepository));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        ArgumentException.ThrowIfNullOrWhiteSpace(librariesConfigPath);
        _librariesConfigPath = librariesConfigPath;
    }

    /// <summary>
    /// Reads the pinned-library descriptors from <c>config/native-recovery/libraries.json</c>.
    /// </summary>
    public IReadOnlyList<LibraryPin> LoadLibraryPins() =>
        LibCpp2IlNativeBodyRecoveryProvider.LoadLibraryPinsFromConfig(_librariesConfigPath);

    /// <summary>
    /// Locates the installed Schedule I build, or <c>null</c> if none is found.
    /// </summary>
    public Task<ScheduleOneInstallation?> LocateInstallationAsync(CancellationToken cancellationToken) =>
        _locator.LocateAsync(overridePath: null, cancellationToken);

    /// <summary>
    /// Builds the real <see cref="NativeRecoveryWorkflow"/> against the located install, or
    /// <c>null</c> when no Schedule I installation can be found.
    /// </summary>
    public async Task<NativeRecoveryWorkflow?> BuildWorkflowAsync(CancellationToken cancellationToken)
    {
        var installation = await LocateInstallationAsync(cancellationToken).ConfigureAwait(false);
        if (installation is null)
        {
            return null;
        }

        var symbolResolver = new IndexSymbolIdentityResolver(_indexRepository);
        var imageCache = new Il2CppImageCache(LibCpp2IlImageInitializer.InitializeAsync);
        var imageSource = new ScheduleOneNativeImageSource(installation);
        var adapterFactory = new LibCpp2IlAdapterFactory();
        var libraryPins = LoadLibraryPins();
        var unityVersion = UnityVersion.Parse(SupportedUnityVersion);

        var provider = new LibCpp2IlNativeBodyRecoveryProvider(
            imageCache,
            imageSource,
            symbolResolver,
            adapterFactory,
            unityVersion,
            libraryPins);

        return new NativeRecoveryWorkflow(provider);
    }
}
