using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using Xunit;

namespace S1Atlas.Core.Tests.Indexing;

public sealed class CallableSurfaceModelTests
{
    [Fact]
    public void Existing_metadata_and_symbol_constructors_default_visibility_to_private()
    {
        var member = new ManagedMemberFacts("Run", ManagedMemberKind.Method, "Demo::Run()", true, []);
        var symbol = new IndexSymbolRecord("symbol", "snapshot", "key", "Method", "Demo.Run", "Demo::Run()", false);

        Assert.False(member.IsPublic);
        Assert.False(symbol.IsPublic);
    }

    [Fact]
    public void Callable_surface_records_preserve_status_kind_and_local_trust()
    {
        var surface = new IndexCallableSurfaceRecord(
            "surface",
            "index",
            "snapshot",
            "symbol",
            "ScheduleI:Installed:Method:Demo::Run()",
            "Assembly-CSharp.dll",
            "hash",
            "public void Demo.Run()",
            CallableSurfaceKind.NonPublicWrapper,
            true,
            CallableSurfaceStatus.Resolved,
            InteropInputTrust.LocalOnly,
            "wrapper forwards through il2cpp_runtime_invoke");

        Assert.Equal(CallableSurfaceKind.NonPublicWrapper, surface.Kind);
        Assert.Equal(CallableSurfaceStatus.Resolved, surface.Status);
        Assert.True(surface.RequiresReflection);
        Assert.Equal(InteropInputTrust.LocalOnly, surface.InteropInputTrust);
    }
}
