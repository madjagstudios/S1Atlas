using S1Atlas.Indexing.Workflow;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Indexing.Tests.Workflow;

public sealed class GameAssemblySetTests
{
    [Fact]
    public async Task Selects_assembly_csharp_first_then_only_schedule_one_assemblies()
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

            Assert.Equal(new[] { "Assembly-CSharp.dll", "ScheduleOne.Core.dll", "ScheduleOne.Zeta.dll" }, selected);
            Assert.Equal("Il2CppScheduleOne.Core.dll", GameAssemblySet.InteropFileName("ScheduleOne.Core.dll"));
            Assert.Equal("Assembly-CSharp.dll", GameAssemblySet.InteropFileName("Assembly-CSharp.dll"));
            Assert.Equal("ScheduleOne.Core.cs", GameAssemblySet.SourceRelativePath("ScheduleOne.Core.dll"));
            Assert.True(GameAssemblySet.IsGameAssemblyName("ScheduleOne.Core"));
            Assert.False(GameAssemblySet.IsGameAssemblyName("UnityEngine.CoreModule.dll"));
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(root);
        }
    }
}
