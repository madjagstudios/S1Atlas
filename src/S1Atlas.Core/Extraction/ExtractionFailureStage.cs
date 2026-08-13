namespace S1Atlas.Core.Extraction;

public enum ExtractionFailureStage
{
    ToolResolution,
    InputResolution,
    PreRunInputVerification,
    InputSnapshotCreation,
    AttemptPersistence,
    ProcessStart,
    ProcessExecution,
    PostRunInputVerification,
    OutputContainment,
    ArtifactValidation,
    AssemblyValidation,
    SanityValidation,
    ReproducibilityValidation,
    FilesystemPromotion,
    DatabasePromotion,
    Recovery
}
