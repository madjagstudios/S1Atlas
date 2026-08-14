using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Decompilation;
using Xunit;

namespace S1Atlas.Indexing.Tests.Decompilation;

public sealed class BodyRecoveryClassifierTests
{
    private readonly BodyRecoveryClassifier _classifier = new();

    [Fact]
    public void Intentional_missing_body_is_no_body_by_design()
    {
        Assert.Equal(
            BodyRecoveryStatus.NoBodyByDesign,
            _classifier.Classify(new ManagedMethodBodyFacts(false, true, 0, 0, 0, false)));
    }

    [Fact]
    public void Concrete_missing_or_empty_body_is_stub_or_unavailable()
    {
        Assert.Equal(
            BodyRecoveryStatus.StubOrUnavailable,
            _classifier.Classify(new ManagedMethodBodyFacts(false, false, 0, 0, 0, false)));
        Assert.Equal(
            BodyRecoveryStatus.StubOrUnavailable,
            _classifier.Classify(new ManagedMethodBodyFacts(true, false, 0, 0, 0, false)));
    }

    [Fact]
    public void Explicit_verified_stub_pattern_is_stub_or_unavailable()
    {
        Assert.Equal(
            BodyRecoveryStatus.StubOrUnavailable,
            _classifier.Classify(new ManagedMethodBodyFacts(true, false, 8, 3, 1, true)));
    }

    [Fact]
    public void Nontrivial_zero_reference_il_can_be_recovered()
    {
        Assert.Equal(
            BodyRecoveryStatus.Recovered,
            _classifier.Classify(new ManagedMethodBodyFacts(true, false, 8, 3, 0, false)));
    }

    [Fact]
    public void Recovered_reference_is_affirmative_recovery_evidence()
    {
        Assert.Equal(
            BodyRecoveryStatus.Recovered,
            _classifier.Classify(new ManagedMethodBodyFacts(true, false, 5, 2, 1, false)));
    }

    [Fact]
    public void Ambiguous_trivial_physical_body_is_unknown_not_false_confidence()
    {
        Assert.Equal(
            BodyRecoveryStatus.Unknown,
            _classifier.Classify(new ManagedMethodBodyFacts(true, false, 1, 1, 0, false)));
    }
}
