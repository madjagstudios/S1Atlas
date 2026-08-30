using System.Security.Cryptography;
using System.Text;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Mcp.Tools;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class ApiIndexToolTests
{
    [Fact]
    public void Tool_catalog_registers_only_read_only_api_index_names()
    {
        var names = McpToolCatalog.DiscoverToolNames();
        var apiNames = names
            .Where(name => name.Contains("api", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Contains("list_api_indexes", apiNames);
        Assert.Contains("search_api_symbols", apiNames);
        Assert.Contains("get_api_source", apiNames);
        Assert.DoesNotContain(
            apiNames,
            name => name != "list_api_indexes" && new[] { "build", "create", "delete", "download", "index", "install", "mutate", "refresh", "run", "update", "write" }
                .Any(verb => name.Contains(verb, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Invalid_arguments_are_rejected_before_the_atlas_is_opened()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-api-tool-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tools = new ApiIndexTools(McpServerComposition.BuildReadOnlyServices(root));
            var cancellationToken = TestContext.Current.CancellationToken;

            var invalidCodebase = await tools.SearchApiSymbolsAsync("schedulei", "installed", "Demo.Api", 50, cancellationToken);
            var invalidChannel = await tools.SearchApiSymbolsAsync("s1api", "nightly", "Demo.Api", 50, cancellationToken);
            var blankQuery = await tools.SearchApiSymbolsAsync("s1api", "release", " ", 50, cancellationToken);
            var invalidLimit = await tools.SearchApiSymbolsAsync("s1api", "release", "Demo.Api", 0, cancellationToken);
            var invalidContext = await tools.GetApiSourceAsync("s1mapi", "preview", "Demo.Api", -1, 10, cancellationToken);
            var invalidRelatedLimit = await tools.GetApiSourceAsync("s1mapi", "preview", "Demo.Api", 0, 51, cancellationToken);

            AssertInvalid(invalidCodebase, "InvalidCodebase");
            AssertInvalid(invalidChannel, "InvalidChannel");
            AssertInvalid(blankQuery, "InvalidArguments");
            AssertInvalid(invalidLimit, "InvalidLimit");
            AssertInvalid(invalidContext, "InvalidContext");
            AssertInvalid(invalidRelatedLimit, "InvalidRelatedLimit");
            Assert.False(File.Exists(Path.Combine(root, "atlas.db")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task List_api_indexes_preserves_each_completed_selection_and_its_source_provenance()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var environmentId = await atlas.SeedCurrentBuildAsync("build-current", cancellationToken);
        var installed = await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            "api-installed-s1api",
            "installed-s1api-binary",
            environmentId,
            cancellationToken);
        var releaseSourceIdentity = new string('a', 40);
        var release = await atlas.SeedIndexAsync(
            CodebaseKind.S1MApi,
            CodeChannel.Release,
            "api-release-s1mapi",
            releaseSourceIdentity,
            environmentSnapshotId: null,
            cancellationToken);

        var result = await atlas.Tools.ListApiIndexesAsync(null, cancellationToken);

        Assert.Equal(ToolStatus.Resolved, result.Status);
        var catalog = Assert.IsType<ApiIndexCatalogResult>(result.Data);
        Assert.Equal("build-current", catalog.ResolvedBuildId);
        var installedSelection = Assert.Single(catalog.Selections, selection =>
            selection.Codebase == CodebaseKind.S1Api && selection.Channel == CodeChannel.Installed);
        Assert.Equal(ApiIndexAvailability.Current, installedSelection.Availability);
        Assert.Equal(installed.IndexId, installedSelection.IndexId);
        Assert.Equal("installed-s1api-binary", installedSelection.SourceIdentity);
        Assert.Equal(environmentId, installedSelection.EnvironmentSnapshotId);
        var releaseSelection = Assert.Single(catalog.Selections, selection =>
            selection.Codebase == CodebaseKind.S1MApi && selection.Channel == CodeChannel.Release);
        Assert.Equal(ApiIndexAvailability.Current, releaseSelection.Availability);
        Assert.Equal(release.IndexId, releaseSelection.IndexId);
        Assert.Equal(releaseSourceIdentity, releaseSelection.SourceIdentity);
        Assert.Null(releaseSelection.EnvironmentSnapshotId);
        Assert.Contains(result.Provenance, entry =>
            entry.Classification == ProvenanceClassification.Fact &&
            entry.IndexId == installed.IndexId &&
            entry.Source.Contains("installed-s1api-binary", StringComparison.Ordinal));
        Assert.Contains(result.Provenance, entry =>
            entry.Classification == ProvenanceClassification.Fact &&
            entry.IndexId == release.IndexId &&
            entry.BuildId is null &&
            entry.Source.Contains(releaseSourceIdentity, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_api_symbols_uses_the_exact_s1api_or_s1mapi_upstream_scope()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var s1ApiSource = new string('b', 40);
        var s1MApiSource = new string('c', 40);
        var s1Api = await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "api-search-s1api-release",
            s1ApiSource,
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [ApiSymbol("s1api-symbol", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ApiOnly")]);
        var s1MApi = await atlas.SeedIndexAsync(
            CodebaseKind.S1MApi,
            CodeChannel.Preview,
            "api-search-s1mapi-preview",
            s1MApiSource,
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [ApiSymbol("s1mapi-symbol", CodebaseKind.S1MApi, CodeChannel.Preview, "Demo.ApiOnly")]);

        var s1ApiResult = await atlas.Tools.SearchApiSymbolsAsync(
            "s1api", "release", "Demo.ApiOnly", 10, cancellationToken);
        var s1MApiResult = await atlas.Tools.SearchApiSymbolsAsync(
            "S1MAPI", "PREVIEW", "Demo.ApiOnly", 10, cancellationToken);

        AssertApiSearch(s1ApiResult, "S1Api", "Release", s1Api.IndexId, "s1api-symbol", s1ApiSource);
        AssertApiSearch(s1MApiResult, "S1MApi", "Preview", s1MApi.IndexId, "s1mapi-symbol", s1MApiSource);
        Assert.Null(s1ApiResult.Build!.ResolvedBuildId);
        Assert.Null(s1MApiResult.Build!.ResolvedBuildId);
    }

    [Fact]
    public async Task Get_api_source_preserves_source_identity_and_body_status()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceIdentity = new string('d', 40);
        var sourceText = "namespace Demo;\npublic sealed class Api\n{\n    public void Run() { }\n}\n";
        var symbol = ApiSymbol(
            "api-source-symbol",
            CodebaseKind.S1MApi,
            CodeChannel.Release,
            "Demo.Api.Run",
            "System.Void Demo.Api::Run()",
            BodyRecoveryStatus.StubOrUnavailable);
        var sourceFile = new IndexSourceFileRecord(
            "api-source-file",
            string.Empty,
            "Api.cs",
            Sha256(sourceText),
            Encoding.UTF8.GetByteCount(sourceText));
        var index = await atlas.SeedIndexAsync(
            CodebaseKind.S1MApi,
            CodeChannel.Release,
            "api-source-s1mapi-release",
            sourceIdentity,
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [symbol],
            sourceFiles: [sourceFile],
            sourceLocations: [new IndexSourceLocationRecord(symbol.SymbolId, sourceFile.SourceFileId, 4, 5, 4, 26)],
            sourceText: sourceText);

        var result = await atlas.Tools.GetApiSourceAsync(
            "s1mapi",
            "release",
            symbol.QualifiedName,
            context: 0,
            relatedLimit: 0,
            cancellationToken);

        Assert.Equal(ToolStatus.Resolved, result.Status);
        Assert.Equal("S1MApi", result.Build?.Codebase);
        Assert.Equal("Release", result.Build?.Channel);
        Assert.Equal(index.IndexId, result.Build?.IndexId);
        Assert.Equal(sourceIdentity, result.Provenance.First(entry => entry.Classification == ProvenanceClassification.Fact).Source.Split("source=", StringSplitOptions.None).Last());
        var source = Assert.IsType<SourceSnippetQueryResult>(result.Data);
        Assert.Equal(index.IndexId, source.IndexId);
        Assert.Equal(BodyRecoveryStatus.StubOrUnavailable, source.BodyRecoveryStatus);
        Assert.Contains("public void Run", source.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_relationship_tools_preserve_direction_and_filter_type_totals()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = ApiSymbol("api-target", CodebaseKind.S1Api, CodeChannel.Release, "Demo.Target");
        var caller = ApiSymbol("api-caller", CodebaseKind.S1Api, CodeChannel.Release, "Demo.Caller");
        var callee = ApiSymbol("api-callee", CodebaseKind.S1Api, CodeChannel.Release, "Demo.Callee");
        var baseType = ApiSymbol("api-base", CodebaseKind.S1Api, CodeChannel.Release, "Demo.Base");
        var interfaceType = ApiSymbol("api-interface", CodebaseKind.S1Api, CodeChannel.Release, "Demo.Interface");
        await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "api-relationships",
            new string('h', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [target, caller, callee, baseType, interfaceType],
            relationships:
            [
                new("relationship-caller", string.Empty, caller.SymbolId, target.SymbolId, null, "Calls", "metadata"),
                new("relationship-callee", string.Empty, target.SymbolId, callee.SymbolId, null, "Calls", "metadata"),
                new("relationship-inherits", string.Empty, target.SymbolId, baseType.SymbolId, null, "Inherits", "metadata"),
                new("relationship-interface", string.Empty, target.SymbolId, interfaceType.SymbolId, null, "ImplementsInterface", "metadata")
            ]);

        var callers = await atlas.Tools.FindApiCallersAsync("s1api", "release", target.QualifiedName, 10, cancellationToken);
        var callees = await atlas.Tools.FindApiCalleesAsync("s1api", "release", target.QualifiedName, 10, cancellationToken);
        var references = await atlas.Tools.FindApiReferencesAsync("s1api", "release", target.QualifiedName, 10, cancellationToken);
        var related = await atlas.Tools.FindApiRelatedTypesAsync(
            "s1api", "release", target.QualifiedName, ["Inherits"], 10, cancellationToken);

        Assert.Equal(ToolStatus.Resolved, callers.Status);
        Assert.Equal("Incoming", Assert.Single(callers.Data!.Relationships).Direction);
        Assert.Equal(ToolStatus.Resolved, callees.Status);
        Assert.Equal("Outgoing", Assert.Single(callees.Data!.Relationships).Direction);
        Assert.Equal(ToolStatus.Resolved, references.Status);
        Assert.Equal(4, references.Data!.TotalCount);
        Assert.Equal(ToolStatus.Resolved, related.Status);
        Assert.Equal(1, related.Data!.TotalCount);
        Assert.Equal("Inherits", Assert.Single(related.Data.Relationships).Kind);
    }

    [Fact]
    public async Task Missing_and_stale_api_indexes_return_explicit_statuses()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var staleEnvironmentId = await atlas.SeedCurrentBuildAsync("build-stale", cancellationToken);
        await atlas.SeedCurrentBuildAsync("build-current", cancellationToken);
        await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            "api-installed-stale",
            "stale-binary",
            staleEnvironmentId,
            cancellationToken,
            symbols: [ApiSymbol("stale-symbol", CodebaseKind.S1Api, CodeChannel.Installed, "Demo.Stale")]);

        var stale = await atlas.Tools.SearchApiSymbolsAsync(
            "s1api", "installed", "Demo.Stale", 10, cancellationToken);
        var missing = await atlas.Tools.SearchApiSymbolsAsync(
            "s1mapi", "preview", "Demo.Missing", 10, cancellationToken);

        Assert.Equal(ToolStatus.Unavailable, stale.Status);
        Assert.Equal("StaleApiIndex", stale.Error?.Code);
        Assert.Equal(ToolStatus.NotFound, missing.Status);
        Assert.Equal("NoCompletedIndex", missing.Error?.Code);
    }

    [Fact]
    public async Task Api_queries_are_read_only()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "api-read-only",
            new string('e', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [ApiSymbol("read-only-symbol", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ReadOnly")]);
        var before = FileTree.HashAll(atlas.Root);

        await atlas.Tools.ListApiIndexesAsync(null, cancellationToken);
        await atlas.Tools.SearchApiSymbolsAsync("s1api", "release", "Demo.ReadOnly", 10, cancellationToken);
        await atlas.Tools.GetApiSourceAsync("s1api", "release", "Demo.ReadOnly", 0, 0, cancellationToken);

        var after = FileTree.HashAll(atlas.Root);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Get_api_source_preserves_ambiguity_and_reports_source_failures()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Preview,
            "api-source-ambiguous",
            new string('f', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols:
            [
                ApiSymbol("ambiguous-int", CodebaseKind.S1Api, CodeChannel.Preview, "Demo.Api.Run", "System.Void Demo.Api::Run(System.Int32)"),
                ApiSymbol("ambiguous-string", CodebaseKind.S1Api, CodeChannel.Preview, "Demo.Api.Run", "System.Void Demo.Api::Run(System.String)")
            ]);
        var ambiguous = await atlas.Tools.GetApiSourceAsync(
            "s1api", "preview", "Demo.Api.Run", 0, 0, cancellationToken);

        Assert.Equal(ToolStatus.Ambiguous, ambiguous.Status);
        Assert.Equal(2, ambiguous.Candidates.Count);

        var sourceIdentity = new string('g', 40);
        var sourceText = "namespace Demo;\npublic sealed class Api\n{\n    public void Run() { }\n}\n";
        var symbol = ApiSymbol("missing-source", CodebaseKind.S1Api, CodeChannel.Release, "Demo.Api.Missing");
        var sourceFile = new IndexSourceFileRecord(
            "missing-source-file",
            string.Empty,
            "Api.cs",
            Sha256(sourceText),
            Encoding.UTF8.GetByteCount(sourceText));
        var missingIndex = await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "api-source-missing-file",
            sourceIdentity,
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [symbol],
            sourceFiles: [sourceFile],
            sourceLocations: [new IndexSourceLocationRecord(symbol.SymbolId, sourceFile.SourceFileId, 4, 5, 4, 26)]);
        var missing = await atlas.Tools.GetApiSourceAsync(
            "s1api", "release", symbol.QualifiedName, 0, 0, cancellationToken);

        Assert.Equal(ToolStatus.Unavailable, missing.Status);
        Assert.Equal("SourceUnavailable", missing.Error?.Code);

        var tamperedPath = Path.Combine(
            atlas.Root,
            "upstream",
            "s1api",
            "commits",
            sourceIdentity,
            "indexes",
            missingIndex.IndexId,
            sourceFile.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(tamperedPath)!);
        await File.WriteAllTextAsync(tamperedPath, "tampered", cancellationToken);
        var integrity = await atlas.Tools.GetApiSourceAsync(
            "s1api", "release", symbol.QualifiedName, 0, 0, cancellationToken);

        Assert.Equal(ToolStatus.Unavailable, integrity.Status);
        Assert.Equal("SourceIntegrityFailure", integrity.Error?.Code);
    }

    [Fact]
    public async Task Installed_api_query_reports_unavailable_without_current_build_authority()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            "api-installed-no-authority",
            "installed-without-authority",
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [ApiSymbol("no-authority", CodebaseKind.S1Api, CodeChannel.Installed, "Demo.NoAuthority")]);

        var result = await atlas.Tools.SearchApiSymbolsAsync(
            "s1api", "installed", "Demo.NoAuthority", 10, cancellationToken);

        Assert.Equal(ToolStatus.Unavailable, result.Status);
        Assert.Equal("ApiIndexUnavailable", result.Error?.Code);
    }

    private static void AssertInvalid<T>(ToolEnvelope<T> envelope, string errorCode) where T : class
    {
        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal(errorCode, envelope.Error?.Code);
        Assert.Null(envelope.Data);
    }

    private static void AssertApiSearch(
        ToolEnvelope<SymbolSearchResult> envelope,
        string codebase,
        string channel,
        string indexId,
        string symbolId,
        string sourceIdentity)
    {
        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(codebase, envelope.Build?.Codebase);
        Assert.Equal(channel, envelope.Build?.Channel);
        Assert.Equal(indexId, envelope.Build?.IndexId);
        var symbol = Assert.Single(envelope.Data!.Results);
        Assert.Equal(codebase, symbol.Codebase);
        Assert.Equal(channel, symbol.Channel);
        Assert.Equal(indexId, symbol.IndexId);
        Assert.Equal(symbolId, symbol.SymbolId);
        Assert.Contains(envelope.Provenance, entry =>
            entry.Classification == ProvenanceClassification.Fact &&
            entry.IndexId == indexId &&
            entry.Source.Contains(sourceIdentity, StringComparison.Ordinal));
    }

    private static IndexSymbolRecord ApiSymbol(
        string symbolId,
        CodebaseKind codebase,
        CodeChannel channel,
        string qualifiedName,
        string? signature = null,
        BodyRecoveryStatus? bodyRecoveryStatus = null) =>
        new(
            symbolId,
            string.Empty,
            $"{codebase}:{channel}:Method:{qualifiedName}:{signature ?? "void"}",
            "Method",
            qualifiedName,
            signature ?? $"System.Void {qualifiedName}()",
            false,
            bodyRecoveryStatus);

    private static string Sha256(string value) =>
        Sha256(Encoding.UTF8.GetBytes(value));

    private static string Sha256(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed class ApiToolAtlas : IAsyncDisposable
    {
        private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-08-30T00:00:00Z");
        private readonly SqliteAtlasRepository _repository;
        private int _seedOrdinal;

        private ApiToolAtlas(string root)
        {
            Root = root;
            _repository = new SqliteAtlasRepository(Path.Combine(root, "atlas.db"), Path.Combine(root, "backups"));
        }

        public string Root { get; }

        public ApiIndexTools Tools => new(McpServerComposition.BuildReadOnlyServices(Root));

        public static async Task<ApiToolAtlas> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "s1atlas-api-tool-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var atlas = new ApiToolAtlas(root);
            await atlas._repository.InitializeAsync(TestContext.Current.CancellationToken);
            return atlas;
        }

        public async Task<string> SeedCurrentBuildAsync(string buildId, CancellationToken cancellationToken)
        {
            var capturedAt = BaseTime.AddMinutes(_seedOrdinal++);
            var snapshot = new EnvironmentSnapshot(
                2,
                new GameBuild(
                    buildId,
                    new string('1', 64),
                    new string('2', 64),
                    capturedAt,
                    true),
                new InstallationObservation("fixture", "app", buildId, Root, null, null),
                [],
                "test",
                capturedAt);
            await _repository.SaveSnapshotAsync(snapshot, cancellationToken);
            return EnvironmentSnapshotId.Create(snapshot);
        }

        public async Task<SeededIndex> SeedIndexAsync(
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
            var createdAt = BaseTime.AddMinutes(_seedOrdinal++).ToString("O");
            await _repository.CreateCodeSnapshotAsync(
                new CodeSnapshotRecord(
                    snapshotId,
                    codebase,
                    channel,
                    sourceIdentity,
                    createdAt,
                    environmentSnapshotId),
                cancellationToken);
            await _repository.StartIndexRunAsync(
                new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, createdAt),
                cancellationToken);

            var normalizedSymbols = (symbols ?? []).Select(symbol => symbol with { SnapshotId = snapshotId }).ToArray();
            var normalizedSourceFiles = (sourceFiles ?? []).Select(file => file with { SnapshotId = snapshotId }).ToArray();
            var normalizedRelationships = (relationships ?? []).Select(relationship => relationship with { SnapshotId = snapshotId }).ToArray();
            await _repository.CompleteIndexRunAsync(
                indexId,
                new IndexWriteSet(
                    normalizedSymbols,
                    normalizedSourceFiles,
                    sourceLocations ?? [],
                    [],
                    normalizedRelationships),
                BaseTime.AddMinutes(_seedOrdinal++).ToString("O"),
                cancellationToken);

            if (sourceText is not null && normalizedSourceFiles.Length > 0)
            {
                var sourceRoot = Path.Combine(
                    Root,
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

        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SeededIndex(string IndexId, string SnapshotId);
}
