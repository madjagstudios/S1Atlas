using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class RuntimeVerificationClassifierTests
{
    [Theory]
    [InlineData("Physics", RuntimeVerificationSignal.Physics)]
    [InlineData("Rigidbody", RuntimeVerificationSignal.Physics)]
    [InlineData("Rigidbody2D", RuntimeVerificationSignal.Physics)]
    [InlineData("Collider", RuntimeVerificationSignal.Physics)]
    [InlineData("Collider2D", RuntimeVerificationSignal.Physics)]
    [InlineData("NavMesh", RuntimeVerificationSignal.NavMesh)]
    [InlineData("NavMeshAgent", RuntimeVerificationSignal.NavMesh)]
    [InlineData("OffMeshLink", RuntimeVerificationSignal.NavMesh)]
    [InlineData("NavMeshPath", RuntimeVerificationSignal.NavMesh)]
    [InlineData("OnTrigger", RuntimeVerificationSignal.TriggerState)]
    [InlineData("OnCollision", RuntimeVerificationSignal.TriggerState)]
    [InlineData("isTrigger", RuntimeVerificationSignal.TriggerState)]
    [InlineData("OverlapSphere", RuntimeVerificationSignal.TriggerState)]
    [InlineData("OverlapBox", RuntimeVerificationSignal.TriggerState)]
    [InlineData("OverlapCapsule", RuntimeVerificationSignal.TriggerState)]
    [InlineData("ComputePenetration", RuntimeVerificationSignal.TriggerState)]
    public void Classify_returns_the_expected_signal_for_each_exact_token(
        string token,
        RuntimeVerificationSignal expected)
    {
        var result = RuntimeVerificationClassifier.Classify($"void Run() {{ var value = {token}; }}", "Run():System.Void");

        Assert.NotNull(result);
        Assert.Contains(expected, result!.Signals);
    }

    [Theory]
    [InlineData("Metaphysics")]
    [InlineData("IsTriggered")]
    [InlineData("NavMeshable")]
    [InlineData("ColliderProxy")]
    public void Classify_rejects_identifier_substrings(string token)
    {
        var result = RuntimeVerificationClassifier.Classify($"void Run() {{ var value = {token}; }}", "Run():System.Void");

        Assert.Null(result);
    }

    [Fact]
    public void Classify_can_detect_a_signal_in_the_canonical_signature()
    {
        var result = RuntimeVerificationClassifier.Classify("void Run() { }", "Physics.World::Run():System.Void");

        Assert.Equal([RuntimeVerificationSignal.Physics], result?.Signals);
    }
}
