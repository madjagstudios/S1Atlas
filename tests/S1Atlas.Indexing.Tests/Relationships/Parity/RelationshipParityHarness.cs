using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Indexing.Authority;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Indexing.Query;
using S1Atlas.Indexing.Workflow;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;

namespace S1Atlas.Indexing.Tests.Relationships.Parity;

public static class RelationshipParityHarness
{
    public const string FixtureAssemblyName = "S1Atlas.ParityFixture.dll";
    public const string FixtureNamespace = "S1Atlas.ParityFixture";
    public const string OracleFileName = "expected.json";
    public const string BaselineFileName = "relationship-baseline.md";
    public const string ReportMarkdownFileName = "relationship-parity.md";
    public const string ReportJsonFileName = "relationship-parity.json";
    public const string BaselineHeaderSeparator = "---";

    private const int QueryLimit = 10000;

    private static readonly IReadOnlyDictionary<string, string> ReasonTickets =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["virtual dispatch"] = "AT-65",
            ["interface dispatch"] = "AT-65",
            ["lambda body"] = "AT-66",
            ["iterator body"] = "AT-66",
            ["async body"] = "AT-66",
            ["local function body"] = "AT-66",
            ["delegate creation"] = "AT-67",
            ["address taken"] = "AT-67"
        };

    public static async Task<ParityReport> RunAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var oracle = ParityOracle.Load(Path.Combine(
            repositoryRoot, "tests", "S1Atlas.Indexing.Tests", "Relationships", "Parity", OracleFileName));
        await using var index = await IndexFixtureAsync(cancellationToken);
        var service = new IndexQueryService(index.Repository);
        var symbolIds = await MapKindsAndNamesToIdsAsync(index.Repository, index.Run.IndexId, cancellationToken);

        var differences = new List<ParityDifference>();
        var matchedGaps = new HashSet<ParityKnownGap>();
        var consistencyNotes = new List<string>();

        foreach (var target in oracle.Targets)
        {
            var found = await QueryTargetAsync(service, index.Run, symbolIds[(KindFor(target.Target), target.Target)], cancellationToken);
            foreach (var relation in new[] { "callers", "callees", "readers", "writers" })
            {
                var expected = EdgesFor(target, relation);
                var actual = found[relation];
                foreach (var edge in expected)
                {
                    if (actual.Contains(edge.Symbol))
                    {
                        differences.Add(new ParityDifference(
                            target.Target, relation, edge.Symbol, ParityClassification.Found, edge.Reason, TicketFor(edge.Reason)));
                    }
                    else
                    {
                        differences.Add(new ParityDifference(
                            target.Target, relation, edge.Symbol, ParityClassification.Missing, edge.Reason, TicketFor(edge.Reason)));
                    }
                }

                foreach (var symbol in actual.Where(symbol => !expected.Any(edge => edge.Symbol == symbol)))
                {
                    var gap = target.KnownGaps.FirstOrDefault(gap =>
                        gap.Relation == relation && gap.Symbol == symbol);
                    if (gap is not null)
                    {
                        matchedGaps.Add(gap);
                        differences.Add(new ParityDifference(
                            target.Target, relation, symbol, ParityClassification.Mislabeled, gap.Reason, gap.Ticket));
                    }
                    else
                    {
                        differences.Add(new ParityDifference(
                            target.Target, relation, symbol, ParityClassification.Extra, ReasonForExtra(symbol), "unowned"));
                    }
                }
            }

            consistencyNotes.AddRange(await CheckRefsConsistencyAsync(
                service, index.Run, symbolIds[(KindFor(target.Target), target.Target)], found, cancellationToken));
        }

        var unmatchedGaps = oracle.Targets
            .SelectMany(target => target.KnownGaps.Select(gap => (target.Target, gap)))
            .Where(item => !matchedGaps.Contains(item.gap))
            .Select(item => new ParityKnownGapUnmatched(item.Target, item.gap.Relation, item.gap.Symbol, item.gap.Reason, item.gap.Ticket))
            .ToArray();

        return new ParityReport(differences, BuildTotals(differences), unmatchedGaps, consistencyNotes);
    }

    public static async Task<OwnedFixtureIndex> IndexFixtureAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-parity-" + Guid.NewGuid().ToString("N"));
        var extractionRoot = Path.Combine(root, "extraction");
        Directory.CreateDirectory(Path.Combine(extractionRoot, "reconstructed"));
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "parity-fixture", FixtureAssemblyName),
            Path.Combine(extractionRoot, "reconstructed", "Assembly-CSharp.dll"));

        var repository = new SqliteAtlasRepository(Path.Combine(root, "atlas.db"));
        var buildId = new string('a', 64);
        var extractionId = new string('b', 64);
        var authority = new PreferredVerifiedExtraction(
            buildId,
            new PreferredExtraction(buildId, extractionId, DateTimeOffset.UtcNow, ExtractionPreferenceReason.ManualPromotion),
            new ValidatedExtraction(extractionId, "recipe", buildId, "tool", "attempt", "profile", 1, "profile-digest", 1, 1, "manifest", extractionRoot, DateTimeOffset.UtcNow, ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, new ExtractionStatistics(0, 0, 1, 0, 0, 0, 0, 0, 0, 0, [])));

        try
        {
            await repository.InitializeAsync(cancellationToken);
            var workflow = new IndexingWorkflow(
                root,
                repository,
                (_, _) => Task.FromResult<PreferredVerifiedExtraction?>(authority),
                new ScheduleOneIndexSource(new IlSpyManagedDecompiler()));

            var result = await workflow.RunScheduleOneAsync(buildId, false, cancellationToken);
            var run = await repository.GetLatestCompletedIndexAsync(
                    CodebaseKind.ScheduleI, CodeChannel.Installed, null, cancellationToken)
                ?? throw new InvalidOperationException("The parity fixture index run was not recorded.");
            if (run.IndexId != result.IndexId)
                throw new InvalidOperationException("The latest completed index is not the parity fixture run.");

            return new OwnedFixtureIndex(root, repository, run);
        }
        catch
        {
            await TestDirectory.DeleteTreeAsync(root);
            throw;
        }
    }

    internal static async Task<Dictionary<(string Kind, string Name), string>> MapKindsAndNamesToIdsAsync(
        SqliteAtlasRepository repository,
        string indexId,
        CancellationToken cancellationToken)
    {
        var symbols = await repository.GetCompletedSymbolsAsync(indexId, cancellationToken);
        var map = new Dictionary<(string Kind, string Name), string>();
        foreach (var symbol in symbols)
        {
            map.TryAdd((symbol.Kind, symbol.QualifiedName), symbol.SymbolId);
        }

        return map;
    }

    internal static string KindFor(string target)
    {
        if (!target.Contains('('))
            return "Field";
        var separator = target.IndexOf("::", StringComparison.Ordinal);
        var paren = target.IndexOf('(', separator < 0 ? 0 : separator);
        var name = separator < 0 || paren < 0
            ? string.Empty
            : target[(separator + 2)..paren];
        return name is ".ctor" or ".cctor" ? "Constructor" : "Method";
    }

    private static IReadOnlyList<ParityEdge> EdgesFor(ParityTarget target, string relation) => relation switch
    {
        "callers" => target.Callers,
        "callees" => target.Callees,
        "readers" => target.Readers,
        "writers" => target.Writers,
        _ => throw new ArgumentOutOfRangeException(nameof(relation))
    };

    private static string TicketFor(string reason) =>
        ReasonTickets.TryGetValue(reason, out var ticket) ? ticket : "unowned";

    private static string ReasonForExtra(string symbol) =>
        symbol.StartsWith("0x", StringComparison.Ordinal) ? "unresolved token" : "unexpected edge";

    private static async Task<Dictionary<string, HashSet<string>>> QueryTargetAsync(
        IndexQueryService service,
        IndexRunRecord run,
        string selector,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["callers"] = [],
            ["callees"] = [],
            ["readers"] = [],
            ["writers"] = []
        };

        var callers = await service.CallersInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, QueryLimit, cancellationToken);
        foreach (var edge in callers.Relationships)
        {
            var symbol = IncomingSymbol(edge);
            if (symbol is not null)
                found["callers"].Add(symbol);
        }

        var callees = await service.CalleesInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, QueryLimit, cancellationToken);
        foreach (var edge in callees.Relationships)
        {
            var symbol = OutgoingSymbol(edge);
            if (symbol is not null)
                found["callees"].Add(symbol);
        }

        var fieldRefs = await service.FieldReferencesInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, QueryLimit,
            FieldReferenceFilter.All, cancellationToken);
        foreach (var edge in fieldRefs.Relationships)
        {
            var symbol = IncomingSymbol(edge);
            if (symbol is null)
                continue;
            if (edge.Kind == "ReadsField")
                found["readers"].Add(symbol);
            else if (edge.Kind == "WritesField")
                found["writers"].Add(symbol);
        }

        return found;
    }

    private static string? IncomingSymbol(RelationshipQueryResult edge)
    {
        if (edge.Source.Resolved && edge.Source.QualifiedName is not null)
            return edge.Source.QualifiedName;
        return null;
    }

    private static string? OutgoingSymbol(RelationshipQueryResult edge)
    {
        if (edge.Target.Resolved && edge.Target.QualifiedName is not null)
            return edge.Target.QualifiedName;
        var raw = edge.Target.RawText;
        if (raw is null)
            return null;
        if (raw.StartsWith(FixtureNamespace, StringComparison.Ordinal) || raw.StartsWith("0x", StringComparison.Ordinal))
            return raw;
        return null;
    }

    private static async Task<IReadOnlyList<string>> CheckRefsConsistencyAsync(
        IndexQueryService service,
        IndexRunRecord run,
        string selector,
        Dictionary<string, HashSet<string>> found,
        CancellationToken cancellationToken)
    {
        var refs = await service.RefsInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, QueryLimit, cancellationToken);
        var fromRefs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["callers"] = [],
            ["callees"] = [],
            ["readers"] = [],
            ["writers"] = []
        };
        foreach (var edge in refs.Relationships)
        {
            if (edge.Direction == "Incoming" && (edge.Kind == "Calls" || edge.Kind == "Constructs"))
            {
                var symbol = IncomingSymbol(edge);
                if (symbol is not null)
                    fromRefs["callers"].Add(symbol);
            }
            else if (edge.Direction == "Outgoing" && (edge.Kind == "Calls" || edge.Kind == "Constructs"))
            {
                var symbol = OutgoingSymbol(edge);
                if (symbol is not null)
                    fromRefs["callees"].Add(symbol);
            }
            else if (edge.Direction == "Incoming" && edge.Kind == "ReadsField")
            {
                var symbol = IncomingSymbol(edge);
                if (symbol is not null)
                    fromRefs["readers"].Add(symbol);
            }
            else if (edge.Direction == "Incoming" && edge.Kind == "WritesField")
            {
                var symbol = IncomingSymbol(edge);
                if (symbol is not null)
                    fromRefs["writers"].Add(symbol);
            }
        }

        var notes = new List<string>();
        foreach (var relation in fromRefs.Keys)
        {
            foreach (var symbol in fromRefs[relation].Where(symbol => !found[relation].Contains(symbol)))
            {
                notes.Add($"refs-only {relation} edge for {selector}: {symbol}");
            }

            foreach (var symbol in found[relation].Where(symbol => !fromRefs[relation].Contains(symbol)))
            {
                notes.Add($"dedicated-query-only {relation} edge for {selector}: {symbol}");
            }
        }

        return notes;
    }

    private static IReadOnlyList<ParityCategoryTotals> BuildTotals(IReadOnlyList<ParityDifference> differences)
    {
        var categories = differences
            .Select(item => item.Reason)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return categories
            .Select(category => new ParityCategoryTotals(
                category,
                differences.Count(item => item.Reason == category && item.Classification is ParityClassification.Found or ParityClassification.Missing),
                differences.Count(item => item.Reason == category && item.Classification == ParityClassification.Found),
                differences.Count(item => item.Reason == category && item.Classification == ParityClassification.Missing),
                differences.Count(item => item.Reason == category && item.Classification == ParityClassification.Extra),
                differences.Count(item => item.Reason == category && item.Classification == ParityClassification.Mislabeled)))
            .ToArray();
    }
}

public sealed class OwnedFixtureIndex : IAsyncDisposable
{
    public OwnedFixtureIndex(string root, SqliteAtlasRepository repository, IndexRunRecord run)
    {
        Root = root;
        Repository = repository;
        Run = run;
    }

    public string Root { get; }

    public SqliteAtlasRepository Repository { get; }

    public IndexRunRecord Run { get; }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(Root);
    }
}
