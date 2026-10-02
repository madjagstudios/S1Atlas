using S1Atlas.Indexing.Decompilation;
using Xunit;

namespace S1Atlas.Indexing.Tests.Decompilation;

public sealed class GeneratedAttributeTests
{
    [Fact]
    public async Task ParityFixtureReportsStateMachineAttributes()
    {
        var decompiler = new IlSpyManagedDecompiler();
        var result = await decompiler.DecompileAsync(FixturePath(), TestContext.Current.CancellationToken);

        var closures = Assert.Single(result.Types, type => type.FullName == "S1Atlas.ParityFixture.ClosureCases");
        var asyncWork = Assert.Single(closures.Members, member => member.Name == "AsyncWork");
        Assert.EndsWith("+<AsyncWork>d__3", asyncWork.StateMachineTypeName, StringComparison.Ordinal);
        Assert.True(asyncWork.IsAsyncStateMachine);
        var iterator = Assert.Single(closures.Members, member => member.Name == "Iterator");
        Assert.EndsWith("+<Iterator>d__2", iterator.StateMachineTypeName, StringComparison.Ordinal);
        Assert.False(iterator.IsAsyncStateMachine);
        var helper = Assert.Single(closures.Members, member => member.Name == "HelperTarget");
        Assert.Null(helper.StateMachineTypeName);
        Assert.False(helper.IsCompilerGenerated);
        var plain = Assert.Single(closures.Members, member => member.Name == "NonCapturing");
        Assert.Null(plain.StateMachineTypeName);
    }

    [Fact]
    public async Task ParityFixtureReportsCompilerGeneratedFlags()
    {
        var decompiler = new IlSpyManagedDecompiler();
        var result = await decompiler.DecompileAsync(FixturePath(), TestContext.Current.CancellationToken);

        var display = Assert.Single(
            result.Types, type => type.FullName == "S1Atlas.ParityFixture.ClosureCases+<>c__DisplayClass1_0");
        Assert.True(display.IsCompilerGenerated);
        var lambda = Assert.Single(display.Members, member => member.Name == "<Capturing>b__0");
        Assert.False(lambda.IsCompilerGenerated);
        var cache = Assert.Single(
            result.Types, type => type.FullName == "S1Atlas.ParityFixture.ClosureCases+<>c");
        Assert.True(cache.IsCompilerGenerated);
        var closures = Assert.Single(result.Types, type => type.FullName == "S1Atlas.ParityFixture.ClosureCases");
        Assert.False(closures.IsCompilerGenerated);
    }

    private static string FixturePath() =>
        Path.Combine(AppContext.BaseDirectory, "parity-fixture", "S1Atlas.ParityFixture.dll");
}
