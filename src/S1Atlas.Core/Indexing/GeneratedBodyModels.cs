namespace S1Atlas.Core.Indexing;

public enum GeneratedBodyKind
{
    Lambda,
    AsyncStateMachine,
    IteratorStateMachine,
    LocalFunction
}

public sealed record GeneratedBodyMapping(
    string? DeclaringKey,
    string Detail,
    bool IsAttributeBased);
