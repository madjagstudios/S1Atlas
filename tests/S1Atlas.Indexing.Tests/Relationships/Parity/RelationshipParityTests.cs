using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;
using Xunit;

namespace S1Atlas.Indexing.Tests.Relationships.Parity;

public sealed class RelationshipParityTests
{
    private static readonly IReadOnlySet<string> KnownReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "direct call",
        "virtual dispatch",
        "interface dispatch",
        "constructed generic",
        "lambda body",
        "iterator body",
        "async body",
        "local function body",
        "delegate creation",
        "address taken",
        "field read",
        "field write",
        "constructs",
        "field initializer",
        "static constructor",
        "property accessor",
        "event accessor",
        "base call"
    };

    [Fact]
    public void Oracle_uses_only_known_reason_vocabulary()
    {
        var root = FindRepositoryRoot();
        var oracle = ParityOracle.Load(OraclePath(root));

        foreach (var target in oracle.Targets)
        {
            foreach (var edge in target.Callers.Concat(target.Callees).Concat(target.Readers).Concat(target.Writers))
            {
                Assert.Contains(edge.Reason, KnownReasons);
            }

            foreach (var gap in target.KnownGaps)
            {
                Assert.Contains(gap.Reason, KnownReasons);
            }
        }
    }

    [Fact]
    public async Task Oracle_symbols_all_exist_in_index()
    {
        var root = FindRepositoryRoot();
        var oracle = ParityOracle.Load(OraclePath(root));
        await using var index = await RelationshipParityHarness.IndexFixtureAsync(TestContext.Current.CancellationToken);
        var symbolIds = await RelationshipParityHarness.MapKindsAndNamesToIdsAsync(
            index.Repository, index.Run.IndexId, TestContext.Current.CancellationToken);

        foreach (var target in oracle.Targets)
        {
            AssertHas(target.Target, "target");
            foreach (var edge in target.Callers.Concat(target.Callees).Concat(target.Readers).Concat(target.Writers))
            {
                AssertHas(edge.Symbol, "edge symbol");
            }

            foreach (var gap in target.KnownGaps)
            {
                AssertHas(gap.Symbol, "gap symbol");
            }

            foreach (var recorded in target.Overriders.Concat(target.Implementers))
            {
                if (!recorded.EndsWith(" (inherited)", StringComparison.Ordinal))
                    AssertHas(recorded, "recorded symbol");
            }

            void AssertHas(string symbol, string what) =>
                Assert.True(
                    symbolIds.ContainsKey((RelationshipParityHarness.KindFor(symbol), symbol)),
                    $"Oracle {what} missing from index: {symbol}");
        }
    }

    [Fact]
    public async Task Report_matches_committed_baseline()
    {
        var root = FindRepositoryRoot();
        var report = await RelationshipParityHarness.RunAsync(root, TestContext.Current.CancellationToken);
        var markdown = report.ToMarkdown();
        Directory.CreateDirectory(Path.Combine(root, "TestResults"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "TestResults", RelationshipParityHarness.ReportMarkdownFileName),
            markdown,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(root, "TestResults", RelationshipParityHarness.ReportJsonFileName),
            report.ToJson(),
            TestContext.Current.CancellationToken);

        var baseline = await File.ReadAllTextAsync(
            Path.Combine(root, "docs", "parity", RelationshipParityHarness.BaselineFileName),
            TestContext.Current.CancellationToken);
        Assert.Equal(SplitHeader(baseline).ReplaceLineEndings("\n"), markdown);
    }

    private static string OraclePath(string repositoryRoot) => Path.Combine(
        repositoryRoot, "tests", "S1Atlas.Indexing.Tests", "Relationships", "Parity",
        RelationshipParityHarness.OracleFileName);

    private static string SplitHeader(string baseline)
    {
        var lines = baseline.ReplaceLineEndings("\n").Split('\n');
        var separator = Array.FindIndex(lines, line => line == RelationshipParityHarness.BaselineHeaderSeparator);
        if (separator < 0)
            throw new InvalidOperationException("The parity baseline has no header separator.");
        return string.Join("\n", lines[(separator + 1)..]).TrimStart('\n');
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "S1Atlas.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
