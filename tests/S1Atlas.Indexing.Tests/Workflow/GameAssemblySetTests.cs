using S1Atlas.Indexing.Workflow;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Indexing.Tests.Workflow;

public sealed class GameAssemblySetTests
{
    [Fact]
    public async Task Selects_assembly_csharp_first_then_schedule_one_and_allowlisted_assemblies()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-game-assemblies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var name in new[]
            {
                "UnityEngine.CoreModule.dll", "ScheduleOne.Core.dll", "Assembly-CSharp.dll", "FishNet.Runtime.dll",
                "ScheduleOne.Zeta.dll", "Assembly-CSharp-firstpass.dll", "ScheduleOneX.dll", "ScheduleOne.Core.pdb"
            })
            {
                await File.WriteAllBytesAsync(Path.Combine(root, name), [1], TestContext.Current.CancellationToken);
            }

            var selected = GameAssemblySet.Select(root).Select(Path.GetFileName).ToArray();

            Assert.Equal(new[] { "Assembly-CSharp.dll", "FishNet.Runtime.dll", "ScheduleOne.Core.dll", "ScheduleOne.Zeta.dll" }, selected);
            Assert.Equal("Il2CppScheduleOne.Core.dll", GameAssemblySet.InteropFileName("ScheduleOne.Core.dll"));
            Assert.Equal("Il2CppFishNet.Runtime.dll", GameAssemblySet.InteropFileName("FishNet.Runtime.dll"));
            Assert.Equal("Assembly-CSharp.dll", GameAssemblySet.InteropFileName("Assembly-CSharp.dll"));
            Assert.Equal("ScheduleOne.Core.cs", GameAssemblySet.SourceRelativePath("ScheduleOne.Core.dll"));
            Assert.Equal("FishNet.Runtime.cs", GameAssemblySet.SourceRelativePath("FishNet.Runtime.dll"));
            Assert.True(GameAssemblySet.IsGameAssemblyName("ScheduleOne.Core"));
            Assert.False(GameAssemblySet.IsGameAssemblyName("UnityEngine.CoreModule.dll"));
            Assert.False(GameAssemblySet.IsGameAssemblyName("FishNet.Runtime"));
            Assert.True(GameAssemblySet.IsIndexedAssemblyName("Assembly-CSharp.dll"));
            Assert.True(GameAssemblySet.IsIndexedAssemblyName("ScheduleOne.Core"));
            Assert.True(GameAssemblySet.IsIndexedAssemblyName("FishNet.Runtime.dll"));
            Assert.False(GameAssemblySet.IsIndexedAssemblyName("Assembly-CSharp-firstpass.dll"));
            Assert.False(GameAssemblySet.IsIndexedAssemblyName("UnityEngine.CoreModule.dll"));
            Assert.False(GameAssemblySet.IsIndexedAssemblyName("ScheduleOneX.dll"));
            Assert.False(GameAssemblySet.IsIndexedAssemblyName(""));
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(root);
        }
    }
}
