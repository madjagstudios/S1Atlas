using S1Atlas.Core.Discovery;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.NativeRecovery;
using Xunit;

namespace S1Atlas.NativeRecovery.Tests;

public class NativeRecoveryCompositionTests
{
    private sealed class NeverFindsInstallationLocator : IScheduleOneLocator
    {
        public Task<ScheduleOneInstallation?> LocateAsync(string? overridePath, CancellationToken cancellationToken) =>
            Task.FromResult<ScheduleOneInstallation?>(null);
    }

    private sealed class ThrowingIndexRepository : IIndexRepository
    {
        public Task CreateCodeSnapshotAsync(CodeSnapshotRecord snapshot, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<CodeSnapshotRecord?> GetCodeSnapshotAsync(string snapshotId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task StartIndexRunAsync(IndexRunRecord run, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task CompleteIndexRunAsync(string indexId, IndexWriteSet writeSet, string completedAtUtc, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task FailIndexRunAsync(string indexId, string failureMessage, string completedAtUtc, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IndexRunRecord?> GetCompletedIndexAsync(string indexId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IndexRunRecord?> GetLatestCompletedIndexAsync(CodebaseKind codebase, CodeChannel channel, string? environmentSnapshotId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsAsync(string indexId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolByCanonicalKeyAsync(string indexId, string canonicalKey, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IndexSymbolRecord?> GetCompletedSymbolByIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsByIdsAsync(string indexId, IReadOnlyList<string> symbolIds, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<int> CountCompletedSymbolMatchesAsync(string indexId, string query, CancellationToken cancellationToken, string? kind = null) => throw new NotImplementedException();
        public Task<IReadOnlyList<IndexSymbolRecord>> SearchCompletedSymbolsAsync(string indexId, string query, int limit, CancellationToken cancellationToken, string? kind = null) => throw new NotImplementedException();
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsAsync(string indexId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsBySourceSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<int> CountCompletedRelationshipsByTargetTextAsync(string indexId, string targetText, RelationshipTargetTextMatchMode matchMode, string relationshipKind, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetTextAsync(string indexId, string targetText, RelationshipTargetTextMatchMode matchMode, string relationshipKind, int limit, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<int> CountCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, string relationshipKind, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, string relationshipKind, int limit, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<IndexSourceFileRecord>> GetCompletedSourceFilesAsync(string indexId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<IndexSourceLocationRecord>> GetCompletedSourceLocationsAsync(string indexId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<IndexFingerprintRecord>> GetCompletedFingerprintsAsync(string indexId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IndexRunRecord?> GetLatestCompletedIndexBySourceIdentityAsync(CodebaseKind codebase, CodeChannel channel, string sourceIdentity, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IndexRunRecord?> GetLatestCompletedIndexForBuildAsync(CodebaseKind codebase, CodeChannel channel, string buildId, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private static string FindLibrariesConfigPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "config", "native-recovery", "libraries.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate config/native-recovery/libraries.json.");
    }

    [Fact]
    public void SupportedUnityVersion_ParsesAsAValidUnityVersion()
    {
        var version = AssetRipper.Primitives.UnityVersion.Parse(NativeRecoveryComposition.SupportedUnityVersion);

        Assert.Equal(2022, version.Major);
    }

    [Fact]
    public void LoadLibraryPins_ReadsTheRealConfigFile()
    {
        var composition = new NativeRecoveryComposition(
            new ThrowingIndexRepository(), new NeverFindsInstallationLocator(), FindLibrariesConfigPath());

        var pins = composition.LoadLibraryPins();

        Assert.NotEmpty(pins);
    }

    [Fact]
    public async Task BuildWorkflowAsync_NoInstallationLocated_ReturnsNull()
    {
        var composition = new NativeRecoveryComposition(
            new ThrowingIndexRepository(), new NeverFindsInstallationLocator(), FindLibrariesConfigPath());

        var workflow = await composition.BuildWorkflowAsync(CancellationToken.None);

        Assert.Null(workflow);
    }
}
