namespace S1Atlas.Core.Extraction;

public enum ExtractionFailureCode
{
    ToolNotInstalled,
    ToolDefinitionInvalid,
    ToolChecksumMismatch,
    ToolProbeFailed,
    BuildNotFound,
    LiveInputNotFound,
    BuildInputMismatch,
    ArchivedInputInvalid,
    ProcessStartFailed,
    ProcessTimedOut,
    ProcessExitNonZero,
    OperationCanceled,
    InputChangedDuringExtraction,
    OutputOutsideStaging,
    NoArtifactsProduced,
    NoManagedAssembliesProduced,
    EmptyArtifact,
    InvalidManagedAssembly,
    RequiredAssemblyMissing,
    DuplicateAssemblyIdentity,
    CatastrophicSanityDeviation,
    SameRecipeDifferentOutput,
    FilesystemPromotionFailed,
    DatabasePromotionFailed,
    InterruptedProcess,
    IntegrityMismatch,
    CustomToolPathInvalid,
    ExtractionAlreadyActive
}
