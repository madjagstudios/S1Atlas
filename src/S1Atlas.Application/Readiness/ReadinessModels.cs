using S1Atlas.Core.Indexing;

namespace S1Atlas.Application.Readiness;

public enum ReadinessState
{
    Ok,
    Missing,
    Stale,
    NotApplicable
}

public static class ReadinessItemIds
{
    public const string AtlasSchema = "atlas-schema";
    public const string DotNetRuntime = "dotnet-runtime";
    public const string GameInstall = "game-install";
    public const string Scan = "scan";
    public const string Tools = "tools";
    public const string Extraction = "extraction";
    public const string Index = "index";
    public const string Scene = "scene";
    public const string ApiIndex = "api-index";
    public const string ReferenceCollections = "reference-collections";
}

/// <summary>
/// One ordered readiness checklist entry. <see cref="FixCommand"/> is the
/// exact runnable command that satisfies the item, or null when no command
/// can satisfy it (the detail then carries the guidance).
/// </summary>
public sealed record ReadinessItem(
    string Id,
    string Title,
    ReadinessState State,
    string Detail,
    string? FixCommand,
    bool IsOptional);

public sealed record ReadinessNextStep(
    bool IsReady,
    string Summary,
    string? Command);

public sealed record ReadinessReport(
    IReadOnlyList<ReadinessItem> Items,
    ReadinessNextStep NextStep,
    bool IsReady,
    string ExampleQuery,
    IReadOnlyList<string> MissingRequiredToolIds);

public interface IAtlasReadinessService
{
    Task<ReadinessReport> EvaluateAsync(CancellationToken cancellationToken);
}

public sealed record DotNetRuntimeInfo(
    bool IsSupported,
    string Version,
    string Detail);

public interface IDotNetRuntimeProbe
{
    DotNetRuntimeInfo GetCurrent();
}

/// <summary>
/// Read-only view of the cached upstream commits left by
/// <c>upstream sync</c>. Implementations must not touch the network.
/// </summary>
public interface IUpstreamCommitCache
{
    IReadOnlyList<string> GetCachedCommits(CodebaseKind codebase);
}
