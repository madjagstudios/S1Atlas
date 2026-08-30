using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class ApiIndexQueryServiceTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-api-query-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataRoot;
    private readonly SqliteAtlasRepository _repository;
    private int _seedOrdinal;

    public ApiIndexQueryServiceTests()
    {
        Directory.CreateDirectory(_root);
        _dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataRoot);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task ListAsync_distinguishes_current_stale_and_unavailable_api_indexes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);

        var staleEnvironmentId = await SeedEnvironmentAsync("build-stale", cancellationToken);
        var currentEnvironmentId = await SeedEnvironmentAsync("build-current", cancellationToken);
        var current = await SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            "api-installed-current",
            "installed-s1api-binary",
            currentEnvironmentId,
            cancellationToken);
        var release = await SeedIndexAsync(
            CodebaseKind.S1MApi,
            CodeChannel.Release,
            "api-release-s1mapi",
            new string('a', 40),
            environmentSnapshotId: null,
            cancellationToken);
        var stale = await SeedIndexAsync(
            CodebaseKind.S1MApi,
            CodeChannel.Installed,
            "api-installed-stale",
            "installed-s1mapi-binary",
            staleEnvironmentId,
            cancellationToken);

        var result = await CreateService().ListAsync(buildId: null, cancellationToken);

        Assert.Equal("build-current", result.ResolvedBuildId);
        Assert.Null(result.RequestedBuildId);
        Assert.Equal(6, result.Selections.Count);

        var currentSelection = Selection(result, CodebaseKind.S1Api, CodeChannel.Installed);
        Assert.Equal(ApiIndexAvailability.Current, currentSelection.Availability);
        Assert.Equal(current.IndexId, currentSelection.IndexId);
        Assert.Equal(current.SnapshotId, currentSelection.SnapshotId);
        Assert.Equal("installed-s1api-binary", currentSelection.SourceIdentity);
        Assert.Equal(currentEnvironmentId, currentSelection.EnvironmentSnapshotId);

        var staleSelection = Selection(result, CodebaseKind.S1MApi, CodeChannel.Installed);
        Assert.Equal(ApiIndexAvailability.Stale, staleSelection.Availability);
        Assert.Equal(stale.IndexId, staleSelection.IndexId);
        Assert.Equal(stale.SnapshotId, staleSelection.SnapshotId);
        Assert.Equal(staleEnvironmentId, staleSelection.EnvironmentSnapshotId);
        Assert.Contains("build-current", staleSelection.Message, StringComparison.Ordinal);

        var releaseSelection = Selection(result, CodebaseKind.S1MApi, CodeChannel.Release);
        Assert.Equal(ApiIndexAvailability.Current, releaseSelection.Availability);
        Assert.Equal(release.IndexId, releaseSelection.IndexId);
        Assert.Equal(release.SnapshotId, releaseSelection.SnapshotId);
        Assert.Equal(new string('a', 40), releaseSelection.SourceIdentity);
        Assert.Null(releaseSelection.EnvironmentSnapshotId);

        foreach (var unavailable in result.Selections.Where(selection =>
                     selection.Codebase == CodebaseKind.S1Api && selection.Channel != CodeChannel.Installed ||
                     selection.Codebase == CodebaseKind.S1MApi && selection.Channel == CodeChannel.Preview))
        {
            Assert.Equal(ApiIndexAvailability.Unavailable, unavailable.Availability);
            Assert.Null(unavailable.IndexId);
            Assert.Null(unavailable.SnapshotId);
            Assert.False(string.IsNullOrWhiteSpace(unavailable.Message));
        }
    }

    [Fact]
    public async Task ListAsync_with_optional_build_selects_the_completed_installed_snapshot_for_that_build()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);

        var buildAEnvironmentId = await SeedEnvironmentAsync("build-a", cancellationToken);
        var buildBEnvironmentId = await SeedEnvironmentAsync("build-b", cancellationToken);
        await SeedEnvironmentAsync("build-a", cancellationToken);
        var buildA = await SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            "api-installed-build-a",
            "binary-a",
            buildAEnvironmentId,
            cancellationToken);
        var buildB = await SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            "api-installed-build-b",
            "binary-b",
            buildBEnvironmentId,
            cancellationToken);

        var result = await CreateService().ListAsync("build-b", cancellationToken);

        Assert.Equal("build-b", result.RequestedBuildId);
        Assert.Equal("build-b", result.ResolvedBuildId);
        var selected = Selection(result, CodebaseKind.S1Api, CodeChannel.Installed);
        Assert.Equal(ApiIndexAvailability.Current, selected.Availability);
        Assert.Equal(buildB.IndexId, selected.IndexId);
        Assert.Equal(buildB.SnapshotId, selected.SnapshotId);
        Assert.Equal(buildBEnvironmentId, selected.EnvironmentSnapshotId);
        Assert.NotEqual(buildA.IndexId, selected.IndexId);
    }

    [Fact]
    public async Task Stale_installed_api_indexes_are_not_queryable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var staleEnvironmentId = await SeedEnvironmentAsync("build-stale", cancellationToken);
        await SeedEnvironmentAsync("build-current", cancellationToken);
        await SeedIndexAsync(
            CodebaseKind.S1MApi,
            CodeChannel.Installed,
            "api-installed-stale-query",
            "stale-binary",
            staleEnvironmentId,
            cancellationToken,
            symbols:
            [
                Symbol("stale-symbol", CodebaseKind.S1MApi, CodeChannel.Installed, "Demo.Stale")
            ]);

        var service = CreateService();
        var search = await service.SearchAsync(
            CodebaseKind.S1MApi,
            CodeChannel.Installed,
            "Demo.Stale",
            limit: 10,
            cancellationToken);
        var source = await service.SourceAsync(
            CodebaseKind.S1MApi,
            CodeChannel.Installed,
            "Demo.Stale",
            context: 0,
            relatedLimit: 0,
            cancellationToken);

        Assert.Equal(SymbolResolutionStatus.NoCompletedIndex, search.ResolutionStatus);
        Assert.Equal(SymbolResolutionStatus.NoCompletedIndex, source.Resolution.Status);
        Assert.Null(source.Snippet);
    }

    [Fact]
    public async Task Index_only_repository_does_not_claim_requested_build_authority()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        await SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            "api-installed-index-only",
            "index-only-binary",
            environmentSnapshotId: null,
            cancellationToken);

        var proxy = DispatchProxy.Create<IIndexRepository, PassthroughIndexRepositoryProxy>();
        ((PassthroughIndexRepositoryProxy)(object)proxy).Inner = _repository;
        var service = new ApiIndexQueryService(proxy, new IndexQueryService(proxy, _dataRoot));

        var result = await service.ListAsync("unverifiable-build", cancellationToken);
        var selection = Selection(result, CodebaseKind.S1Api, CodeChannel.Installed);

        Assert.Equal("unverifiable-build", result.RequestedBuildId);
        Assert.Null(result.ResolvedBuildId);
        Assert.Equal(ApiIndexAvailability.Unavailable, selection.Availability);
        Assert.NotNull(selection.IndexId);
    }

    [Fact]
    public async Task SearchAsync_delegates_to_the_exact_api_codebase_and_channel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var s1Api = await SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "api-search-s1api-release",
            new string('b', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols:
            [
                Symbol(
                    "s1api-symbol",
                    CodebaseKind.S1Api,
                    CodeChannel.Release,
                    "Demo.ApiOnly")
            ]);
        await SeedIndexAsync(
            CodebaseKind.S1MApi,
            CodeChannel.Release,
            "api-search-s1mapi-release",
            new string('c', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols:
            [
                Symbol(
                    "s1mapi-symbol",
                    CodebaseKind.S1MApi,
                    CodeChannel.Release,
                    "Demo.ApiOnly")
            ]);

        var result = await CreateService().SearchAsync(
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "Demo.ApiOnly",
            limit: 10,
            cancellationToken);

        Assert.Null(result.ResolutionStatus);
        Assert.Equal(1, result.TotalCount);
        var symbol = Assert.Single(result.Results);
        Assert.Equal(s1Api.IndexId, symbol.IndexId);
        Assert.Equal(CodebaseKind.S1Api.ToString(), symbol.Codebase);
        Assert.Equal(CodeChannel.Release.ToString(), symbol.Channel);
        Assert.Equal("s1api-symbol", symbol.SymbolId);
    }

    [Fact]
    public async Task SourceAsync_preserves_body_status_and_relationship_totals_from_the_selected_snapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var sourceIdentity = new string('d', 40);
        var sourceText = "namespace Demo;\npublic sealed class Api\n{\n    public void Run() { }\n}\n";
        var target = Symbol(
            "target",
            CodebaseKind.S1MApi,
            CodeChannel.Release,
            "Demo.Api.Run",
            "System.Void Demo.Api::Run()",
            BodyRecoveryStatus.StubOrUnavailable);
        var caller = Symbol(
            "caller",
            CodebaseKind.S1MApi,
            CodeChannel.Release,
            "Demo.Caller.Invoke",
            "System.Void Demo.Caller::Invoke()",
            BodyRecoveryStatus.Recovered);
        var callee = Symbol(
            "callee",
            CodebaseKind.S1MApi,
            CodeChannel.Release,
            "Demo.Callee.Execute",
            "System.Void Demo.Callee::Execute()",
            BodyRecoveryStatus.Recovered);
        var sourceFile = new IndexSourceFileRecord(
            "api-source-file",
            string.Empty,
            "Api.cs",
            Sha256(sourceText),
            Encoding.UTF8.GetByteCount(sourceText));
        var location = new IndexSourceLocationRecord(target.SymbolId, sourceFile.SourceFileId, 4, 5, 4, 26);
        var index = await SeedIndexAsync(
            CodebaseKind.S1MApi,
            CodeChannel.Release,
            "api-source-s1mapi-release",
            sourceIdentity,
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [target, caller, callee],
            sourceFiles: [sourceFile],
            sourceLocations: [location],
            relationships:
            [
                new IndexRelationshipRecord(
                    "incoming-call",
                    string.Empty,
                    caller.SymbolId,
                    target.SymbolId,
                    null,
                    "Calls",
                    "fixture:incoming"),
                new IndexRelationshipRecord(
                    "outgoing-call",
                    string.Empty,
                    target.SymbolId,
                    callee.SymbolId,
                    null,
                    "Calls",
                    "fixture:outgoing")
            ],
            sourceText);

        var result = await CreateService().SourceAsync(
            CodebaseKind.S1MApi,
            CodeChannel.Release,
            target.SymbolId,
            context: 0,
            relatedLimit: 1,
            cancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        var snippet = Assert.IsType<SourceSnippetQueryResult>(result.Snippet);
        Assert.Equal(index.IndexId, snippet.IndexId);
        Assert.Equal(index.IndexId, snippet.Symbol.IndexId);
        Assert.Equal(CodebaseKind.S1MApi.ToString(), snippet.Symbol.Codebase);
        Assert.Equal(CodeChannel.Release.ToString(), snippet.Symbol.Channel);
        Assert.Equal(BodyRecoveryStatus.StubOrUnavailable, snippet.BodyRecoveryStatus);
        Assert.Equal("S1MApi:Release:generated", snippet.Provenance);
        Assert.NotNull(snippet.Neighborhood);
        Assert.Equal(1, snippet.Neighborhood!.CallerTotal);
        Assert.Equal(1, snippet.Neighborhood.CalleeTotal);
        Assert.Single(snippet.Neighborhood.Callers);
        Assert.Single(snippet.Neighborhood.Callees);
        Assert.Contains("public void Run", snippet.Text, StringComparison.Ordinal);

        var catalog = await CreateService().ListAsync(null, cancellationToken);
        var selection = Selection(catalog, CodebaseKind.S1MApi, CodeChannel.Release);
        Assert.Equal(sourceIdentity, selection.SourceIdentity);
        Assert.Equal(index.SnapshotId, selection.SnapshotId);
    }

    [Fact]
    public async Task SourceAsync_preserves_ambiguous_symbol_resolution_within_the_selected_api_index()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        await SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Preview,
            "api-ambiguous-s1api-preview",
            new string('e', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols:
            [
                Symbol("ambiguous-int", CodebaseKind.S1Api, CodeChannel.Preview, "Demo.Api.Run", "System.Void Demo.Api::Run(System.Int32)"),
                Symbol("ambiguous-string", CodebaseKind.S1Api, CodeChannel.Preview, "Demo.Api.Run", "System.Void Demo.Api::Run(System.String)")
            ]);

        var result = await CreateService().SourceAsync(
            CodebaseKind.S1Api,
            CodeChannel.Preview,
            "Demo.Api.Run",
            context: 0,
            relatedLimit: 0,
            cancellationToken);

        Assert.Equal(SymbolResolutionStatus.Ambiguous, result.Resolution.Status);
        Assert.Null(result.Snippet);
        Assert.Equal(2, result.Resolution.Candidates.Count);
        Assert.All(result.Resolution.Candidates, candidate =>
        {
            Assert.Equal("S1Api", candidate.Codebase);
            Assert.Equal("Preview", candidate.Channel);
        });
    }

    [Fact]
    public async Task Query_operations_do_not_modify_the_atlas_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        await SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "api-read-only",
            new string('f', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols:
            [
                Symbol("read-only-symbol", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ReadOnly")
            ]);
        var before = FileHash(Path.Combine(_root, "atlas.db"));
        var service = CreateService();

        await service.ListAsync(null, cancellationToken);
        await service.SearchAsync(CodebaseKind.S1Api, CodeChannel.Release, "Demo.ReadOnly", 10, cancellationToken);

        var after = FileHash(Path.Combine(_root, "atlas.db"));
        Assert.Equal(before, after);
    }

    private ApiIndexQueryService CreateService() =>
        new(_repository, new IndexQueryService(_repository, _dataRoot));

    private async Task<string> SeedEnvironmentAsync(string buildId, CancellationToken cancellationToken)
    {
        var capturedAt = DateTimeOffset.Parse("2026-08-30T00:00:00Z").AddMinutes(_seedOrdinal++);
        var snapshot = new EnvironmentSnapshot(
            2,
            new GameBuild(
                buildId,
                new string('1', 64),
                new string('2', 64),
                capturedAt,
                true),
            new InstallationObservation("fixture", "app", buildId, _root, null, null),
            [],
            "test",
            capturedAt);
        await _repository.SaveSnapshotAsync(snapshot, cancellationToken);
        return EnvironmentSnapshotId.Create(snapshot);
    }

    private async Task<SeededIndex> SeedIndexAsync(
        CodebaseKind codebase,
        CodeChannel channel,
        string indexId,
        string sourceIdentity,
        string? environmentSnapshotId,
        CancellationToken cancellationToken,
        IReadOnlyList<IndexSymbolRecord>? symbols = null,
        IReadOnlyList<IndexSourceFileRecord>? sourceFiles = null,
        IReadOnlyList<IndexSourceLocationRecord>? sourceLocations = null,
        IReadOnlyList<IndexRelationshipRecord>? relationships = null,
        string? sourceText = null)
    {
        var snapshotId = "snapshot-" + indexId;
        var createdAt = DateTimeOffset.Parse("2026-08-30T01:00:00Z").AddMinutes(_seedOrdinal++).ToString("O");
        await _repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(
                snapshotId,
                codebase,
                channel,
                sourceIdentity,
                createdAt,
                environmentSnapshotId),
            cancellationToken);

        var run = new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, createdAt);
        await _repository.StartIndexRunAsync(run, cancellationToken);
        var normalizedSymbols = (symbols ?? []).Select(symbol => symbol with { SnapshotId = snapshotId }).ToArray();
        var normalizedSourceFiles = (sourceFiles ?? []).Select(file => file with { SnapshotId = snapshotId }).ToArray();
        var normalizedLocations = sourceLocations ?? [];
        var normalizedRelationships = (relationships ?? []).Select(relationship => relationship with { SnapshotId = snapshotId }).ToArray();
        await _repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(
                normalizedSymbols,
                normalizedSourceFiles,
                normalizedLocations,
                [],
                normalizedRelationships),
            DateTimeOffset.Parse("2026-08-30T02:00:00Z").AddMinutes(_seedOrdinal++).ToString("O"),
            cancellationToken);

        if (sourceText is not null && normalizedSourceFiles.Length > 0)
        {
            var sourceRoot = Path.Combine(
                _dataRoot,
                "upstream",
                codebase == CodebaseKind.S1Api ? "s1api" : "s1mapi",
                "commits",
                sourceIdentity,
                "indexes",
                indexId);
            Directory.CreateDirectory(sourceRoot);
            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, normalizedSourceFiles[0].RelativePath),
                sourceText,
                new UTF8Encoding(false),
                cancellationToken);
        }

        return new SeededIndex(indexId, snapshotId);
    }

    private static IndexSymbolRecord Symbol(
        string symbolId,
        CodebaseKind codebase,
        CodeChannel channel,
        string qualifiedName,
        string? signature = null,
        BodyRecoveryStatus? bodyRecoveryStatus = null) =>
        new(
            symbolId,
            string.Empty,
            $"{codebase}:{channel}:Method:{qualifiedName}::{signature ?? "void"}",
            "Method",
            qualifiedName,
            signature ?? $"System.Void {qualifiedName}()",
            false,
            bodyRecoveryStatus);

    private static ApiIndexSelection Selection(
        ApiIndexCatalogResult result,
        CodebaseKind codebase,
        CodeChannel channel) =>
        Assert.Single(result.Selections, selection =>
            selection.Codebase == codebase && selection.Channel == channel);

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string FileHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private sealed record SeededIndex(string IndexId, string SnapshotId);

    private class PassthroughIndexRepositoryProxy : DispatchProxy
    {
        public IIndexRepository Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Invoke(Inner, args);
    }
}
