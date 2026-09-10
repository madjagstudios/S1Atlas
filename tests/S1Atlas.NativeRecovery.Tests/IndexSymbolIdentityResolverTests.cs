using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.NativeRecovery;
using Xunit;

namespace S1Atlas.NativeRecovery.Tests;

public class IndexSymbolIdentityResolverTests
{
    private const string IndexId = "index-1";

    [Fact]
    public async Task ResolveAsync_KnownMethodSymbol_ParsesSignatureIntoDescriptor()
    {
        var record = new IndexSymbolRecord(
            SymbolId: "sym-method",
            SnapshotId: "snapshot-1",
            CanonicalKey: "canonical-1",
            Kind: "Method",
            QualifiedName: "ScheduleOne.Economy.Customer::EvaluateCounteroffer(ScheduleOne.Product.ProductDefinition,System.Int32,System.Single):System.Boolean",
            Signature: "ScheduleOne.Economy.Customer::EvaluateCounteroffer(ScheduleOne.Product.ProductDefinition,System.Int32,System.Single):System.Boolean",
            IsBestEffort: false);

        var repository = new FakeIndexRepository(new Dictionary<string, IndexSymbolRecord> { ["sym-method"] = record });
        var resolver = new IndexSymbolIdentityResolver(repository);

        var result = await resolver.ResolveAsync(IndexId, ["sym-method"], CancellationToken.None);

        var descriptor = Assert.Single(result).Value;
        Assert.NotNull(descriptor);
        Assert.Equal("ScheduleOne.Economy.Customer", descriptor!.DeclaringTypeFullName);
        Assert.Equal("EvaluateCounteroffer", descriptor.MethodName);
        Assert.Equal(
            ["ScheduleOne.Product.ProductDefinition", "System.Int32", "System.Single"],
            descriptor.ParameterTypeFullNames);
    }

    [Fact]
    public async Task ResolveAsync_UnknownSymbolId_MapsToNull()
    {
        var repository = new FakeIndexRepository(new Dictionary<string, IndexSymbolRecord>());
        var resolver = new IndexSymbolIdentityResolver(repository);

        var result = await resolver.ResolveAsync(IndexId, ["unknown-sym"], CancellationToken.None);

        Assert.Null(Assert.Single(result).Value);
    }

    [Fact]
    public async Task ResolveAsync_NonMethodKind_MapsToNull()
    {
        var record = new IndexSymbolRecord(
            SymbolId: "sym-field",
            SnapshotId: "snapshot-1",
            CanonicalKey: "canonical-2",
            Kind: "Field",
            QualifiedName: "Foo.Bar::baz",
            Signature: "Foo.Bar::baz:System.Int32",
            IsBestEffort: false);

        var repository = new FakeIndexRepository(new Dictionary<string, IndexSymbolRecord> { ["sym-field"] = record });
        var resolver = new IndexSymbolIdentityResolver(repository);

        var result = await resolver.ResolveAsync(IndexId, ["sym-field"], CancellationToken.None);

        Assert.Null(Assert.Single(result).Value);
    }

    [Fact]
    public async Task ResolveAsync_MultipleSymbolIds_ResolvesEachIndependently()
    {
        var methodRecord = new IndexSymbolRecord(
            SymbolId: "sym-method",
            SnapshotId: "snapshot-1",
            CanonicalKey: "canonical-1",
            Kind: "Method",
            QualifiedName: "Foo.Bar::DoWork():System.Void",
            Signature: "Foo.Bar::DoWork():System.Void",
            IsBestEffort: false);

        var repository = new FakeIndexRepository(new Dictionary<string, IndexSymbolRecord>
        {
            ["sym-method"] = methodRecord
        });
        var resolver = new IndexSymbolIdentityResolver(repository);

        var result = await resolver.ResolveAsync(IndexId, ["sym-method", "sym-missing"], CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.NotNull(result["sym-method"]);
        Assert.Equal("Foo.Bar", result["sym-method"]!.DeclaringTypeFullName);
        Assert.Equal("DoWork", result["sym-method"]!.MethodName);
        Assert.Empty(result["sym-method"]!.ParameterTypeFullNames);
        Assert.Null(result["sym-missing"]);
    }

    private sealed class FakeIndexRepository(IReadOnlyDictionary<string, IndexSymbolRecord> symbolsById) : IIndexRepository
    {
        public Task<IndexSymbolRecord?> GetCompletedSymbolByIdAsync(
            string indexId, string symbolId, CancellationToken cancellationToken) =>
            Task.FromResult(symbolsById.TryGetValue(symbolId, out var record) ? record : null);

        public Task CreateCodeSnapshotAsync(CodeSnapshotRecord snapshot, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<CodeSnapshotRecord?> GetCodeSnapshotAsync(string snapshotId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task StartIndexRunAsync(IndexRunRecord run, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task CompleteIndexRunAsync(string indexId, IndexWriteSet writeSet, string completedAtUtc, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task FailIndexRunAsync(string indexId, string failureMessage, string completedAtUtc, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IndexRunRecord?> GetCompletedIndexAsync(string indexId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IndexRunRecord?> GetLatestCompletedIndexAsync(CodebaseKind codebase, CodeChannel channel, string? environmentSnapshotId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsAsync(string indexId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolByCanonicalKeyAsync(string indexId, string canonicalKey, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsByIdsAsync(string indexId, IReadOnlyList<string> symbolIds, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<int> CountCompletedSymbolMatchesAsync(string indexId, string query, CancellationToken cancellationToken, string? kind = null) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<IndexSymbolRecord>> SearchCompletedSymbolsAsync(string indexId, string query, int limit, CancellationToken cancellationToken, string? kind = null) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsAsync(string indexId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsBySourceSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<int> CountCompletedRelationshipsByTargetTextAsync(string indexId, string targetText, RelationshipTargetTextMatchMode matchMode, string relationshipKind, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetTextAsync(string indexId, string targetText, RelationshipTargetTextMatchMode matchMode, string relationshipKind, int limit, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<int> CountCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, string relationshipKind, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, string relationshipKind, int limit, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<IndexSourceFileRecord>> GetCompletedSourceFilesAsync(string indexId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<IndexSourceLocationRecord>> GetCompletedSourceLocationsAsync(string indexId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<IndexFingerprintRecord>> GetCompletedFingerprintsAsync(string indexId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IndexRunRecord?> GetLatestCompletedIndexBySourceIdentityAsync(CodebaseKind codebase, CodeChannel channel, string sourceIdentity, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IndexRunRecord?> GetLatestCompletedIndexForBuildAsync(CodebaseKind codebase, CodeChannel channel, string buildId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }
}
