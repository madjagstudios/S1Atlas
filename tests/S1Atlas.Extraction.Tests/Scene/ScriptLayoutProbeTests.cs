using S1Atlas.Extraction.Scene;
using Xunit;

namespace S1Atlas.Extraction.Tests.Scene;

internal static class ScriptLayoutFixturePaths
{
    public static string ManagedDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "script-layout-fixture");
}

public sealed class ScriptLayoutProbeTests
{
    [Fact]
    public void AssemblyWithSerializeFieldAttributes_IsReportedAsRestored()
    {
        Assert.True(ScriptLayoutProbe.HasRestoredSerializationAttributes(ScriptLayoutFixturePaths.ManagedDirectory));
    }

    [Fact]
    public void AssemblyWithoutSerializeFieldAttributes_IsNotRestored()
    {
        // The ManagedAssemblyFixture (also named Assembly-CSharp) carries no UnityEngine.SerializeField,
        // exactly like a v1 Cpp2IL reconstruction.
        Assert.False(ScriptLayoutProbe.HasRestoredSerializationAttributes(AppContext.BaseDirectory));
    }

    [Fact]
    public void MissingDirectoryOrAssembly_IsNotRestored()
    {
        Assert.False(ScriptLayoutProbe.HasRestoredSerializationAttributes(
            Path.Combine(Path.GetTempPath(), "s1atlas-missing-" + Guid.NewGuid().ToString("N"))));
    }
}
