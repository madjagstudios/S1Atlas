using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using S1Atlas.Application.Authority;
using S1Atlas.Application.Envelope;
using S1Atlas.Application.Readiness;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Mcp.Mapping;

namespace S1Atlas.Mcp.Tools;

[McpServerToolType]
public sealed class BuildEnvironmentTools
{
    private readonly McpReadOnlyServices _services;

    public BuildEnvironmentTools(McpReadOnlyServices services)
    {
        _services = services;
    }

    [McpServerTool(Name = "list_builds", Title = "List builds", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List indexed Schedule I Installed builds with extraction and index availability.")]
    public Task<ToolEnvelope<BuildListResult>> ListBuildsAsync(
        [Description("Maximum builds to return (1-500).")] int limit = 50,
        CancellationToken ct = default,
        [Description("Cursor for the next page; reuse arguments verbatim.")] string? cursor = null) =>
        EnvelopeMapper.WithAtlasAvailabilityAsync(() => ListBuildsCoreAsync(limit, cursor, ct));

    private async Task<ToolEnvelope<BuildListResult>> ListBuildsCoreAsync(
        int limit,
        string? cursor,
        CancellationToken ct)
    {
        if (limit <= 0)
        {
            return ToolEnvelope<BuildListResult>.Invalid(
                new ToolError("InvalidLimit", "The build result limit must be positive."));
        }

        var boundedLimit = Math.Min(limit, 500);
        if (!CodeSymbolTools.ToolArguments.TryDecodeCursor<BuildListResult>(cursor, null, out var cursorHash, out var offset, out var cursorError))
        {
            return cursorError;
        }

        var current = await _services.Repository.GetCurrentSnapshotAsync(ct);
        var builds = await _services.Repository.ListBuildsAsync(ct);
        var (window, hasMore) = IndexPaging.TakePage(builds, offset, boundedLimit);
        var items = new List<BuildListItem>(window.Count);

        foreach (var build in window)
        {
            var preferred = await _services.AuthorityResolver.ResolvePreferredExtractionAsync(build.BuildId, ct);
            var authority = await _services.AuthorityResolver.ResolveAsync(build.BuildId, ct);
            items.Add(new BuildListItem(
                build.BuildId,
                build.FirstSeenAtUtc,
                build.IsValid,
                string.Equals(current?.Build.BuildId, build.BuildId, StringComparison.Ordinal),
                preferred is not null &&
                    string.Equals(preferred.Extraction.BuildId, build.BuildId, StringComparison.Ordinal),
                authority.Status == InstalledBuildAuthorityStatus.Resolved));
        }

        var expectedHash = CodeSymbolTools.ToolArguments.CursorHashFor(
            "list_builds", current?.Build.BuildId, builds.Count.ToString(), boundedLimit.ToString());
        if (!CodeSymbolTools.ToolArguments.VerifyCursorHash<BuildListResult>(cursorHash, expectedHash, null, out var hashError))
        {
            return hashError;
        }

        var nextCursor = McpPageCursor.MintNextCursor(hasMore, expectedHash, offset, boundedLimit);
        return ToolEnvelope<BuildListResult>.Resolved(
            null,
            new BuildListResult(items, nextCursor, hasMore),
            new ProvenanceEntry(ProvenanceClassification.Fact, "atlas-build-list", null, null, null),
            new ProvenanceEntry(ProvenanceClassification.Derived, "installed-build-availability", null, null, null));
    }

    [McpServerTool(Name = "get_environment", Title = "Get environment", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Return verified environment facts for the current build.")]
    public Task<ToolEnvelope<EnvironmentFacts>> GetEnvironmentAsync(
        [Description("Optional build ID; only the current environment snapshot can be returned.")] string? buildId = null,
        CancellationToken ct = default) =>
        EnvelopeMapper.WithAtlasAvailabilityAsync(() => GetEnvironmentCoreAsync(buildId, ct));

    private async Task<ToolEnvelope<EnvironmentFacts>> GetEnvironmentCoreAsync(
        string? buildId,
        CancellationToken ct)
    {
        var snapshot = await _services.Repository.GetCurrentSnapshotAsync(ct);
        if (snapshot is null)
        {
            return ToolEnvelope<EnvironmentFacts>.Unavailable(
                new ToolError("NoCurrentBuild", "No current environment snapshot is available.", ReadinessFixCommands.Scan));
        }

        if (!string.IsNullOrWhiteSpace(buildId) &&
            !string.Equals(buildId, snapshot.Build.BuildId, StringComparison.Ordinal))
        {
            return ToolEnvelope<EnvironmentFacts>.Unavailable(
                new ToolError(
                    "NoMatchingEnvironmentSnapshot",
                    "Environment facts are available only for the current environment snapshot."),
                new BuildContext(buildId, null, null, null, "ScheduleI", "Installed", false));
        }

        var authority = await _services.AuthorityResolver.ResolveAsync(snapshot.Build.BuildId, ct);
        if (authority.Status != InstalledBuildAuthorityStatus.Resolved)
        {
            return AuthorityEnvelope.From<EnvironmentFacts>(authority);
        }

        return ToolEnvelope<EnvironmentFacts>.Resolved(
            EnvelopeMapper.BuildFrom(authority),
            EnvironmentFacts.From(snapshot),
            new ProvenanceEntry(
                ProvenanceClassification.Fact,
                "current-environment-snapshot",
                snapshot.Build.BuildId,
                authority.ExtractionId,
                authority.IndexId));
    }
}

public sealed record BuildListResult(
    IReadOnlyList<BuildListItem> Builds,
    string? NextCursor = null,
    [property: JsonIgnore] bool HasMore = false);

public sealed record BuildListItem(
    string BuildId,
    DateTimeOffset FirstSeenAtUtc,
    bool IsValid,
    bool IsCurrent,
    bool HasPreferredVerifiedExtraction,
    bool HasCompletedIndex);

public sealed record EnvironmentFacts(
    string BuildId,
    string? ExecutableVersion,
    string? SteamAppId,
    string? SteamBuildId,
    string? InstallationRoot,
    string? GameAssemblyPath,
    string? GlobalMetadataPath,
    IReadOnlyList<EnvironmentDependency> Dependencies)
{
    public static EnvironmentFacts From(EnvironmentSnapshot snapshot) =>
        new(
            snapshot.Build.BuildId,
            snapshot.Installation.ExecutableVersion,
            snapshot.Installation.SteamAppId,
            snapshot.Installation.SteamBuildId,
            snapshot.Installation.InstallationRoot,
            snapshot.Installation.GameAssemblyPath,
            snapshot.Installation.GlobalMetadataPath,
            snapshot.Dependencies
                .Select(dependency => new EnvironmentDependency(
                    dependency.Kind.ToString(),
                    dependency.Version,
                    dependency.Path,
                    dependency.IsInstalled))
                .ToArray());
}

public sealed record EnvironmentDependency(
    string Kind,
    string? Version,
    string? Path,
    bool IsInstalled);
