using System.Text.Json;
using AssetRipper.Primitives;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.Metadata;
using LibCpp2IL.Reflection;
using S1Atlas.Core.Discovery;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.NativeRecovery;

namespace S1Atlas.NativeRecovery;

/// <summary>
/// Raw IL2CPP image bytes plus their content hashes, as read from an installed build.
/// Read-only: bytes only, never a live handle to the game process.
/// </summary>
public sealed record NativeImage(
    byte[] GameAssemblyBytes,
    string GameAssemblySha256,
    byte[] MetadataBytes,
    string MetadataSha256);

/// <summary>
/// Supplies the <c>GameAssembly.dll</c> + <c>global-metadata.dat</c> bytes used for recovery.
/// </summary>
public interface INativeImageSource
{
    Task<NativeImage> GetImageAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads the native image bytes directly from a discovered <see cref="ScheduleOneInstallation"/>.
/// Read-only: only ever reads bytes from disk, never launches or mutates the game.
/// </summary>
public sealed class ScheduleOneNativeImageSource(ScheduleOneInstallation installation) : INativeImageSource
{
    private readonly ScheduleOneInstallation _installation =
        installation ?? throw new ArgumentNullException(nameof(installation));

    public async Task<NativeImage> GetImageAsync(CancellationToken cancellationToken)
    {
        var gameAssemblyBytes = await File.ReadAllBytesAsync(_installation.GameAssemblyPath, cancellationToken)
            .ConfigureAwait(false);
        var metadataBytes = await File.ReadAllBytesAsync(_installation.GlobalMetadataPath, cancellationToken)
            .ConfigureAwait(false);

        return new NativeImage(
            gameAssemblyBytes,
            ComputeSha256Hex(gameAssemblyBytes),
            metadataBytes,
            ComputeSha256Hex(metadataBytes));
    }

    private static string ComputeSha256Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>
/// The managed identity (declaring type, method name, parameter types) that an S1Atlas symbol id
/// resolves to. The real index-backed lookup (via <c>IIndexRepository</c>) is wired in a later
/// composition task; this provider only consumes the already-resolved identity, which keeps it
/// unit-testable without the database.
/// </summary>
public sealed record ManagedSymbolDescriptor(
    string DeclaringTypeFullName,
    string MethodName,
    IReadOnlyList<string> ParameterTypeFullNames);

/// <summary>
/// Resolves an S1Atlas symbol id to its managed identity, or <c>null</c> when the symbol id is
/// unknown to the index.
/// </summary>
public interface ISymbolIdentityResolver
{
    ManagedSymbolDescriptor? Resolve(string symbolId);
}

/// <summary>
/// Builds the LibCpp2IL-backed <see cref="IIl2CppMethodLookup"/> / <see cref="IAddressResolver"/> /
/// <see cref="IFieldResolver"/> adapters. Instances are created inside
/// <see cref="Il2CppImageCache.WithImageAsync{T}"/>, after the requested image has been loaded, so
/// they may safely read <see cref="LibCpp2IlMain"/>'s global state. Kept as an injectable seam so
/// the provider itself is testable with fakes and needs no game to run in CI.
/// </summary>
public interface ILibCpp2IlAdapterFactory
{
    IIl2CppMethodLookup CreateMethodLookup();

    IAddressResolver CreateAddressResolver();

    IFieldResolver CreateFieldResolver(string declaringTypeFullName);
}

/// <summary>
/// The real <see cref="ILibCpp2IlAdapterFactory"/>, backed by <see cref="LibCpp2IlMain"/>'s global
/// state. Only usable while that state reflects the image the caller intends to query (i.e. from
/// inside <see cref="Il2CppImageCache.WithImageAsync{T}"/>).
/// </summary>
public sealed class LibCpp2IlAdapterFactory : ILibCpp2IlAdapterFactory
{
    public IIl2CppMethodLookup CreateMethodLookup() => new LibCpp2IlMethodLookup();

    public IAddressResolver CreateAddressResolver() => new LibCpp2IlAddressResolver();

    public IFieldResolver CreateFieldResolver(string declaringTypeFullName) =>
        new LibCpp2IlFieldResolver(declaringTypeFullName);
}

/// <summary>
/// Shared type-name lookup helpers for the LibCpp2IL-backed adapters. IL2CPP's own
/// <see cref="Il2CppTypeDefinition.FullName"/> renders nested types with <c>+</c> (confirmed
/// against the real installed build), while an S1Atlas managed identity may carry <c>.</c> or
/// <c>/</c> for the same nesting. Both sides are normalized before comparison so a valid method
/// does not falsely resolve as not-found (carried forward from Task 2.3).
/// </summary>
internal static class LibCpp2IlTypeNames
{
    public static string NormalizeSeparators(string typeFullName) =>
        typeFullName.Replace('/', '.').Replace('+', '.');

    public static Il2CppTypeDefinition? FindByFullName(string declaringTypeFullName)
    {
        var normalizedRequested = NormalizeSeparators(declaringTypeFullName);
        var typeDefs = LibCpp2IlMain.TheMetadata?.typeDefs ?? [];
        foreach (var type in typeDefs)
        {
            if (type.FullName is not null &&
                string.Equals(NormalizeSeparators(type.FullName), normalizedRequested, StringComparison.Ordinal))
            {
                return type;
            }
        }

        return null;
    }
}

/// <summary>
/// Looks up native method candidates by declaring type + name over <see cref="LibCpp2IlMain"/>'s
/// loaded metadata.
/// </summary>
public sealed class LibCpp2IlMethodLookup : IIl2CppMethodLookup
{
    public IReadOnlyList<NativeMethodCandidate> FindByTypeAndName(string declaringTypeFullName, string methodName)
    {
        var type = LibCpp2IlTypeNames.FindByFullName(declaringTypeFullName);
        if (type is null)
        {
            return [];
        }

        var results = new List<NativeMethodCandidate>();
        foreach (var method in type.Methods ?? [])
        {
            var candidateMethodName = method.Name ?? string.Empty;
            if (!string.Equals(candidateMethodName, methodName, StringComparison.Ordinal))
            {
                continue;
            }

            var parameterTypeFullNames = (method.Parameters ?? [])
                .Select(parameter => parameter.Type?.ToString() ?? string.Empty)
                .ToArray();

            results.Add(new NativeMethodCandidate(
                method.MethodPointer,
                method.MethodOffsetInFile,
                method.Rva,
                type.FullName ?? declaringTypeFullName,
                candidateMethodName,
                parameterTypeFullNames,
                method.IsStatic));
        }

        return results;
    }
}

/// <summary>
/// Resolves a native call-target address using <see cref="LibCpp2IlMain.GetManagedMethodImplementationsAtAddress"/>
/// — proven in the AT-39 spike to be the correct API (unlike <c>GetMethodDefinitionByGlobalAddress</c>,
/// which returns null universally).
/// </summary>
public sealed class LibCpp2IlAddressResolver : IAddressResolver
{
    public AddressResolution Resolve(ulong virtualAddress)
    {
        var implementations = LibCpp2IlMain.GetManagedMethodImplementationsAtAddress(virtualAddress);
        if (implementations is not { Count: > 0 })
        {
            return new AddressResolution(AddressResolutionKind.None, null);
        }

        if (implementations.Count > 1)
        {
            return new AddressResolution(AddressResolutionKind.Ambiguous, null);
        }

        var method = implementations[0];
        var managedName = NativeNameNormalizer.ManagedName(
            method.DeclaringType?.FullName ?? string.Empty, method.Name ?? string.Empty);
        return new AddressResolution(AddressResolutionKind.Single, managedName);
    }
}

/// <summary>
/// Resolves a this-relative field offset to a field name using the declaring type's field layout
/// (<see cref="Il2CppTypeDefinition.FieldInfos"/>, which carries the computed offset — not
/// <c>Fields</c>, which does not). Never inspects alias/read-vs-write state; that hardening is
/// deferred to Task 2.6 against <see cref="BoundedNativeDecoder"/> itself.
/// </summary>
public sealed class LibCpp2IlFieldResolver : IFieldResolver
{
    private readonly IReadOnlyDictionary<long, string> _fieldsByOffset;

    public LibCpp2IlFieldResolver(string declaringTypeFullName)
    {
        var map = new Dictionary<long, string>();
        var type = LibCpp2IlTypeNames.FindByFullName(declaringTypeFullName);
        if (type is not null)
        {
            foreach (var field in type.FieldInfos ?? [])
            {
                var name = field.Field?.Name;
                if (name is not null)
                {
                    map.TryAdd(field.FieldOffset, name);
                }
            }
        }

        _fieldsByOffset = map;
    }

    public string? ResolveFieldName(ulong offset) =>
        _fieldsByOffset.TryGetValue(unchecked((long)offset), out var name) ? name : null;
}

/// <summary>
/// Assembles the Phase-2 pieces (<see cref="Il2CppImageCache"/>, <see cref="ManagedSymbolResolver"/>,
/// <see cref="BoundedNativeDecoder"/>, <see cref="LibraryToolIdentity"/>) into the real
/// <see cref="INativeBodyRecoveryProvider"/>. Read-only: only ever reads bytes supplied by
/// <see cref="INativeImageSource"/>; never launches or mutates the game, never persists raw
/// disassembly.
/// </summary>
public sealed class LibCpp2IlNativeBodyRecoveryProvider : INativeBodyRecoveryProvider
{
    private readonly Il2CppImageCache _imageCache;
    private readonly INativeImageSource _imageSource;
    private readonly ISymbolIdentityResolver _symbolIdentityResolver;
    private readonly ILibCpp2IlAdapterFactory _adapterFactory;
    private readonly UnityVersion _unityVersion;
    private readonly TimeProvider _timeProvider;
    private readonly string _toolVersion;
    private readonly string _toolSha256;

    public LibCpp2IlNativeBodyRecoveryProvider(
        Il2CppImageCache imageCache,
        INativeImageSource imageSource,
        ISymbolIdentityResolver symbolIdentityResolver,
        ILibCpp2IlAdapterFactory adapterFactory,
        UnityVersion unityVersion,
        IReadOnlyList<LibraryPin> libraryPins,
        TimeProvider? timeProvider = null)
    {
        _imageCache = imageCache ?? throw new ArgumentNullException(nameof(imageCache));
        _imageSource = imageSource ?? throw new ArgumentNullException(nameof(imageSource));
        _symbolIdentityResolver = symbolIdentityResolver ?? throw new ArgumentNullException(nameof(symbolIdentityResolver));
        _adapterFactory = adapterFactory ?? throw new ArgumentNullException(nameof(adapterFactory));
        _unityVersion = unityVersion;
        ArgumentNullException.ThrowIfNull(libraryPins);
        _toolVersion = LibraryToolIdentity.ToolVersion(libraryPins);
        _toolSha256 = LibraryToolIdentity.ComputeToolSha256(libraryPins);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Reads the pinned-library descriptor from <c>config/native-recovery/libraries.json</c> (not
    /// <c>config/tools/</c> — that path holds the unrelated executable-tool schema) into the
    /// <see cref="LibraryPin"/> list <see cref="LibraryToolIdentity"/> expects.
    /// </summary>
    public static IReadOnlyList<LibraryPin> LoadLibraryPinsFromConfig(string configFilePath)
    {
        using var stream = File.OpenRead(configFilePath);
        using var document = JsonDocument.Parse(stream);
        var pins = new List<LibraryPin>();
        foreach (var pin in document.RootElement.GetProperty("pins").EnumerateArray())
        {
            pins.Add(new LibraryPin(
                pin.GetProperty("packageId").GetString() ?? throw new InvalidDataException("A pin is missing packageId."),
                pin.GetProperty("version").GetString() ?? throw new InvalidDataException("A pin is missing version."),
                pin.GetProperty("contentSha256").GetString() ?? throw new InvalidDataException("A pin is missing contentSha256.")));
        }

        return pins;
    }

    public async Task<NativeRecoveryRecord> RecoverAsync(NativeRecoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var image = await _imageSource.GetImageAsync(cancellationToken).ConfigureAwait(false);

        return await _imageCache.WithImageAsync(
            image.GameAssemblySha256,
            image.GameAssemblyBytes,
            image.MetadataSha256,
            image.MetadataBytes,
            _unityVersion,
            _ => Task.FromResult(RecoverWithinImage(request, image.GameAssemblyBytes)),
            cancellationToken).ConfigureAwait(false);
    }

    private NativeRecoveryRecord RecoverWithinImage(NativeRecoveryRequest request, byte[] gameAssemblyBytes)
    {
        var methodLookup = _adapterFactory.CreateMethodLookup();
        var addressResolver = _adapterFactory.CreateAddressResolver();

        var resolutions = new List<(string SymbolId, SymbolResolutionResult Result, ManagedSymbolDescriptor? Descriptor)>();
        foreach (var symbolId in request.SymbolIds)
        {
            var descriptor = _symbolIdentityResolver.Resolve(symbolId);
            var result = descriptor is null
                ? new SymbolResolutionResult(
                    SymbolResolution.NotFound,
                    null,
                    "The symbol id did not resolve to a managed method.")
                : ManagedSymbolResolver.Resolve(
                    symbolId,
                    descriptor.DeclaringTypeFullName,
                    descriptor.MethodName,
                    descriptor.ParameterTypeFullNames,
                    methodLookup);
            resolutions.Add((symbolId, result, descriptor));
        }

        var mappingEvidence = resolutions
            .Select(resolution => DescribeResolution(resolution.SymbolId, resolution.Result))
            .ToArray();

        if (resolutions.Any(resolution => resolution.Result.Kind == SymbolResolution.Ambiguous))
        {
            return BuildRecord(request, NativeRecoveryStatus.AmbiguousMapping, mappingEvidence, [], [], isComplete: false);
        }

        var resolved = resolutions.Where(resolution => resolution.Result.Kind == SymbolResolution.Resolved).ToList();
        if (resolved.Count == 0)
        {
            return BuildRecord(request, NativeRecoveryStatus.NoBody, mappingEvidence, [], [], isComplete: false);
        }

        var edges = new List<NativeEvidenceEdge>();
        var fieldAccesses = new List<string>();
        var remainingBudget = request.MaxTraversalEdges;
        var truncated = false;

        foreach (var (_, result, descriptor) in resolved)
        {
            var symbol = result.Symbol!;

            if (remainingBudget <= 0)
            {
                truncated = true;
                continue;
            }

            var offset = checked((int)symbol.MethodOffsetInFile);
            if (offset < 0 || offset >= gameAssemblyBytes.Length)
            {
                truncated = true;
                continue;
            }

            var fieldResolver = _adapterFactory.CreateFieldResolver(descriptor!.DeclaringTypeFullName);
            var code = gameAssemblyBytes.AsSpan(offset);
            // IL2CPP compiles against the x64 calling convention, which passes the first
            // integer/pointer argument (the implicit `this` for an instance method) in RCX. A
            // static method has no `this` pointer, so Register.None disables this-gated
            // field-access evidence entirely for it.
            var thisRegister = symbol.IsStatic ? Register.None : Register.RCX;
            var decoded = BoundedNativeDecoder.Decode(
                code,
                symbol.MethodPointer,
                NativeNameNormalizer.Pointer(symbol.MethodPointer),
                remainingBudget,
                addressResolver,
                fieldResolver,
                thisRegister);

            edges.AddRange(decoded.Edges);
            fieldAccesses.AddRange(decoded.FieldAccesses);
            remainingBudget -= decoded.Edges.Count;
            if (!decoded.IsComplete)
            {
                truncated = true;
            }
        }

        var isComplete = !truncated && edges.All(edge => edge.IsComplete);
        return BuildRecord(request, NativeRecoveryStatus.Recovered, mappingEvidence, edges, fieldAccesses, isComplete);
    }

    private static string DescribeResolution(string symbolId, SymbolResolutionResult result) =>
        result.Kind switch
        {
            SymbolResolution.Resolved =>
                $"{symbolId} resolved to {result.Symbol!.ManagedName} at {NativeNameNormalizer.Pointer(result.Symbol.MethodPointer)}",
            SymbolResolution.Ambiguous =>
                $"{symbolId} ambiguous mapping: {result.Detail}",
            _ =>
                $"{symbolId} not found: {result.Detail}",
        };

    private NativeRecoveryRecord BuildRecord(
        NativeRecoveryRequest request,
        NativeRecoveryStatus status,
        IReadOnlyList<string> mappingEvidence,
        IReadOnlyList<NativeEvidenceEdge> edges,
        IReadOnlyList<string> fieldAccesses,
        bool isComplete)
    {
        var outputSha256 = NativeRecoveryIntegrity.ComputeOutputSha256(
            status, mappingEvidence, edges, fieldAccesses, isComplete, failureMessage: null);
        var recoveryId = NativeRecoveryIntegrity.ComputeRecoveryId(
            request, LibraryToolIdentity.ToolName, _toolVersion, _toolSha256, outputSha256);

        return new NativeRecoveryRecord(
            recoveryId,
            request,
            LibraryToolIdentity.ToolName,
            _toolVersion,
            _toolSha256,
            status,
            mappingEvidence,
            edges,
            fieldAccesses,
            isComplete,
            outputSha256,
            _timeProvider.GetUtcNow(),
            FailureMessage: null);
    }
}
