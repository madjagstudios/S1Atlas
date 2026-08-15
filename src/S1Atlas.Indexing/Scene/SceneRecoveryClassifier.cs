using S1Atlas.Core.Scenes;

namespace S1Atlas.Indexing.Scene;

public readonly record struct SceneRecoveryFacts(
    bool Evaluated,
    bool ObjectReadable,
    bool GraphAvailable,
    bool SupportedFieldsAvailable,
    bool RequiredFieldsComplete);

public sealed class SceneRecoveryClassifier
{
    public SceneRecoveryStatus Classify(SceneRecoveryFacts facts)
    {
        if (!facts.Evaluated)
            return SceneRecoveryStatus.Unknown;

        if (!facts.ObjectReadable)
            return SceneRecoveryStatus.StubOrUnavailable;

        if (facts.GraphAvailable && !facts.SupportedFieldsAvailable)
            return SceneRecoveryStatus.GraphOnly;

        if (facts.GraphAvailable && facts.SupportedFieldsAvailable && facts.RequiredFieldsComplete)
            return SceneRecoveryStatus.FullyRecovered;

        return SceneRecoveryStatus.PartiallyRecovered;
    }
}
