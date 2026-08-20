using S1Atlas.Docs.Generation;
using S1Atlas.Docs.Determinism;
using S1Atlas.Docs.Identity;
using S1Atlas.Docs.Source;

namespace S1Atlas.Docs.Content;

public sealed class DerivedContextBuilder
{
    private readonly RoslynLearningConceptDetector _detector = new();
    private readonly DeterministicText _text = new();

    public DerivedContext Build(
        PortalSymbolModel symbol,
        PortalRelationshipEvidenceModel relationships,
        PortalSourceResult source,
        PortalLinkResolver links)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(relationships);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(links);
        var evidence = "#evidence";
        var overview = new List<DerivedStatement>
        {
            Derived($"{_text.FormatPlural(relationships.CallerTotal, "caller", "callers")} in this index.", evidence),
            Derived($"{_text.FormatPlural(relationships.CalleeTotal, "callee", "callees")} in this index.", evidence),
            Derived($"{_text.FormatPlural(relationships.ReferenceTotal, "reference", "references")} in this index.", evidence)
        };
        var relevance = new List<DerivedStatement>
        {
            Derived($"Modder relevance signal: {_text.FormatPlural(relationships.CallerTotal, "caller", "callers")} and {_text.FormatPlural(relationships.CalleeTotal, "callee", "callees")} are measured in this index.", evidence)
        };
        if (source.State is PortalSourceState.NoIndexedLocation or PortalSourceState.Unavailable or PortalSourceState.IntegrityFailure)
            relevance.Add(Derived(source.Label, evidence));

        var learning = new List<DerivedStatement>();
        if (source.Snippet is not null)
            foreach (var concept in _detector.Detect(source.Snippet.Text))
                learning.Add(Derived(concept.Label + " in the displayed source span.", evidence));
        return new DerivedContext(overview, relevance, learning);
    }

    private static DerivedStatement Derived(string text, string evidenceHref) =>
        new("DERIVED: " + text, evidenceHref);
}
