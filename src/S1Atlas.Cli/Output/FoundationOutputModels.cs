using System.Text.Json.Serialization;
using S1Atlas.Application.Readiness;

namespace S1Atlas.Cli.Output;

internal sealed record StatusOutput(
    bool HasCurrentBuild,
    string? BuildId,
    string? ExecutableVersion,
    string? SteamAppId,
    string? SteamBuildId,
    DateTimeOffset? CapturedAtUtc,
    int InstalledDependencyCount,
    int DependencyCount,
    StatusReadinessOutput Readiness);

internal sealed record StatusReadinessOutput(
    bool IsReady,
    string Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? NextCommand)
{
    public static StatusReadinessOutput FromReport(ReadinessReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new StatusReadinessOutput(
            report.IsReady,
            report.NextStep.Summary,
            report.NextStep.Command);
    }
}

internal sealed record EnvironmentOutput(
    string BuildId,
    string? ExecutableVersion,
    string? SteamAppId,
    string? SteamBuildId,
    string? InstallationRoot,
    string? GameAssemblyPath,
    string? GlobalMetadataPath,
    IReadOnlyList<DependencyOutput> Dependencies);

internal sealed record DependencyOutput(
    string Kind,
    string? Version,
    string? Path,
    bool IsInstalled);

internal sealed record BuildsOutput(
    IReadOnlyList<BuildOutput> Builds);

internal sealed record BuildOutput(
    string BuildId,
    DateTimeOffset FirstSeenAtUtc,
    bool IsValid);
