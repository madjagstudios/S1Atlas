using S1Atlas.Docs.Source;
using Xunit;

namespace S1Atlas.Docs.Tests.Source;

public sealed class RoslynLearningConceptDetectorTests
{
    [Fact]
    public void Detect_reports_syntax_properties_present_in_the_displayed_span()
    {
        const string source = """
            public void M<T>(T value)
            {
                var result = value?.ToString() ?? "none";
                var values = from item in items select item;
            }
            """;

        var concepts = new RoslynLearningConceptDetector().Detect(source);

        Assert.Contains(concepts, concept => concept.Label == "contains generic syntax");
        Assert.Contains(concepts, concept => concept.Label == "contains a null-conditional operator");
        Assert.Contains(concepts, concept => concept.Label == "contains a null-coalescing operator");
        Assert.Contains(concepts, concept => concept.Label == "contains a LINQ query expression");
    }

    [Fact]
    public void Detect_does_not_infer_lowered_or_absent_concepts()
    {
        var concepts = new RoslynLearningConceptDetector().Detect("var value = Enumerable.Select(items, item => item);");

        Assert.DoesNotContain(concepts, concept => concept.Label == "contains a LINQ query expression");
    }
}
