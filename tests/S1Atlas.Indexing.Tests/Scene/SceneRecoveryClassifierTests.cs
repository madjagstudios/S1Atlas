using S1Atlas.Core.Scenes;
using S1Atlas.Indexing.Scene;
using Xunit;

namespace S1Atlas.Indexing.Tests.Scene;

public sealed class SceneRecoveryClassifierTests
{
    private readonly SceneRecoveryClassifier _classifier = new();

    [Fact]
    public void Unevaluated_facts_are_unknown()
    {
        Assert.Equal(
            SceneRecoveryStatus.Unknown,
            _classifier.Classify(new SceneRecoveryFacts(false, false, false, false, false)));
    }

    [Fact]
    public void Unreadable_or_unsupported_object_is_stub_or_unavailable()
    {
        Assert.Equal(
            SceneRecoveryStatus.StubOrUnavailable,
            _classifier.Classify(new SceneRecoveryFacts(true, false, false, false, false)));
    }

    [Fact]
    public void Recovered_graph_without_supported_fields_is_graph_only()
    {
        Assert.Equal(
            SceneRecoveryStatus.GraphOnly,
            _classifier.Classify(new SceneRecoveryFacts(true, true, true, false, false)));
    }

    [Fact]
    public void Supported_but_incomplete_fields_are_partially_recovered()
    {
        Assert.Equal(
            SceneRecoveryStatus.PartiallyRecovered,
            _classifier.Classify(new SceneRecoveryFacts(true, true, true, true, false)));
    }

    [Fact]
    public void Complete_required_graph_and_fields_are_fully_recovered()
    {
        Assert.Equal(
            SceneRecoveryStatus.FullyRecovered,
            _classifier.Classify(new SceneRecoveryFacts(true, true, true, true, true)));
    }
}
