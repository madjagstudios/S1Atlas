using LibCpp2IL.Metadata;
using Xunit;

namespace S1Atlas.NativeRecovery.Tests.Spikes;

/// <summary>
/// Locates the real, locally-installed Schedule I build used by the LocalGameRequired spikes.
/// Read-only: only ever reads bytes from the install; never launches or mutates the game.
/// </summary>
internal static class LocalGameFixture
{
    private const string EnvironmentOverrideVariable = "S1ATLAS_GAME_ROOT";
    private const string DefaultRootPath = @"C:\Program Files (x86)\Steam\steamapps\common\Schedule I";

    public static string RootPath { get; } =
        Environment.GetEnvironmentVariable(EnvironmentOverrideVariable) is { Length: > 0 } overridePath
            ? overridePath
            : DefaultRootPath;

    public static string GameAssemblyPath => Path.Combine(RootPath, "GameAssembly.dll");

    public static string GlobalMetadataPath => Path.Combine(
        RootPath,
        "Schedule I_Data",
        "il2cpp_data",
        "Metadata",
        "global-metadata.dat");

    public static bool IsAvailable => File.Exists(GameAssemblyPath) && File.Exists(GlobalMetadataPath);

    public static void SkipUnlessAvailable()
    {
        Assert.SkipUnless(
            IsAvailable,
            $"Schedule I install not found at '{RootPath}' (override with the {EnvironmentOverrideVariable} " +
            "environment variable). Skipping LocalGameRequired spike.");
    }

    public static (byte[] BinaryBytes, byte[] MetadataBytes) ReadImageBytes() =>
        (File.ReadAllBytes(GameAssemblyPath), File.ReadAllBytes(GlobalMetadataPath));
}

/// <summary>
/// Shared LibCpp2IL lookup helpers for the Customer type used across both spikes.
/// </summary>
internal static class CustomerLookup
{
    public const string DeclaringNamespace = "ScheduleOne.Economy";
    public const string TypeName = "Customer";
    public const string UnitySupportedVersion = "2022.3.62f2";

    public static Il2CppTypeDefinition? FindCustomerType() =>
        LibCpp2IL.LibCpp2IlMain.TheMetadata?.typeDefs
            .SingleOrDefault(type => type.Namespace == DeclaringNamespace && type.Name == TypeName);

    public static Il2CppMethodDefinition? FindMethod(Il2CppTypeDefinition declaringType, string methodName) =>
        (declaringType.Methods ?? []).SingleOrDefault(method => method.Name == methodName);
}

/// <summary>
/// Groups the two spike test classes into a single, non-parallel xUnit collection because
/// <c>LibCpp2IlMain</c> is global static state; running both spikes concurrently would race.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LibCpp2IlGlobalStateCollection
{
    public const string Name = "LibCpp2IL global state";
}
