using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class SymbolResolverTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-symbol-resolver-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public SymbolResolverTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task Exact_symbol_id_is_the_strongest_selector()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.ExactId.SymbolId,
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Status);
        Assert.Equal(fixture.ExactId.SymbolId, result.Symbol?.SymbolId);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Resolved_symbol_carries_twelve_character_short_id()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.ExactId.SymbolId,
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Status);
        Assert.NotNull(result.Symbol);
        Assert.Equal(fixture.ExactId.SymbolId[..12], result.Symbol.ShortId);
    }

    [Fact]
    public async Task Exact_canonical_key_or_signature_resolves_before_textual_matching()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var byKey = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.Canonical.CanonicalKey,
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken);
        var bySignature = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.Signature.Signature,
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, byKey.Status);
        Assert.Equal(fixture.Canonical.SymbolId, byKey.Symbol?.SymbolId);
        Assert.Equal(SymbolResolutionStatus.Resolved, bySignature.Status);
        Assert.Equal(fixture.Signature.SymbolId, bySignature.Symbol?.SymbolId);
    }

    [Fact]
    public async Task Unique_exact_qualified_name_resolves_before_lower_ranked_matches()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.ExactName.QualifiedName,
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Status);
        Assert.Equal(fixture.ExactName.SymbolId, result.Symbol?.SymbolId);
    }

    [Fact]
    public async Task Unique_best_ranked_textual_match_resolves()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "inventory",
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Status);
        Assert.Equal(fixture.UniqueText.SymbolId, result.Symbol?.SymbolId);
    }

    [Fact]
    public async Task Equal_best_rank_is_ambiguous_and_never_first_row_wins()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "dealer",
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Ambiguous, result.Status);
        Assert.Null(result.Symbol);
        Assert.Equal(
            new[] { fixture.DealerA.SymbolId, fixture.DealerB.SymbolId }.Order(StringComparer.Ordinal),
            result.Candidates.Select(candidate => candidate.SymbolId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Kinded_id_of_matching_kind_resolves()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.Canonical.SymbolId,
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken,
            kinds: new HashSet<SymbolKind> { SymbolKind.Type });

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Status);
        Assert.Equal(fixture.Canonical.SymbolId, result.Symbol?.SymbolId);
        Assert.IsType<SymbolResolutionResult>(result);
    }

    [Fact]
    public async Task Kinded_search_resolves_type_despite_fifty_other_kind_matches()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "Crowd",
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken,
            kinds: new HashSet<SymbolKind> { SymbolKind.Type });

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Status);
        Assert.Equal(fixture.CrowdType.SymbolId, result.Symbol?.SymbolId);
    }

    [Fact]
    public async Task Kinded_id_of_wrong_kind_returns_not_found_with_mismatch()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.Signature.SymbolId,
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken,
            kinds: new HashSet<SymbolKind> { SymbolKind.Type });

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Null(result.Symbol);
        Assert.Empty(result.Candidates);
        var kinded = Assert.IsType<KindedSymbolResolutionResult>(result);
        Assert.Equal(fixture.Signature.SymbolId, kinded.KindMismatch.SymbolId);
        Assert.Equal("Method", kinded.KindMismatch.Kind);
    }

    [Fact]
    public async Task Kinded_canonical_key_of_wrong_kind_returns_not_found_with_mismatch()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.Signature.CanonicalKey,
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken,
            kinds: new HashSet<SymbolKind> { SymbolKind.Type });

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Null(result.Symbol);
        var kinded = Assert.IsType<KindedSymbolResolutionResult>(result);
        Assert.Equal(fixture.Signature.SymbolId, kinded.KindMismatch.SymbolId);
    }

    [Fact]
    public async Task Kinded_absent_canonical_key_does_not_fall_through_to_fuzzy_matching()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "S1Api:Release:Type:ExactWidget",
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken,
            kinds: new HashSet<SymbolKind> { SymbolKind.Type });

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Null(result.Symbol);
        Assert.IsType<SymbolResolutionResult>(result);
    }

    [Fact]
    public async Task Multi_kind_set_merges_searches_and_excludes_other_kinds()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "Factory",
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken,
            kinds: new HashSet<SymbolKind> { SymbolKind.Method, SymbolKind.Constructor });

        Assert.Equal(SymbolResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(
            new[] { fixture.FactoryMethod.SymbolId, fixture.FactoryCtor.SymbolId }.Order(StringComparer.Ordinal),
            result.Candidates.Select(candidate => candidate.SymbolId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task No_matching_symbol_returns_not_found()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "definitely-not-present",
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Null(result.Symbol);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Unique_short_id_prefix_resolves()
    {
        var fixture = await SeedPrefixAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.SoloId[..12],
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Status);
        Assert.Equal(fixture.SoloId, result.Symbol?.SymbolId);
    }

    [Fact]
    public async Task Uppercase_short_id_prefix_resolves()
    {
        var fixture = await SeedPrefixAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.SoloId[..12].ToUpperInvariant(),
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Status);
        Assert.Equal(fixture.SoloId, result.Symbol?.SymbolId);
    }

    [Fact]
    public async Task Ambiguous_short_id_prefix_lists_deterministic_candidates_with_exact_total()
    {
        var fixture = await SeedPrefixAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "abcdef00",
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(
            [fixture.PairAId, fixture.PairBId],
            result.Candidates.Select(candidate => candidate.SymbolId));
        Assert.Equal(2, result.TotalCandidateCount);
    }

    [Fact]
    public async Task Truncated_short_id_prefix_reports_exact_total_via_count()
    {
        var fixture = await SeedPrefixAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "beefbeef",
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(50, result.Candidates.Count);
        Assert.Equal(55, result.TotalCandidateCount);
    }

    [Fact]
    public async Task Seven_hex_chars_fall_through_to_textual_matching()
    {
        var fixture = await SeedPrefixAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.SoloId[..7],
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Empty(result.Candidates);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public async Task Non_hex_selector_skips_prefix_probe()
    {
        var fixture = await SeedPrefixAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "zzzzzzzz",
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public async Task Single_prefix_match_of_wrong_kind_returns_kind_mismatch()
    {
        var fixture = await SeedPrefixAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.WidgetId[..12],
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken,
            kinds: new HashSet<SymbolKind> { SymbolKind.Method });

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        var kinded = Assert.IsType<KindedSymbolResolutionResult>(result);
        Assert.Equal(fixture.WidgetId, kinded.KindMismatch.SymbolId);
    }

    [Fact]
    public async Task Prefix_probe_miss_falls_through_to_textual_match()
    {
        var fixture = await SeedPrefixAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "abcdef12",
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Status);
        Assert.Equal("PrefixProbe.abcdef12", result.Symbol?.QualifiedName);
    }

    [Fact]
    public async Task Unknown_hex_prefix_is_not_found_without_suggestions()
    {
        var fixture = await SeedPrefixAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "12345678",
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public async Task Long_prefix_disambiguates_shared_leader()
    {
        var fixture = await SeedPrefixAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "abcdef000",
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Status);
        Assert.Equal(fixture.PairAId, result.Symbol?.SymbolId);
    }

    [Fact]
    public async Task Dotted_unknown_selector_suggests_close_simple_names()
    {
        var fixture = await SeedSuggestionAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "Demo.Probe.Compote",
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Equal(
            ["Demo.Probe.Compute", "Demo.Probe.Compare"],
            result.Suggestions.Select(suggestion => suggestion.QualifiedName));
    }

    [Fact]
    public async Task Non_dotted_typo_suggests_via_broadening()
    {
        var fixture = await SeedSuggestionAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "Compote",
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Equal(
            ["Demo.Probe.Compute", "Demo.Probe.Compare"],
            result.Suggestions.Select(suggestion => suggestion.QualifiedName));
    }

    [Fact]
    public async Task Gibberish_selector_yields_no_suggestions()
    {
        var fixture = await SeedSuggestionAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "zzzqqq",
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public async Task Suggestion_threshold_excludes_distant_pool_matches()
    {
        var fixture = await SeedSuggestionAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "Demo.Probe.Alphx",
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Equal(
            ["Demo.Probe.Alpha"],
            result.Suggestions.Select(suggestion => suggestion.QualifiedName));
    }

    [Fact]
    public async Task Suggestions_cap_at_five_in_pool_order()
    {
        var fixture = await SeedSuggestionAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "Demo.Probe.Testx",
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Equal(
            ["Demo.Probe.Test1", "Demo.Probe.Test2", "Demo.Probe.Test3", "Demo.Probe.Test4", "Demo.Probe.Test5"],
            result.Suggestions.Select(suggestion => suggestion.QualifiedName));
    }

    [Fact]
    public async Task Suggestions_respect_kind_filters()
    {
        var fixture = await SeedSuggestionAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "Demo.Probe.Widgxx",
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken,
            kinds: new HashSet<SymbolKind> { SymbolKind.Method });

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Equal(
            ["Demo.Probe.Widgex"],
            result.Suggestions.Select(suggestion => suggestion.QualifiedName));
    }

    [Fact]
    public async Task Canonical_path_not_found_suggests_close_names()
    {
        var fixture = await SeedSuggestionAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "S1Api:Release:Method:Demo.Probe.Nope",
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken,
            kinds: new HashSet<SymbolKind> { SymbolKind.Method });

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Equal(
            ["Demo.Probe.Nopes"],
            result.Suggestions.Select(suggestion => suggestion.QualifiedName));
    }

    [Fact]
    public async Task Suggestion_distance_ignores_case()
    {
        var fixture = await SeedSuggestionAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "Demo.Probe.COMPUTEX",
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Status);
        Assert.Equal(
            ["Demo.Probe.Compute"],
            result.Suggestions.Select(suggestion => suggestion.QualifiedName));
    }

    [Fact]
    public async Task Ambiguous_tie_reports_exact_total()
    {
        var fixture = await SeedAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "dealer",
            fixture.Codebase,
            fixture.Channel,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.TotalCandidateCount);
    }

    [Fact]
    public async Task Truncated_exact_signature_reports_unknown_total()
    {
        var fixture = await SeedDuplicateSignatureAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            fixture.Signature,
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(51, result.Candidates.Count);
        Assert.Null(result.TotalCandidateCount);
    }

    [Fact]
    public async Task Truncated_tie_reports_unknown_total()
    {
        var fixture = await SeedTieAsync(TestContext.Current.CancellationToken);
        var resolver = new SymbolResolver(_repository);

        var result = await resolver.ResolveAsync(
            fixture.IndexId,
            "Tie",
            CodebaseKind.S1Api,
            CodeChannel.Release,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(50, result.Candidates.Count);
        Assert.Null(result.TotalCandidateCount);
    }

    private async Task<PrefixFixture> SeedPrefixAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        const string snapshotId = "snapshot-resolver-prefix";
        const string indexId = "index-resolver-prefix";
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "resolver-prefix-source",
            "2026-08-14T03:10:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        IndexSymbolRecord Symbol(string id, string key, string kind, string name) =>
            new(id, snapshotId, key, kind, name, "System.Void PrefixProbe::Run()", false);

        var soloId = new string('1', 64);
        var pairAId = "abcdef00" + new string('0', 55) + "1";
        var pairBId = "abcdef001" + new string('0', 54) + "2";
        var widgetId = new string('c', 64);
        var symbols = new List<IndexSymbolRecord>
        {
            Symbol(soloId, "S1Api:Release:Method:PrefixProbe.Solo::Run()", "Method", "PrefixProbe.Solo"),
            Symbol(pairAId, "S1Api:Release:Method:PrefixProbe.PairA::Run()", "Method", "PrefixProbe.PairA"),
            Symbol(pairBId, "S1Api:Release:Method:PrefixProbe.PairB::Run()", "Method", "PrefixProbe.PairB"),
            Symbol(new string('9', 64), "S1Api:Release:Method:PrefixProbe.HexName::Run()", "Method", "PrefixProbe.abcdef12"),
            Symbol(widgetId, "S1Api:Release:Type:PrefixProbe.Widget", "Type", "PrefixProbe.Widget"),
        };
        for (var crowd = 0; crowd < 55; crowd++)
        {
            var suffix = crowd.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            symbols.Add(Symbol(
                "beefbeef" + suffix + new string('0', 52),
                $"S1Api:Release:Method:PrefixProbe.Crowd{crowd:00}::Run()",
                "Method",
                $"PrefixProbe.Crowd{crowd:00}"));
        }

        await _repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(symbols, [], [], [], []),
            "2026-08-14T03:11:00Z",
            cancellationToken);
        return new PrefixFixture(indexId, soloId, pairAId, pairBId, widgetId);
    }

    private async Task<SuggestionFixture> SeedSuggestionAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        const string snapshotId = "snapshot-resolver-suggest";
        const string indexId = "index-resolver-suggest";
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "resolver-suggest-source",
            "2026-08-14T03:12:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        IndexSymbolRecord Symbol(string key, string kind, string name) =>
            new(
                HashId(snapshotId + "\n" + key),
                snapshotId,
                key,
                kind,
                name,
                "System.Void Demo.Probe::Run()",
                false);

        var symbols = new List<IndexSymbolRecord>
        {
            Symbol("S1Api:Release:Method:Demo.Probe.Compute::Run()", "Method", "Demo.Probe.Compute"),
            Symbol("S1Api:Release:Method:Demo.Probe.Compare::Run()", "Method", "Demo.Probe.Compare"),
            Symbol("S1Api:Release:Method:Demo.Probe.Alpha::Run()", "Method", "Demo.Probe.Alpha"),
            Symbol("S1Api:Release:Method:Demo.Probe.Alphabet::Run()", "Method", "Demo.Probe.Alphabet"),
            Symbol("S1Api:Release:Method:Demo.Probe.Widgex::Run()", "Method", "Demo.Probe.Widgex"),
            Symbol("S1Api:Release:Type:Demo.Probe.Widget", "Type", "Demo.Probe.Widget"),
            Symbol("S1Api:Release:Method:Demo.Probe.Nopes::Run()", "Method", "Demo.Probe.Nopes"),
        };
        for (var test = 1; test <= 6; test++)
            symbols.Add(Symbol(
                $"S1Api:Release:Method:Demo.Probe.Test{test}::Run()",
                "Method",
                $"Demo.Probe.Test{test}"));

        await _repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(symbols, [], [], [], []),
            "2026-08-14T03:13:00Z",
            cancellationToken);
        return new SuggestionFixture(indexId);
    }

    private async Task<TieFixture> SeedTieAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        const string snapshotId = "snapshot-resolver-tie";
        const string indexId = "index-resolver-tie";
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "resolver-tie-source",
            "2026-08-14T03:14:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        var symbols = new List<IndexSymbolRecord>();
        for (var tie = 0; tie < 55; tie++)
        {
            var key = $"S1Api:Release:Method:Tie.Breaker{tie:00}::Run()";
            symbols.Add(new IndexSymbolRecord(
                HashId(snapshotId + "\n" + key),
                snapshotId,
                key,
                "Method",
                $"Tie.Breaker{tie:00}",
                $"System.Void Tie::Breaker{tie:00}()",
                false));
        }

        await _repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(symbols, [], [], [], []),
            "2026-08-14T03:15:00Z",
            cancellationToken);
        return new TieFixture(indexId);
    }

    private async Task<DuplicateSignatureFixture> SeedDuplicateSignatureAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        const string snapshotId = "snapshot-resolver-dup-signature";
        const string indexId = "index-resolver-dup-signature";
        const string signature = "System.Void Dup::Clone()";
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "resolver-dup-signature-source",
            "2026-08-14T03:16:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        var symbols = new List<IndexSymbolRecord>();
        for (var clone = 0; clone < 55; clone++)
        {
            var key = $"S1Api:Release:Method:Dup.Clone{clone:00}::Run()";
            symbols.Add(new IndexSymbolRecord(
                HashId(snapshotId + "\n" + key),
                snapshotId,
                key,
                "Method",
                $"Dup.Clone{clone:00}",
                signature,
                false));
        }

        await _repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(symbols, [], [], [], []),
            "2026-08-14T03:17:00Z",
            cancellationToken);
        return new DuplicateSignatureFixture(indexId, signature);
    }

    private async Task<ResolverFixture> SeedAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        const string snapshotId = "snapshot-resolver";
        const string indexId = "index-resolver";
        const CodebaseKind codebase = CodebaseKind.S1Api;
        const CodeChannel channel = CodeChannel.Release;
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            codebase,
            channel,
            "resolver-source",
            "2026-08-14T03:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        IndexSymbolRecord Symbol(string key, string kind, string name, string signature) =>
            new(
                HashId(snapshotId + "\n" + key),
                snapshotId,
                key,
                kind,
                name,
                signature,
                false);

        var exactId = Symbol(
            "S1Api:Release:Type:Demo.ById",
            "Type",
            "Demo.ById",
            "Demo.ById");
        var canonical = Symbol(
            "S1Api:Release:Type:Demo.CanonicalTarget",
            "Type",
            "Demo.CanonicalTarget",
            "Demo.CanonicalTarget");
        var signature = Symbol(
            "S1Api:Release:Method:Demo.Signatures::Execute(System.Int32)",
            "Method",
            "Demo.Signatures.Execute",
            "System.Void Demo.Signatures::Execute(System.Int32)");
        var exactName = Symbol(
            "S1Api:Release:Type:Demo.ExactWidget",
            "Type",
            "Demo.ExactWidget",
            "Demo.ExactWidget");
        var lowerExactName = Symbol(
            "S1Api:Release:Type:Demo.ExactWidgetHelper",
            "Type",
            "Demo.ExactWidgetHelper",
            "Demo.ExactWidgetHelper");
        var uniqueText = Symbol(
            "S1Api:Release:Type:InventoryService",
            "Type",
            "InventoryService",
            "InventoryService");
        var lowerText = Symbol(
            "S1Api:Release:Type:Demo.SuperInventoryArchive",
            "Type",
            "Demo.SuperInventoryArchive",
            "Demo.SuperInventoryArchive");
        var dealerA = Symbol(
            "S1Api:Release:Type:Alpha.DealerService",
            "Type",
            "Alpha.DealerService",
            "Alpha.DealerService");
        var dealerB = Symbol(
            "S1Api:Release:Type:Beta.DealerService",
            "Type",
            "Beta.DealerService",
            "Beta.DealerService");
        var crowdMethods = Enumerable.Range(0, 50)
            .Select(i => Symbol(
                $"S1Api:Release:Method:Crowd.A{i:00}::Run()",
                "Method",
                $"Crowd.A{i:00}.Run",
                $"System.Void Crowd.A{i:00}::Run()"))
            .ToArray();
        var crowdType = Symbol(
            "S1Api:Release:Type:Crowd.Target",
            "Type",
            "Crowd.Target",
            "Crowd.Target");
        var factoryType = Symbol(
            "S1Api:Release:Type:Demo.Factory",
            "Type",
            "Demo.Factory",
            "Demo.Factory");
        var factoryMethod = Symbol(
            "S1Api:Release:Method:Demo.Factory::Build()",
            "Method",
            "Demo.Factory.Build",
            "System.Void Demo.Factory::Build()");
        var factoryCtor = Symbol(
            "S1Api:Release:Constructor:Demo.Factory::.ctor()",
            "Constructor",
            "Demo.Factory.New",
            "System.Void Demo.Factory::.ctor()");

        await _repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(
                [
                    exactId,
                    canonical,
                    signature,
                    exactName,
                    lowerExactName,
                    uniqueText,
                    lowerText,
                    dealerA,
                    dealerB,
                    ..crowdMethods,
                    crowdType,
                    factoryType,
                    factoryMethod,
                    factoryCtor
                ],
                [],
                [],
                [],
                []),
            "2026-08-14T03:01:00Z",
            cancellationToken);

        return new ResolverFixture(
            indexId,
            codebase,
            channel,
            exactId,
            canonical,
            signature,
            exactName,
            uniqueText,
            dealerA,
            dealerB,
            crowdType,
            factoryMethod,
            factoryCtor);
    }

    private static string HashId(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
    }

    private sealed record ResolverFixture(
        string IndexId,
        CodebaseKind Codebase,
        CodeChannel Channel,
        IndexSymbolRecord ExactId,
        IndexSymbolRecord Canonical,
        IndexSymbolRecord Signature,
        IndexSymbolRecord ExactName,
        IndexSymbolRecord UniqueText,
        IndexSymbolRecord DealerA,
        IndexSymbolRecord DealerB,
        IndexSymbolRecord CrowdType,
        IndexSymbolRecord FactoryMethod,
        IndexSymbolRecord FactoryCtor);

    private sealed record PrefixFixture(
        string IndexId,
        string SoloId,
        string PairAId,
        string PairBId,
        string WidgetId);

    private sealed record SuggestionFixture(string IndexId);

    private sealed record TieFixture(string IndexId);

    private sealed record DuplicateSignatureFixture(string IndexId, string Signature);
}
