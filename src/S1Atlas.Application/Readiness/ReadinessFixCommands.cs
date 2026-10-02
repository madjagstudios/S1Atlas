using S1Atlas.Application.Authority;
using S1Atlas.Core.Indexing;

namespace S1Atlas.Application.Readiness;

/// <summary>
/// Single home of every fix command the readiness checklist, the authority
/// hints, and setup print, so doctor and the setup errors cannot drift
/// apart. A hint is an exact runnable command, or null when the fix needs
/// input only the operator can provide.
/// </summary>
public static class ReadinessFixCommands
{
    public const string Scan = "s1atlas scan";
    public const string Builds = "s1atlas builds";
    public const string Extract = "s1atlas extract";
    public const string ExtractRetry = "s1atlas extract --retry";
    public const string Index = "s1atlas index";
    public const string IndexForce = "s1atlas index --force";
    public const string IndexScene = "s1atlas index --scene";
    public const string Status = "s1atlas status";
    public const string ExampleQuery = "s1atlas search \"Player\" --limit 20";

    public const string Cpp2IlToolId = "cpp2il";
    public const string UnityClassDataToolId = "unity-classdata";

    public static string InstallTool(string toolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        return $"s1atlas tools install {toolId}";
    }

    public static string IndexApiInstalled(CodebaseKind codebase) =>
        $"s1atlas index --codebase {ApiCodebaseName(codebase)} --channel installed";

    public static string IndexApiCommit(
        CodebaseKind codebase,
        CodeChannel channel,
        string commitSha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        if (channel is not (CodeChannel.Release or CodeChannel.Preview))
        {
            throw new ArgumentException(
                "A commit-scoped API index command requires the release or preview channel.",
                nameof(channel));
        }

        return $"s1atlas index --codebase {ApiCodebaseName(codebase)} --channel {channel.ToString().ToLowerInvariant()} --commit {commitSha}";
    }

    public static string? HintForAuthorityStatus(InstalledBuildAuthorityStatus status) =>
        status switch
        {
            InstalledBuildAuthorityStatus.Resolved => null,
            InstalledBuildAuthorityStatus.NoCurrentBuild => Scan,
            InstalledBuildAuthorityStatus.BuildNotFound => Builds,
            InstalledBuildAuthorityStatus.AmbiguousBuildPrefix => null,
            InstalledBuildAuthorityStatus.NoPreferredVerifiedExtraction => Extract,
            InstalledBuildAuthorityStatus.ExtractionIntegrityFailure => ExtractRetry,
            InstalledBuildAuthorityStatus.NoCompletedIndex => Index,
            InstalledBuildAuthorityStatus.IndexBuildMismatch => null,
            _ => null
        };

    private static string ApiCodebaseName(CodebaseKind codebase) =>
        codebase switch
        {
            CodebaseKind.S1Api => "s1api",
            CodebaseKind.S1MApi => "s1mapi",
            _ => throw new ArgumentException(
                "API index commands require the s1api or s1mapi codebase.",
                nameof(codebase))
        };
}
