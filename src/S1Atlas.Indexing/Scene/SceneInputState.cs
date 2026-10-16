using S1Atlas.Core.Extraction;

namespace S1Atlas.Indexing.Scene;

/// <summary>
/// The scene index gate's verdict on a build's preferred extraction input.
/// Only <see cref="ReplayVerified"/> passes the gate.
/// </summary>
public enum SceneInputCertification
{
    /// <summary>The build has no preferred verified extraction.</summary>
    NoPreferredExtraction,

    /// <summary>The preferred extraction ran from live input that was never archived.</summary>
    NoInputSnapshot,

    /// <summary>The archived input snapshot the extraction names is missing or belongs to another build.</summary>
    InputSnapshotUnavailable,

    /// <summary>The archived input snapshot exists but no replay from it has been certified.</summary>
    NotReplayVerified,

    /// <summary>The archived input snapshot is replay-verified.</summary>
    ReplayVerified
}

/// <summary>
/// <see cref="InputSnapshot"/> is set for <see cref="SceneInputCertification.NotReplayVerified"/>
/// and <see cref="SceneInputCertification.ReplayVerified"/> only.
/// </summary>
public sealed record SceneInputState(
    SceneInputCertification Certification,
    InputSnapshot? InputSnapshot);
