using Xunit;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;
using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Indexing.Paths;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.Indexing.Tests.Workflow;

public sealed class IndexingWorkflowTests
{
    [Fact]
    public void Index_identity_is_deterministic_and_lower_hex()
    {
        var first = S1Atlas.Indexing.Workflow.IndexingWorkflow.CreateIndexId(
            "extraction-1", "ICSharpCode.Decompiler", "10.1.1.8388", "default", 6);
        var second = S1Atlas.Indexing.Workflow.IndexingWorkflow.CreateIndexId(
            "extraction-1", "ICSharpCode.Decompiler", "10.1.1.8388", "default", 6);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.DoesNotContain(first, char.IsUpper);
    }

    [Fact]
    public async Task Schedule_one_index_is_reused_after_a_completed_run()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-workflow-" + Guid.NewGuid().ToString("N"));
        var extractionRoot = Path.Combine(root, "extraction");
        Directory.CreateDirectory(Path.Combine(extractionRoot, "reconstructed"));
        File.Copy(typeof(S1Atlas.ManagedAssemblyFixture.FixtureRoot).Assembly.Location, Path.Combine(extractionRoot, "reconstructed", "Assembly-CSharp.dll"));
        var repository = new SqliteAtlasRepository(Path.Combine(root, "atlas.db"));
        var buildId = new string('a', 64);
        var extractionId = new string('b', 64);
        var authority = new PreferredVerifiedExtraction(
            buildId,
            new PreferredExtraction(buildId, extractionId, DateTimeOffset.UtcNow, ExtractionPreferenceReason.ManualPromotion),
            new ValidatedExtraction(extractionId, "recipe", buildId, "tool", "attempt", "profile", 1, "profile-digest", 1, 1, "manifest", extractionRoot, DateTimeOffset.UtcNow, ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, new ExtractionStatistics(0, 0, 1, 0, 0, 0, 0, 0, 0, 0, [])));

        try
        {
            await repository.InitializeAsync(TestContext.Current.CancellationToken);
            var workflow = new S1Atlas.Indexing.Workflow.IndexingWorkflow(
                root,
                repository,
                (_, _) => Task.FromResult<PreferredVerifiedExtraction?>(authority),
                new S1Atlas.Indexing.Workflow.ScheduleOneIndexSource(new IlSpyManagedDecompiler()));

            var first = await workflow.RunScheduleOneAsync(buildId, false, TestContext.Current.CancellationToken);
            var second = await workflow.RunScheduleOneAsync(buildId, false, TestContext.Current.CancellationToken);

            Assert.False(first.Reused);
            Assert.True(second.Reused);
            Assert.True(File.Exists(OwnedIndexPaths.ForScheduleOne(root, buildId, first.IndexId).CompleteMarkerPath));
            Assert.True(first.SymbolCount > 0);
            Assert.NotEmpty(await repository.GetCompletedSourceLocationsAsync(first.IndexId, TestContext.Current.CancellationToken));

            var forced = await workflow.RunScheduleOneAsync(buildId, true, TestContext.Current.CancellationToken);
            Assert.False(forced.Reused);
            Assert.NotEqual(first.IndexId, forced.IndexId);
            Assert.NotEqual(first.SnapshotId, forced.SnapshotId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
