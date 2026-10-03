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
    IReadOnlyList<ManagedMemberFacts> Members,
    bool IsInterface = false,
    bool IsCompilerGenerated = false);

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
    bool IsPublic = false,
    bool IsVirtual = false,
    bool IsNewSlot = false,
    IReadOnlyList<string>? MethodImplDeclarations = null,
    bool IsCompilerGenerated = false,
    string? StateMachineTypeName = null,
    bool IsAsyncStateMachine = false,
    IReadOnlyList<ManagedPatchFact>? Patches = null)
{
    public IReadOnlyList<string> ParameterTypesOrEmpty => ParameterTypes ?? [];
    public IReadOnlyList<string> MethodImplDeclarationsOrEmpty => MethodImplDeclarations ?? [];
    public IReadOnlyList<ManagedPatchFact> PatchesOrEmpty => Patches ?? [];
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
    string Target,
    RelationshipEvidence Evidence = RelationshipEvidence.RecoveredIL);

public enum HarmonyPatchKind
{
    Prefix,
    Postfix,
    Transpiler,
    Finalizer
}

public static class HarmonyPatchReasons
{
    public const string RuntimeComputedTarget = "runtime-computed-target";
    public const string NoTargetSpecified = "no-target-specified";
    public const string UnknownDeclaringType = "unknown-declaring-type";
    public const string UnknownMemberName = "unknown-member-name";
    public const string UnsupportedMethodType = "unsupported-method-type";
    public const string NoMatchingOverload = "no-matching-overload";
    public const string AmbiguousOverload = "ambiguous-overload";
    public const string AmbiguousPatchMethod = "ambiguous-patch-method";
    public const string UnknownPatchMethod = "unknown-patch-method";
    public const string NonConstantArguments = "non-constant-arguments";
    public const string NonConstantTarget = "non-constant-target";
    public const string UnrecognizedManualShape = "unrecognized-manual-shape";

    public static string Qualify(string reason, string text) => $"unresolved:{reason}:{text}";
}

/// <summary>
/// One Harmony patch declaration found in a reference-mod member: the patch kind, a
/// canonical target attempt ("Type::Member" or "Type::Member(params)", without a
/// return type), and either a complete attempt or an unresolved reason. Manual
/// patches name their patch method explicitly, since the containing member only hosts
/// the call; attribute patches patch from the containing member itself.
/// </summary>
public sealed record ManagedPatchFact(
    HarmonyPatchKind Kind,
    string? TargetSignature,
    string? Reason,
    RelationshipEvidence Evidence,
    string? PatchMethodType = null,
    string? PatchMethodName = null,
    IReadOnlyList<string>? PatchMethodArgumentTypes = null);

public enum ManagedReferenceKind
{
    Calls,
    CallsVirtual,
    Constructs,
    ReadsField,
    WritesField,
    ReferencesMethod,
    TakesFieldAddress
}
