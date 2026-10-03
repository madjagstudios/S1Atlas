namespace S1Atlas.Core.Indexing;

public enum RelationshipKind
{
    Inherits,
    ImplementsInterface,
    FieldType,
    PropertyType,
    EventType,
    ParameterType,
    ReturnType,
    Calls,
    CallsVirtual,
    Constructs,
    ReadsField,
    WritesField,
    Overrides,
    ImplementsMethod,
    ReferencesMethod,
    TakesFieldAddress,
    Patches
}

public enum RelationshipEvidence
{
    Metadata,
    RecoveredIL,
    UpstreamSource
}

public sealed record RelationshipFact(
    string SourceKey,
    string? TargetKey,
    string? TargetText,
    RelationshipKind Kind,
    RelationshipEvidence Evidence,
    string? GeneratedSourceKey = null,
    string? GeneratedDetail = null);
