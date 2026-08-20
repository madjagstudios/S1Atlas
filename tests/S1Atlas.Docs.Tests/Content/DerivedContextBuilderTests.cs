using S1Atlas.Core.Indexing;
using S1Atlas.Docs.Content;
using S1Atlas.Docs.Generation;
using S1Atlas.Docs.Identity;
using Xunit;

namespace S1Atlas.Docs.Tests.Content;

public sealed class DerivedContextBuilderTests
{
    [Fact]
    public void Build_uses_true_totals_and_keeps_measured_zero_explicit()
    {
        var symbol = new PortalSymbolModel(
            "index-1", CodebaseKind.S1Api, CodeChannel.Release, "symbol-1",
            "S1Api:Release:Method:Demo.Worker::Run()", SymbolKind.Method,
            "Demo.Worker.Run", "void Run()", false, BodyRecoveryStatus.Recovered,
            "code/s1api/release/symbols/aa/run.html", "member-123456789012");
        var relationships = new PortalRelationshipEvidenceModel([], 0, [], 0, [], 0, "complete", "complete");
        var source = new PortalSourceResult(PortalSourceState.NoIndexedLocation, null, "source not indexed");

        var context = new DerivedContextBuilder().Build(symbol, relationships, source, new PortalLinkResolver());

        Assert.Contains(context.Overview, statement => statement.Text.Contains("0 callers", StringComparison.Ordinal));
        Assert.Contains(context.ModderRelevance, statement => statement.Text.Contains("0 callees", StringComparison.Ordinal));
        Assert.All(context.Overview.Concat(context.ModderRelevance), statement => Assert.Contains("#", statement.EvidenceHref, StringComparison.Ordinal));
    }
}
