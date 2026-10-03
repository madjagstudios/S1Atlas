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
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class ApiCodebaseRoutingTests
{
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

        var result = await atlas.Tools.ListApiIndexesAsync(null, ct: cancellationToken);

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
    public async Task Invalid_api_arguments_are_rejected_before_the_atlas_is_opened()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-api-tool-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(root));
            var cancellationToken = TestContext.Current.CancellationToken;

            var blankSelector = await tools.SearchSymbolsAsync(" ", McpCodebase.s1api, CodeChannel.Release, limit: 50, ct: cancellationToken);
            var invalidLimit = await tools.SearchSymbolsAsync("Demo.Api", McpCodebase.s1api, CodeChannel.Release, limit: 0, ct: cancellationToken);
            var invalidContext = await tools.GetSourceAsync("Demo.Api", McpCodebase.s1mapi, CodeChannel.Preview, context: -1, ct: cancellationToken);
            var invalidRelatedLimit = await tools.GetSourceAsync("Demo.Api", McpCodebase.s1mapi, CodeChannel.Preview, relatedLimit: 51, ct: cancellationToken);
            var invalidKinds = await tools.FindRelatedTypesAsync("Demo.Api", McpCodebase.s1api, CodeChannel.Release, relationKinds: ["Calls"], ct: cancellationToken);
            var invalidFilter = await tools.FindFieldReferencesAsync("Demo.Api", McpCodebase.s1api, CodeChannel.Release, readers: true, writers: true, ct: cancellationToken);
            var invalidScope = await tools.SearchSymbolsAsync("Demo.Api", McpCodebase.s1api, CodeChannel.Release, scope: IndexQueryScope.Reference, collection: "any", ct: cancellationToken);
            var invalidCollection = await tools.SearchSymbolsAsync("Demo.Api", McpCodebase.s1api, CodeChannel.Release, collection: "any", ct: cancellationToken);

            AssertInvalid(blankSelector, "InvalidArguments");
            AssertInvalid(invalidLimit, "InvalidLimit");
            AssertInvalid(invalidContext, "InvalidContext");
            AssertInvalid(invalidRelatedLimit, "InvalidRelatedLimit");
            AssertInvalid(invalidKinds, "InvalidRelationshipKind");
            AssertInvalid(invalidFilter, "InvalidFieldFilter");
            AssertInvalid(invalidScope, "InvalidScope");
            AssertInvalid(invalidCollection, "InvalidCollection");
            Assert.False(File.Exists(Path.Combine(root, "atlas.db")));
        }
        finally
        {
            await TestDirectory.DeleteTreeAsync(root);
        }
    }

    [Fact]
    public async Task Search_routes_to_s1api_release()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceIdentity = new string('b', 40);
        var seeded = await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "api-routing-search",
            sourceIdentity,
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [ApiSymbol("routing-search", CodebaseKind.S1Api, CodeChannel.Release, "Demo.Parity")]);

        var envelope = await atlas.Tools.SearchSymbolsAsync(
            "Demo.Parity",
            McpCodebase.s1api,
            CodeChannel.Release,
            buildId: null,
            kind: null,
            limit: 10,
            cancellationToken);

        AssertApiSearch(envelope, "S1Api", "Release", seeded.IndexId, "routing-search", sourceIdentity);
    }

    [Fact]
    public async Task Get_source_routes_to_s1mapi_release()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceText = "namespace Demo;\npublic sealed class Parity\n{\n    public void Run() { }\n}\n";
        var symbol = ApiSymbol(
            "routing-source-symbol",
            CodebaseKind.S1MApi,
            CodeChannel.Release,
            "Demo.Parity.Run",
            "System.Void Demo.Parity::Run()");
        var sourceFile = new IndexSourceFileRecord(
            "routing-source-file",
            string.Empty,
            "Parity.cs",
            Sha256(sourceText),
            Encoding.UTF8.GetByteCount(sourceText));
        await atlas.SeedIndexAsync(
            CodebaseKind.S1MApi,
            CodeChannel.Release,
            "api-routing-source",
            new string('c', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [symbol],
            sourceFiles: [sourceFile],
            sourceLocations: [new IndexSourceLocationRecord(symbol.SymbolId, sourceFile.SourceFileId, 4, 5, 4, 26)],
            sourceText: sourceText);

        var envelope = await atlas.Tools.GetSourceAsync(
            symbol.QualifiedName,
            McpCodebase.s1mapi,
            CodeChannel.Release,
            buildId: null,
            context: 0,
            cancellationToken,
            relatedLimit: 0);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(symbol.QualifiedName, envelope.Data!.Symbol.QualifiedName);
        Assert.Contains("public void Run", envelope.Data.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Find_callers_routes_to_s1api_release()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = ApiSymbol("routing-target", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ParityTarget");
        var caller = ApiSymbol("routing-caller", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ParityCaller");
        await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "api-routing-callers",
            new string('d', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [target, caller],
            relationships:
            [
                new("routing-calls", string.Empty, caller.SymbolId, target.SymbolId, null, "Calls", "metadata")
            ]);

        var envelope = await atlas.Tools.FindCallersAsync(
            target.QualifiedName,
            McpCodebase.s1api,
            CodeChannel.Release,
            buildId: null,
            limit: 10,
            cancellationToken,
            exact: true,
            includeDelegates: true);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        var relationship = Assert.Single(envelope.Data!.Relationships);
        Assert.Equal(caller.QualifiedName, relationship.Source.QualifiedName);
    }

    [Fact]
    public async Task Find_callees_routes_to_s1api_release()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var caller = ApiSymbol("routing-caller", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ParityCaller");
        var callee = ApiSymbol("routing-callee", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ParityCallee");
        await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "api-routing-callees",
            new string('e', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [caller, callee],
            relationships:
            [
                new("routing-calls", string.Empty, caller.SymbolId, callee.SymbolId, null, "Calls", "metadata")
            ]);

        var envelope = await atlas.Tools.FindCalleesAsync(
            caller.QualifiedName,
            McpCodebase.s1api,
            CodeChannel.Release,
            buildId: null,
            limit: 10,
            cancellationToken);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        var relationship = Assert.Single(envelope.Data!.Relationships);
        Assert.Equal(callee.QualifiedName, relationship.Target.QualifiedName);
    }

    [Fact]
    public async Task Find_references_routes_to_s1api_release()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = ApiSymbol("routing-target", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ParityTarget");
        var referrer = ApiSymbol("routing-referrer", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ParityReferrer");
        await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "api-routing-references",
            new string('f', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [target, referrer],
            relationships:
            [
                new("routing-refers", string.Empty, referrer.SymbolId, target.SymbolId, null, "Calls", "metadata")
            ]);

        var envelope = await atlas.Tools.FindReferencesAsync(
            target.QualifiedName,
            McpCodebase.s1api,
            CodeChannel.Release,
            buildId: null,
            limit: 10,
            cancellationToken);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        var relationship = Assert.Single(envelope.Data!.Relationships);
        Assert.Equal(referrer.QualifiedName, relationship.Source.QualifiedName);
    }

    [Fact]
    public async Task Find_related_types_routes_to_s1api_release()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = ApiSymbol("routing-target", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ParityTarget", kind: "Type");
        var baseType = ApiSymbol("routing-base", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ParityBase", kind: "Type");
        await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "api-routing-related",
            new string('a', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [target, baseType],
            relationships:
            [
                new("routing-inherits", string.Empty, target.SymbolId, baseType.SymbolId, null, "Inherits", "metadata")
            ]);

        var envelope = await atlas.Tools.FindRelatedTypesAsync(
            target.QualifiedName,
            McpCodebase.s1api,
            CodeChannel.Release,
            buildId: null,
            relationKinds: ["Inherits"],
            limit: 10,
            cancellationToken);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        var relationship = Assert.Single(envelope.Data!.Relationships);
        Assert.Equal("Inherits", relationship.Kind);
        Assert.Equal(baseType.QualifiedName, relationship.Target.QualifiedName);
    }

    [Fact]
    public async Task Find_call_sites_routes_to_s1api_release()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var caller = ApiSymbol("routing-caller", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ParityCaller");
        var target = ApiSymbol("routing-target", CodebaseKind.S1Api, CodeChannel.Release, "Demo.Api.Target", "System.Void Demo.Api::Target()");
        await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "api-routing-call-sites",
            new string('b', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [caller, target],
            relationships:
            [
                new("routing-callsite", string.Empty, caller.SymbolId, target.SymbolId, "Demo.Api::Target()", "Calls", "metadata")
            ]);

        var envelope = await atlas.Tools.FindCallSitesAsync(
            "Demo.Api.Target",
            McpCodebase.s1api,
            CodeChannel.Release,
            buildId: null,
            limit: 10,
            cancellationToken);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(1, envelope.Data!.TotalCount);
    }

    [Fact]
    public async Task Find_field_references_routes_to_s1api_release()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var reader = ApiSymbol("routing-reader", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ParityReader");
        var field = ApiSymbol("routing-field", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ParityWidget::value", kind: "Field");
        await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "api-routing-fields",
            new string('c', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [reader, field],
            relationships:
            [
                new("routing-reads", string.Empty, reader.SymbolId, field.SymbolId, null, "ReadsField", "Body")
            ]);

        var envelope = await atlas.Tools.FindFieldReferencesAsync(
            field.QualifiedName,
            McpCodebase.s1api,
            CodeChannel.Release,
            buildId: null,
            readers: true,
            writers: false,
            limit: 10,
            cancellationToken);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        var relationship = Assert.Single(envelope.Data!.Relationships);
        Assert.Equal(reader.QualifiedName, relationship.Source.QualifiedName);
    }

    [Fact]
    public async Task Get_type_resolves_api_symbol()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "api-routing-get-type",
            new string('d', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [ApiSymbol("routing-widget", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ParityWidget", kind: "Type")]);

        var envelope = await atlas.Tools.GetTypeAsync(
            "Demo.ParityWidget",
            McpCodebase.s1api,
            CodeChannel.Release,
            buildId: null,
            limit: 10,
            cancellationToken);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal("Demo.ParityWidget", envelope.Data!.QualifiedName);
    }

    [Fact]
    public async Task Find_overrides_traverses_api_hierarchy()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var derived = ApiSymbol("routing-derived", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ParityDerived.Render");
        var @base = ApiSymbol("routing-base", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ParityBase.Render");
        await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "api-routing-overrides",
            new string('e', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [derived, @base],
            relationships:
            [
                new("routing-overrides", string.Empty, derived.SymbolId, @base.SymbolId, null, "Overrides", "metadata")
            ]);

        var envelope = await atlas.Tools.FindOverridesAsync(
            derived.QualifiedName,
            McpCodebase.s1api,
            CodeChannel.Release,
            buildId: null,
            limit: 10,
            cancellationToken);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        var node = Assert.Single(envelope.Data!.Nodes);
        Assert.Equal("Overrides", node.Edge.Kind);
        Assert.Equal(@base.QualifiedName, node.Edge.Target.QualifiedName);
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

        var stale = await atlas.Tools.SearchSymbolsAsync(
            "Demo.Stale", McpCodebase.s1api, CodeChannel.Installed, buildId: null, limit: 10, ct: cancellationToken);
        var missing = await atlas.Tools.SearchSymbolsAsync(
            "Demo.Missing", McpCodebase.s1mapi, CodeChannel.Preview, buildId: null, limit: 10, ct: cancellationToken);

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
            new string('f', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols: [ApiSymbol("read-only-symbol", CodebaseKind.S1Api, CodeChannel.Release, "Demo.ReadOnly")]);
        var before = FileTree.HashAll(atlas.Root);

        await atlas.Tools.ListApiIndexesAsync(null, ct: cancellationToken);
        await atlas.Tools.SearchSymbolsAsync(
            "Demo.ReadOnly", McpCodebase.s1api, CodeChannel.Release, buildId: null, limit: 10, ct: cancellationToken);
        await atlas.Tools.GetSourceAsync(
            "Demo.ReadOnly", McpCodebase.s1api, CodeChannel.Release, buildId: null, context: 0, ct: cancellationToken, relatedLimit: 0);

        var after = FileTree.HashAll(atlas.Root);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Get_source_preserves_ambiguity_and_reports_source_failures()
    {
        await using var atlas = await ApiToolAtlas.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        await atlas.SeedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Preview,
            "api-source-ambiguous",
            new string('a', 40),
            environmentSnapshotId: null,
            cancellationToken,
            symbols:
            [
                ApiSymbol("ambiguous-int", CodebaseKind.S1Api, CodeChannel.Preview, "Demo.Api.Run", "System.Void Demo.Api::Run(System.Int32)"),
                ApiSymbol("ambiguous-string", CodebaseKind.S1Api, CodeChannel.Preview, "Demo.Api.Run", "System.Void Demo.Api::Run(System.String)")
            ]);
        var ambiguous = await atlas.Tools.GetSourceAsync(
            "Demo.Api.Run", McpCodebase.s1api, CodeChannel.Preview, buildId: null, context: 0, ct: cancellationToken, relatedLimit: 0);

        Assert.Equal(ToolStatus.Ambiguous, ambiguous.Status);
        Assert.Equal(2, ambiguous.Candidates.Count);
        Assert.Equal(2, ambiguous.TotalCandidateCount);
        Assert.Empty(ambiguous.Suggestions);

        var sourceIdentity = new string('b', 40);
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
        var missing = await atlas.Tools.GetSourceAsync(
            symbol.QualifiedName, McpCodebase.s1api, CodeChannel.Release, buildId: null, context: 0, ct: cancellationToken, relatedLimit: 0);

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
        var integrity = await atlas.Tools.GetSourceAsync(
            symbol.QualifiedName, McpCodebase.s1api, CodeChannel.Release, buildId: null, context: 0, ct: cancellationToken, relatedLimit: 0);

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

        var result = await atlas.Tools.SearchSymbolsAsync(
            "Demo.NoAuthority", McpCodebase.s1api, CodeChannel.Installed, buildId: null, limit: 10, ct: cancellationToken);

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
        BodyRecoveryStatus? bodyRecoveryStatus = null,
        string kind = "Method",
        bool isGenerated = false) =>
        new(
            symbolId,
            string.Empty,
            $"{codebase}:{channel}:{kind}:{qualifiedName}:{signature ?? "void"}",
            kind,
            qualifiedName,
            signature ?? $"System.Void {qualifiedName}()",
            false,
            bodyRecoveryStatus,
            IsGenerated: isGenerated);

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

        public CodeSymbolTools Tools => new(McpServerComposition.BuildReadOnlyServices(Root));

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

        public async ValueTask DisposeAsync()
        {
            await TestDirectory.DeleteTreeAsync(Root);
        }
    }

    private sealed record SeededIndex(string IndexId, string SnapshotId);
}
