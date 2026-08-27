using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Decompilation;

public sealed class BodyRecoveryClassifier
{
    public BodyRecoveryStatus Classify(ManagedMethodBodyFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.NoBodyByDesign)
            return BodyRecoveryStatus.NoBodyByDesign;

        if (facts.MatchesInteropWrapperPattern)
            return BodyRecoveryStatus.StubOrUnavailable;

        if (!facts.HasPhysicalBody || facts.IlByteCount == 0 || facts.InstructionCount == 0 || facts.MatchesVerifiedStubPattern)
            return BodyRecoveryStatus.StubOrUnavailable;

        if (facts.RecoveredReferenceCount > 0)
            return BodyRecoveryStatus.Recovered;

        // Multiple decoded instructions and non-trivial byte content are affirmative
        // recovery evidence even when the method makes no calls or field references.
        if (facts.InstructionCount >= 3 && facts.IlByteCount >= 3)
            return BodyRecoveryStatus.Recovered;

        return BodyRecoveryStatus.Unknown;
    }
}
