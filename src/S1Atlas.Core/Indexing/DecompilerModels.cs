namespace S1Atlas.Core.Indexing;

public sealed record ManagedDecompilation(
    string AssemblyPath,
    string SourceText,
    IReadOnlyList<ManagedTypeFacts> Types);

public sealed record ManagedTypeFacts(
    string FullName,
    string Namespace,
    string Name,
    string? BaseType,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<ManagedMemberFacts> Members);

public sealed record ManagedMemberFacts(
    string Name,
    ManagedMemberKind Kind,
    string Signature,
    bool HasBody,
    IReadOnlyList<ManagedReferenceFact> References,
    IReadOnlyList<string>? ParameterTypes = null,
    string? ReturnType = null,
    string? ValueType = null,
    int GenericParameterCount = 0,
    ManagedMethodBodyFacts? BodyFacts = null,
    BodyRecoveryStatus? BodyRecoveryStatus = null,
    bool IsPublic = false)
{
    public IReadOnlyList<string> ParameterTypesOrEmpty => ParameterTypes ?? [];
}

public sealed record ManagedMethodBodyFacts(
    bool HasPhysicalBody,
    bool NoBodyByDesign,
    int IlByteCount,
    int InstructionCount,
    int RecoveredReferenceCount,
    bool MatchesVerifiedStubPattern,
    bool MatchesInteropWrapperPattern = false);

public enum BodyRecoveryStatus
{
    NoBodyByDesign,
    Recovered,
    StubOrUnavailable,
    Unknown
}

public enum ManagedMemberKind
{
    Constructor,
    Method,
    Field,
    Property,
    Event
}

public sealed record ManagedReferenceFact(
    ManagedReferenceKind Kind,
    string Target);

public enum ManagedReferenceKind
{
    Calls,
    Constructs,
    ReadsField,
    WritesField
}
