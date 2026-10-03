using S1Atlas.Application.Readiness;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Tools;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Indexing.Tests.Readiness;

internal sealed class StubDefinitionProvider(
    IReadOnlyList<ResolvedToolDefinition> definitions) : IToolDefinitionProvider
{
    public IReadOnlyList<ResolvedToolDefinition> GetAll() => definitions;

    public ResolvedToolDefinition GetRequired(string toolId, string platform) =>
        throw new NotSupportedException("The readiness probe only enumerates definitions.");
}

internal sealed class ThrowingDefinitionProvider : IToolDefinitionProvider
{
    public IReadOnlyList<ResolvedToolDefinition> GetAll() =>
        throw new ToolOperationException("ToolDefinitionInvalid", "Tool definitions are missing.");

    public ResolvedToolDefinition GetRequired(string toolId, string platform) =>
        throw new NotSupportedException("The readiness probe only enumerates definitions.");
}

public sealed class ReadinessAdapterTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "s1atlas-readiness-adapters-" + Guid.NewGuid().ToString("N"));

    public ReadinessAdapterTests()
    {
        Directory.CreateDirectory(_root);
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
    }

    [Fact]
    public void RuntimeProbe_ReportsCurrentRuntimeAsSupported()
    {
        var info = new DotNetRuntimeProbe().GetCurrent();

        Assert.True(info.IsSupported);
        Assert.Equal(Environment.Version.ToString(), info.Version);
        Assert.False(string.IsNullOrWhiteSpace(info.Detail));
    }

    [Fact]
    public async Task ToolStatusReader_ReturnsCurrentPlatformStatusesInToolIdOrder()
    {
        var inspected = new List<string>();
        var reader = new ReadOnlyManagedToolStatusReader(
            new StubDefinitionProvider(
            [
                ReadinessFixtures.FakeResolvedDefinition("unity-classdata"),
                ReadinessFixtures.FakeResolvedDefinition("cpp2il"),
                ReadinessFixtures.FakeResolvedDefinition("cpp2il", platform: "linux-x64")
            ]),
            (definition, _) =>
            {
                inspected.Add(definition.Definition.ToolId);
                return Task.FromResult(ReadinessFixtures.UnverifiedTool(
                    definition.Definition.ToolId,
                    ToolInstallationStatus.NotInstalled));
            },
            "win-x64");

        var statuses = await reader.GetStatusesAsync(CancellationToken.None);

        Assert.Equal(
            ["cpp2il", "unity-classdata"],
            statuses.Select(status => status.Definition.Definition.ToolId).ToArray());
        Assert.Equal(["cpp2il", "unity-classdata"], inspected);
    }

    [Fact]
    public async Task ToolStatusReader_PropagatesDefinitionFailures()
    {
        var reader = new ReadOnlyManagedToolStatusReader(
            new ThrowingDefinitionProvider(),
            (_, _) => throw new InvalidOperationException("Inspect must not run."),
            "win-x64");

        await Assert.ThrowsAsync<ToolOperationException>(
            () => reader.GetStatusesAsync(CancellationToken.None));
    }

    [Fact]
    public void UpstreamCommitCache_ReturnsCachedCommitsPerCodebase()
    {
        const string commit = "cccccccccccccccccccccccccccccccccccccccc";
        var commitDir = Path.Combine(_root, "upstream", "s1api", "commits", commit);
        Directory.CreateDirectory(commitDir);
        File.WriteAllText(Path.Combine(commitDir, "snapshot-manifest.json"), "[]");
        var cache = new UpstreamCommitCache(_root);

        Assert.Equal([commit], cache.GetCachedCommits(CodebaseKind.S1Api));
        Assert.Empty(cache.GetCachedCommits(CodebaseKind.S1MApi));
    }
}
