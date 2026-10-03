using S1Atlas.Core.Indexing;
using Xunit;

namespace S1Atlas.Core.Tests.Indexing;

public sealed class InteropTypeNamesTests
{
    [Theory]
    [InlineData("Il2CppScheduleOne.Widget", "ScheduleOne.Widget")]
    [InlineData("Il2CppScheduleOne.Outer+Inner", "ScheduleOne.Outer+Inner")]
    [InlineData("Il2Cpp.ActionList", "ActionList")]
    [InlineData("Il2Cpp.CircularQueue`1", "CircularQueue`1")]
    [InlineData("Il2CppScheduleOne.NavigationOverride`1<System.Int32>", "ScheduleOne.NavigationOverride`1<System.Int32>")]
    [InlineData("Il2CppBeautify.Demos.Demo", "Beautify.Demos.Demo")]
    [InlineData("Il2CppInterop.Runtime.X", "Il2CppInterop.Runtime.X")]
    [InlineData("ScheduleOne.Widget", "ScheduleOne.Widget")]
    [InlineData("Game.Widget", "Game.Widget")]
    [InlineData("System.String", "System.String")]
    [InlineData("UnityEngine.GameObject", "UnityEngine.GameObject")]
    [InlineData("Il2CppFoo", "Il2CppFoo")]
    [InlineData("Foo", "Foo")]
    public void Normalizes_generated_names_to_game_names(string typeName, string expected)
    {
        Assert.Equal(expected, InteropTypeNames.Normalize(typeName));
    }
}
