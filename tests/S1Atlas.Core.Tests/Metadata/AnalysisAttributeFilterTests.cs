using S1Atlas.Core.Metadata;
using Xunit;

namespace S1Atlas.Core.Tests.Metadata;

public sealed class AnalysisAttributeFilterTests
{
    [Theory]
    [InlineData("Cpp2ILInjected.AddressAttribute")]
    [InlineData("Cpp2ILInjected.TokenAttribute")]
    [InlineData("Cpp2ILInjected.FieldOffsetAttribute")]
    [InlineData("Cpp2ILInjected.AttributeAttribute")]
    [InlineData("Cpp2ILInjected.CallsAttribute")]
    [InlineData("Cpp2ILInjected.FutureAnalysisAttribute")]
    public void Excludes_injected_analysis_attributes(string fullName)
    {
        Assert.True(AnalysisAttributeFilter.IsAnalysisAttribute(fullName));
    }

    [Theory]
    [InlineData("UnityEngine.SerializeField")]
    [InlineData("UnityEngine.RangeAttribute")]
    [InlineData("UnityEngine.HeaderAttribute")]
    [InlineData("System.ObsoleteAttribute")]
    [InlineData("System.Runtime.CompilerServices.CompilerGeneratedAttribute")]
    [InlineData("FishNet.Object.ServerRpcAttribute")]
    [InlineData("FishNet.Object.ObserversRpcAttribute")]
    [InlineData("FishNet.Object.Synchronizing.SyncVarAttribute")]
    [InlineData("HarmonyLib.HarmonyPatch")]
    [InlineData("HarmonyLib.HarmonyPrefix")]
    [InlineData("Mod.FixtureCaseAttribute")]
    [InlineData("AOT.MonoPInvokeCallbackAttribute")]
    [InlineData("cpp2ilinjected.AddressAttribute")]
    public void Keeps_genuine_game_attributes(string fullName)
    {
        Assert.False(AnalysisAttributeFilter.IsAnalysisAttribute(fullName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Ignores_missing_names(string? fullName)
    {
        Assert.False(AnalysisAttributeFilter.IsAnalysisAttribute(fullName!));
    }
}
