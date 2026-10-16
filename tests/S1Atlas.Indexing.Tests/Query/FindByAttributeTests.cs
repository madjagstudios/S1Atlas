using S1Atlas.Core.Indexing;
using S1Atlas.Core.Metadata;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class FindByAttributeTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-find-by-attribute-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public FindByAttributeTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task FindByAttributeInIndexAsync_FullNameMatch_ReturnsMatchingSymbolsWithDecodedArguments()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedGameIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "FishNet.Object.ServerRpcAttribute",
            [],
            10,
            cancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.ReturnedCount);
        Assert.Equal(
            ["Demo.Net.ServerRpcA.Run", "Demo.Net.ServerRpcB.Run"],
            result.Results.Select(hit => hit.Symbol.QualifiedName));
        var first = Assert.Single(result.Results[0].MatchingAttributes);
        Assert.Equal("FishNet.Object.ServerRpcAttribute", first.AttributeType);
        Assert.Equal("Member", first.Target);
        Assert.Null(first.ParameterIndex);
        Assert.Null(first.ParameterName);
        var fixedArgument = Assert.Single(first.FixedArguments);
        Assert.Equal(CustomAttributeValueKind.Primitive, fixedArgument.Kind);
        Assert.Equal(42, fixedArgument.Value);
        var namedArgument = Assert.Single(first.NamedArguments);
        Assert.Equal("RequireOwnership", namedArgument.Name);
        Assert.Equal(false, namedArgument.Value.Value);
    }

    [Fact]
    public async Task FindByAttributeInIndexAsync_SuffixMatch_MatchesAfterLastDot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedGameIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "ServerRpcAttribute",
            [],
            10,
            cancellationToken);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(3, result.ReturnedCount);
        Assert.Equal(
            ["FishNet.Object.ServerRpcAttribute", "FishNet.Object.ServerRpcAttribute", "Other.ServerRpcAttribute"],
            result.Results.Select(hit => Assert.Single(hit.MatchingAttributes).AttributeType));
    }

    [Fact]
    public async Task FindByAttributeInIndexAsync_MatchingIsCaseInsensitive()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedGameIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var fullName = await service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "fishnet.object.serverrpcattribute",
            [],
            10,
            cancellationToken);
        var suffix = await service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "serverrpcattribute",
            [],
            10,
            cancellationToken);

        Assert.Equal(2, fullName.TotalCount);
        Assert.Equal(3, suffix.TotalCount);
    }

    [Fact]
    public async Task FindByAttributeInIndexAsync_FullNameMatch_IsCaseInsensitiveForNonAscii()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedNonAsciiAttributeIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var fullName = await service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "Demo.ättribute",
            [],
            10,
            cancellationToken);
        var suffix = await service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "ättribute",
            [],
            10,
            cancellationToken);

        Assert.Equal(1, fullName.TotalCount);
        Assert.Equal("Demo.Case.Sample", Assert.Single(fullName.Results).Symbol.QualifiedName);
        Assert.Equal(1, suffix.TotalCount);
    }

    [Fact]
    public async Task FindByAttributeInIndexAsync_DottedQueryRequiresFullName()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedGameIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "Object.ServerRpcAttribute",
            [],
            10,
            cancellationToken);

        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.ReturnedCount);
        Assert.Empty(result.Results);
    }

    [Fact]
    public async Task FindByAttributeInIndexAsync_NamedArgumentFilter_MatchesNameAndCanonicalValue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedGameIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "FishNet.Object.ServerRpcAttribute",
            ["RequireOwnership=false"],
            10,
            cancellationToken);

        Assert.Equal(1, result.TotalCount);
        var hit = Assert.Single(result.Results);
        Assert.Equal("Demo.Net.ServerRpcA.Run", hit.Symbol.QualifiedName);
    }

    [Fact]
    public async Task FindByAttributeInIndexAsync_BareValueFilter_MatchesAnyFixedArgument()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedGameIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var first = await service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "FishNet.Object.ServerRpcAttribute",
            ["42"],
            10,
            cancellationToken);
        var second = await service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "FishNet.Object.ServerRpcAttribute",
            ["7"],
            10,
            cancellationToken);

        Assert.Equal("Demo.Net.ServerRpcA.Run", Assert.Single(first.Results).Symbol.QualifiedName);
        Assert.Equal("Demo.Net.ServerRpcB.Run", Assert.Single(second.Results).Symbol.QualifiedName);
    }

    [Fact]
    public async Task FindByAttributeInIndexAsync_ArgumentFiltersCombineWithAnd()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedGameIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var matching = await service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "FishNet.Object.ServerRpcAttribute",
            ["RequireOwnership=false", "42"],
            10,
            cancellationToken);
        var conflicting = await service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "FishNet.Object.ServerRpcAttribute",
            ["RequireOwnership=false", "7"],
            10,
            cancellationToken);

        Assert.Equal("Demo.Net.ServerRpcA.Run", Assert.Single(matching.Results).Symbol.QualifiedName);
        Assert.Equal(0, conflicting.TotalCount);
        Assert.Empty(conflicting.Results);
    }

    [Fact]
    public async Task FindByAttributeInIndexAsync_ParameterTarget_PreservesIndexAndName()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedGameIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "Demo.Annotations.ValidateAttribute",
            [],
            10,
            cancellationToken);

        var hit = Assert.Single(result.Results);
        Assert.Equal("Demo.Params.Echo", hit.Symbol.QualifiedName);
        var attribute = Assert.Single(hit.MatchingAttributes);
        Assert.Equal("Parameter", attribute.Target);
        Assert.Equal(0, attribute.ParameterIndex);
        Assert.Equal("value", attribute.ParameterName);
    }

    [Fact]
    public async Task FindByAttributeInIndexAsync_RespectsLimitAndReportsTotal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedGameIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "ServerRpcAttribute",
            [],
            2,
            cancellationToken);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.ReturnedCount);
        Assert.Equal(
            ["Demo.Net.ServerRpcA.Run", "Demo.Net.ServerRpcB.Run"],
            result.Results.Select(hit => hit.Symbol.QualifiedName));
    }

    [Fact]
    public async Task FindByAttributeInIndexAsync_HidesGeneratedSymbolsUnlessRequested()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedGameIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var hidden = await service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "FishNet.Object.ServerRpcAttribute",
            [],
            10,
            cancellationToken);
        var shown = await service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "FishNet.Object.ServerRpcAttribute",
            [],
            10,
            cancellationToken,
            includeGenerated: true);

        Assert.Equal(2, hidden.TotalCount);
        Assert.DoesNotContain(hidden.Results, hit => hit.Symbol.SymbolId == "generated-rpc");
        Assert.Equal(3, shown.TotalCount);
        Assert.Contains(shown.Results, hit => hit.Symbol.SymbolId == "generated-rpc");
    }

    [Fact]
    public async Task FindByAttributeAsync_RoutesThroughChannelsAndAppliesLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedGameIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.FindByAttributeAsync(
            "ServerRpcAttribute",
            [],
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 1),
            cancellationToken);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(1, result.ReturnedCount);
        Assert.Equal("Demo.Net.ServerRpcA.Run", Assert.Single(result.Results).Symbol.QualifiedName);
    }

    [Fact]
    public async Task FindByAttributeAsync_ReturnsEmptyWhenNoIndexOrAttributeRowsExist()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var missing = await service.FindByAttributeAsync(
            "ServerRpcAttribute",
            [],
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 10),
            cancellationToken);

        Assert.Equal(0, missing.TotalCount);
        Assert.Equal(0, missing.ReturnedCount);
        Assert.Empty(missing.Results);
        Assert.Equal(SymbolResolutionStatus.NoCompletedIndex, missing.ResolutionStatus);

        var fixture = await SeedSymbolOnlyIndexAsync(cancellationToken);
        var empty = await service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "ServerRpcAttribute",
            [],
            10,
            cancellationToken);

        Assert.Equal(0, empty.TotalCount);
        Assert.Equal(0, empty.ReturnedCount);
        Assert.Empty(empty.Results);
        Assert.Null(empty.ResolutionStatus);

        var unknown = await service.FindByAttributeAsync(
            "Does.Not.ExistAttribute",
            [],
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 10),
            cancellationToken);

        Assert.Equal(0, unknown.TotalCount);
        Assert.Empty(unknown.Results);
        Assert.Null(unknown.ResolutionStatus);
    }

    [Fact]
    public async Task FindAsync_PopulatesAttributesOnResults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedGameIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var results = await service.FindAsync(
            "Run",
            SymbolKind.Method,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 10),
            cancellationToken);

        Assert.NotEmpty(results);
        var attributed = Assert.Single(results, symbol => symbol.SymbolId == "rpc-a");
        var attribute = Assert.Single(attributed.Attributes!);
        Assert.Equal("FishNet.Object.ServerRpcAttribute", attribute.AttributeType);
        Assert.Equal(42, Assert.Single(attribute.FixedArguments).Value);
        var plain = Assert.Single(results, symbol => symbol.SymbolId == "plain");
        Assert.NotNull(plain.Attributes);
        Assert.Empty(plain.Attributes);
    }

    [Fact]
    public async Task FindInIndexAsync_PopulatesAttributesOnResults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedGameIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var results = await service.FindInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "Value",
            SymbolKind.Field,
            10,
            cancellationToken);

        var field = Assert.Single(results);
        Assert.Equal("health-field", field.SymbolId);
        var attribute = Assert.Single(field.Attributes!);
        Assert.Equal("UnityEngine.SerializeField", attribute.AttributeType);
    }

    [Fact]
    public async Task FindInIndexAsync_LeavesAttributesNullOnReferenceModSymbols()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedGameIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var results = await service.FindInIndexAsync(
            fixture.Run,
            CodebaseKind.ReferenceMod,
            CodeChannel.Installed,
            "Run",
            SymbolKind.Method,
            10,
            cancellationToken);

        Assert.NotEmpty(results);
        Assert.All(results, symbol => Assert.Null(symbol.Attributes));
    }

    [Fact]
    public async Task FindByAttributeSelectedAsync_QueriesTheSelectedApiIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seeded = await SeedApiIndexAsync(cancellationToken);
        var service = new ApiIndexQueryService(_repository, new IndexQueryService(_repository));
        var selection = new ApiIndexSelection(
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            ApiIndexAvailability.Current,
            seeded.IndexId,
            seeded.SnapshotId,
            "api-source",
            null,
            null);

        var result = await service.FindByAttributeSelectedAsync(
            selection,
            "ObsoleteAttribute",
            ["Use New instead"],
            10,
            cancellationToken);

        var hit = Assert.Single(result.Results);
        Assert.Equal("Demo.Api.Legacy", hit.Symbol.QualifiedName);
        var attribute = Assert.Single(hit.MatchingAttributes);
        Assert.Equal("System.ObsoleteAttribute", attribute.AttributeType);
        Assert.Equal("Use New instead", Assert.Single(attribute.FixedArguments).Value);
    }

    [Fact]
    public async Task FindByAttributeSelectedAsync_ReturnsEmptyWhenSelectionHasNoRun()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var service = new ApiIndexQueryService(_repository, new IndexQueryService(_repository));
        var selection = new ApiIndexSelection(
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            ApiIndexAvailability.Unavailable,
            null,
            null,
            null,
            null,
            "No completed S1Api Installed API index is available.");

        var result = await service.FindByAttributeSelectedAsync(
            selection,
            "ObsoleteAttribute",
            [],
            10,
            cancellationToken);

        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.ReturnedCount);
        Assert.Empty(result.Results);
        Assert.Equal(SymbolResolutionStatus.NoCompletedIndex, result.ResolutionStatus);
    }

    [Fact]
    public async Task FindByAttribute_ValidatesArguments()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SeedGameIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        await Assert.ThrowsAsync<ArgumentException>(() => service.FindByAttributeAsync(
            "  ",
            [],
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 10),
            cancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.FindByAttributeAsync(
            "ServerRpcAttribute",
            [],
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, Limit: 0),
            cancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "",
            [],
            10,
            cancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.FindByAttributeInIndexAsync(
            fixture.Run,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "ServerRpcAttribute",
            [],
            0,
            cancellationToken));
    }

    private async Task<QueryFixture> SeedGameIndexAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-find-by-attribute",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "extraction-find-by-attribute",
            "2026-08-28T16:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(
                "index-find-by-attribute",
                snapshot.SnapshotId,
                IndexRunStatus.Running,
                snapshot.CreatedAtUtc),
            cancellationToken);

        var symbols = new[]
        {
            Method("rpc-a", snapshot.SnapshotId, "Demo.Net.ServerRpcA.Run"),
            Method("rpc-b", snapshot.SnapshotId, "Demo.Net.ServerRpcB.Run"),
            Method("other-rpc", snapshot.SnapshotId, "Demo.Other.Handler.Run"),
            Field("health-field", snapshot.SnapshotId, "Demo.Data.Health.Value"),
            Method("plain", snapshot.SnapshotId, "Demo.Plain.Run"),
            Method("param-method", snapshot.SnapshotId, "Demo.Params.Echo"),
            Method("generated-rpc", snapshot.SnapshotId, "Demo.Net.GeneratedRpc.Run", isGenerated: true)
        };
        var attributes = new[]
        {
            Attribute(
                "attr-001",
                snapshot.SnapshotId,
                "rpc-a",
                "Member",
                null,
                null,
                "FishNet.Object.ServerRpcAttribute",
                [Primitive(42)],
                [Named("RequireOwnership", Primitive(false))]),
            Attribute(
                "attr-002",
                snapshot.SnapshotId,
                "rpc-b",
                "Member",
                null,
                null,
                "FishNet.Object.ServerRpcAttribute",
                [Primitive(7)],
                [Named("RequireOwnership", Primitive(true))]),
            Attribute(
                "attr-003",
                snapshot.SnapshotId,
                "other-rpc",
                "Member",
                null,
                null,
                "Other.ServerRpcAttribute",
                [],
                []),
            Attribute(
                "attr-004",
                snapshot.SnapshotId,
                "health-field",
                "Member",
                null,
                null,
                "UnityEngine.SerializeField",
                [],
                []),
            Attribute(
                "attr-005",
                snapshot.SnapshotId,
                "param-method",
                "Parameter",
                0,
                "value",
                "Demo.Annotations.ValidateAttribute",
                [StringValue("x")],
                []),
            Attribute(
                "attr-006",
                snapshot.SnapshotId,
                "generated-rpc",
                "Member",
                null,
                null,
                "FishNet.Object.ServerRpcAttribute",
                [Primitive(9)],
                [Named("RequireOwnership", Primitive(false))])
        };

        await _repository.CompleteIndexRunAsync(
            "index-find-by-attribute",
            new IndexWriteSet(symbols, [], [], [], [], CustomAttributes: attributes),
            "2026-08-28T16:01:00Z",
            cancellationToken);

        return new QueryFixture(
            new IndexRunRecord(
                "index-find-by-attribute",
                snapshot.SnapshotId,
                IndexRunStatus.Completed,
                snapshot.CreatedAtUtc,
                "2026-08-28T16:01:00Z"));
    }

    private async Task<QueryFixture> SeedNonAsciiAttributeIndexAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-find-by-attribute-nonascii",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "extraction-find-by-attribute-nonascii",
            "2026-08-28T16:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(
                "index-find-by-attribute-nonascii",
                snapshot.SnapshotId,
                IndexRunStatus.Running,
                snapshot.CreatedAtUtc),
            cancellationToken);

        await _repository.CompleteIndexRunAsync(
            "index-find-by-attribute-nonascii",
            new IndexWriteSet(
                [Method("nonascii-sample", snapshot.SnapshotId, "Demo.Case.Sample")],
                [],
                [],
                [],
                [],
                CustomAttributes:
                [
                    Attribute(
                        "attr-nonascii-001",
                        snapshot.SnapshotId,
                        "nonascii-sample",
                        "Member",
                        null,
                        null,
                        "Demo.Ättribute",
                        [],
                        [])
                ]),
            "2026-08-28T16:01:00Z",
            cancellationToken);

        return new QueryFixture(
            new IndexRunRecord(
                "index-find-by-attribute-nonascii",
                snapshot.SnapshotId,
                IndexRunStatus.Completed,
                snapshot.CreatedAtUtc,
                "2026-08-28T16:01:00Z"));
    }

    private async Task<QueryFixture> SeedSymbolOnlyIndexAsync(CancellationToken cancellationToken)
    {
        var snapshot = new CodeSnapshotRecord(
            "snapshot-find-by-attribute-empty",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "extraction-find-by-attribute-empty",
            "2026-08-29T16:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(
                "index-find-by-attribute-empty",
                snapshot.SnapshotId,
                IndexRunStatus.Running,
                snapshot.CreatedAtUtc),
            cancellationToken);
        await _repository.CompleteIndexRunAsync(
            "index-find-by-attribute-empty",
            new IndexWriteSet([Method("lonely", snapshot.SnapshotId, "Demo.Lonely.Run")], [], [], [], []),
            "2026-08-29T16:01:00Z",
            cancellationToken);

        return new QueryFixture(
            new IndexRunRecord(
                "index-find-by-attribute-empty",
                snapshot.SnapshotId,
                IndexRunStatus.Completed,
                snapshot.CreatedAtUtc,
                "2026-08-29T16:01:00Z"));
    }

    private async Task<SeededIndex> SeedApiIndexAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        var snapshotId = "snapshot-find-by-attribute-api";
        await _repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(
                snapshotId,
                CodebaseKind.S1Api,
                CodeChannel.Installed,
                "api-source",
                "2026-08-30T01:00:00Z"),
            cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-find-by-attribute-api", snapshotId, IndexRunStatus.Running, "2026-08-30T01:00:00Z"),
            cancellationToken);
        await _repository.CompleteIndexRunAsync(
            "index-find-by-attribute-api",
            new IndexWriteSet(
                [Method("api-legacy", snapshotId, "Demo.Api.Legacy")],
                [],
                [],
                [],
                [],
                CustomAttributes:
                [
                    Attribute(
                        "attr-api-001",
                        snapshotId,
                        "api-legacy",
                        "Member",
                        null,
                        null,
                        "System.ObsoleteAttribute",
                        [StringValue("Use New instead")],
                        [])
                ]),
            "2026-08-30T02:00:00Z",
            cancellationToken);

        return new SeededIndex("index-find-by-attribute-api", snapshotId);
    }

    private static IndexSymbolRecord Method(string id, string snapshotId, string qualifiedName, bool isGenerated = false) =>
        new(
            id,
            snapshotId,
            "ScheduleI:Installed:Method:" + CanonicalMember(qualifiedName),
            "Method",
            qualifiedName,
            "System.Void " + CanonicalMember(qualifiedName) + "()",
            false,
            BodyRecoveryStatus.Recovered,
            IsGenerated: isGenerated);

    private static IndexSymbolRecord Field(string id, string snapshotId, string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf('.');
        var typeName = qualifiedName[..separator];
        var fieldName = qualifiedName[(separator + 1)..];
        return new IndexSymbolRecord(
            id,
            snapshotId,
            "ScheduleI:Installed:Field:" + typeName + "::System.Int32 " + fieldName,
            "Field",
            qualifiedName,
            "System.Int32 " + typeName + "::" + fieldName,
            false);
    }

    private static string CanonicalMember(string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf('.');
        return separator < 0
            ? qualifiedName
            : qualifiedName[..separator] + "::" + qualifiedName[(separator + 1)..];
    }

    private static IndexCustomAttributeRecord Attribute(
        string attributeId,
        string snapshotId,
        string symbolId,
        string target,
        int? parameterIndex,
        string? parameterName,
        string attributeType,
        IReadOnlyList<CustomAttributeValue> fixedArguments,
        IReadOnlyList<CustomAttributeNamedArgument> namedArguments) =>
        new(
            attributeId,
            snapshotId,
            symbolId,
            target,
            parameterIndex,
            parameterName,
            attributeType,
            CustomAttributeJson.ToJson(new DecodedCustomAttribute(attributeType, fixedArguments, namedArguments)));

    private static CustomAttributeValue Primitive(object value) =>
        new(
            CustomAttributeValueKind.Primitive,
            value,
            value switch
            {
                int => "System.Int32",
                bool => "System.Boolean",
                _ => throw new ArgumentOutOfRangeException(nameof(value), "Unsupported test primitive.")
            },
            null);

    private static CustomAttributeValue StringValue(string value) =>
        new(CustomAttributeValueKind.String, value, "System.String", null);

    private static CustomAttributeNamedArgument Named(string name, CustomAttributeValue value) =>
        new(name, value);

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
    }

    private sealed record QueryFixture(IndexRunRecord Run);

    private sealed record SeededIndex(string IndexId, string SnapshotId);
}
