using S1Atlas.Core;
using S1Atlas.Core.Discovery;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Indexing.Authority;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.Application.Readiness;

/// <summary>
/// Computes the ordered atlas readiness checklist. Read-only: it never
/// writes to the atlas, runs migrations, touches the network, or starts
/// long work. Repository reads run only when the schema inspector reports
/// a current schema, so an empty or outdated data root is safe to probe.
/// The scan comparison uses cheap signals only (Steam build ID, executable
/// version, install root, file size and modified time) and never hashes
/// game files.
/// </summary>
public sealed class AtlasReadinessService : IAtlasReadinessService
{
    private static readonly string[] RequiredToolIds =
    [
        ReadinessFixCommands.Cpp2IlToolId,
        ReadinessFixCommands.UnityClassDataToolId
    ];

    private static readonly CodebaseKind[] ApiCodebases =
        [CodebaseKind.S1Api, CodebaseKind.S1MApi];

    private static readonly CodeChannel[] UpstreamChannels =
        [CodeChannel.Release, CodeChannel.Preview];

    private readonly IAtlasSchemaInspector _schemaInspector;
    private readonly IDotNetRuntimeProbe _runtimeProbe;
    private readonly IScheduleOneLocator _gameLocator;
    private readonly IInstallationMetadataReader _installationMetadataReader;
    private readonly IAtlasRepository _atlasRepository;
    private readonly IValidatedExtractionRepository _validatedRepository;
    private readonly PreferredVerifiedExtractionResolver _preferredResolver;
    private readonly IIndexRepository _indexRepository;
    private readonly IManagedToolStatusReader _toolStatusReader;
    private readonly IUpstreamCommitCache _upstreamCache;

    public AtlasReadinessService(
        IAtlasSchemaInspector schemaInspector,
        IDotNetRuntimeProbe runtimeProbe,
        IScheduleOneLocator gameLocator,
        IInstallationMetadataReader installationMetadataReader,
        IAtlasRepository atlasRepository,
        IValidatedExtractionRepository validatedRepository,
        PreferredVerifiedExtractionResolver preferredResolver,
        IIndexRepository indexRepository,
        IManagedToolStatusReader toolStatusReader,
        IUpstreamCommitCache upstreamCache)
    {
        _schemaInspector = schemaInspector ?? throw new ArgumentNullException(nameof(schemaInspector));
        _runtimeProbe = runtimeProbe ?? throw new ArgumentNullException(nameof(runtimeProbe));
        _gameLocator = gameLocator ?? throw new ArgumentNullException(nameof(gameLocator));
        _installationMetadataReader = installationMetadataReader ?? throw new ArgumentNullException(nameof(installationMetadataReader));
        _atlasRepository = atlasRepository ?? throw new ArgumentNullException(nameof(atlasRepository));
        _validatedRepository = validatedRepository ?? throw new ArgumentNullException(nameof(validatedRepository));
        _preferredResolver = preferredResolver ?? throw new ArgumentNullException(nameof(preferredResolver));
        _indexRepository = indexRepository ?? throw new ArgumentNullException(nameof(indexRepository));
        _toolStatusReader = toolStatusReader ?? throw new ArgumentNullException(nameof(toolStatusReader));
        _upstreamCache = upstreamCache ?? throw new ArgumentNullException(nameof(upstreamCache));
    }

    public async Task<ReadinessReport> EvaluateAsync(CancellationToken cancellationToken)
    {
        var schema = await _schemaInspector.GetStatusAsync(cancellationToken);
        var databaseUsable = schema.Kind == AtlasSchemaStatusKind.Current;
        var databaseMissing = schema.Kind == AtlasSchemaStatusKind.NotCreated;

        var runtime = _runtimeProbe.GetCurrent();
        var install = await _gameLocator.LocateAsync(null, cancellationToken);

        EnvironmentSnapshot? snapshot = null;
        if (databaseUsable)
        {
            snapshot = await _atlasRepository.GetCurrentSnapshotAsync(cancellationToken);
        }

        var scanItem = await ScanItemAsync(
            snapshot, install, databaseUsable, databaseMissing, cancellationToken);
        var (toolsItem, missingToolIds) = await ToolsItemAsync(cancellationToken);

        string? verifiedExtractionId = null;
        ReadinessItem extractionItem;
        if (!databaseUsable && !databaseMissing)
        {
            extractionItem = DatabaseBlockedItem(
                ReadinessItemIds.Extraction, "Preferred extraction", ReadinessFixCommands.Extract);
        }
        else if (snapshot is null)
        {
            extractionItem = new ReadinessItem(
                ReadinessItemIds.Extraction,
                "Preferred extraction",
                ReadinessState.NotApplicable,
                "Needs a scanned build first.",
                ReadinessFixCommands.Extract,
                false);
        }
        else
        {
            var preferred = await _preferredResolver.ResolveAsync(
                snapshot.Build.BuildId, cancellationToken);
            if (preferred is not null)
            {
                verifiedExtractionId = preferred.Extraction.ExtractionId;
                extractionItem = new ReadinessItem(
                    ReadinessItemIds.Extraction,
                    "Preferred extraction",
                    ReadinessState.Ok,
                    $"Preferred extraction {ShortId.Display(verifiedExtractionId)} is verified.",
                    null,
                    false);
            }
            else
            {
                var preference = await _validatedRepository.GetPreferredExtractionAsync(
                    snapshot.Build.BuildId, cancellationToken);
                extractionItem = preference is null
                    ? new ReadinessItem(
                        ReadinessItemIds.Extraction,
                        "Preferred extraction",
                        ReadinessState.Missing,
                        "No preferred verified extraction exists for the build.",
                        ReadinessFixCommands.Extract,
                        false)
                    : new ReadinessItem(
                        ReadinessItemIds.Extraction,
                        "Preferred extraction",
                        ReadinessState.Missing,
                        $"Preferred extraction {ShortId.Display(preference.ExtractionId)} failed integrity " +
                        "verification. No command rebuilds it in place: promote another validated " +
                        "extraction with 's1atlas extractions promote <extraction-id>', or delete the " +
                        "atlas data and run 's1atlas setup' again.",
                        null,
                        false);
            }
        }

        var indexItem = await IndexItemAsync(
            snapshot, verifiedExtractionId, databaseUsable, databaseMissing, cancellationToken);
        var sceneItem = await SceneItemAsync(
            snapshot, databaseUsable, databaseMissing, cancellationToken);
        var apiItem = await ApiItemAsync(
            snapshot, databaseUsable, databaseMissing, cancellationToken);
        var referenceItem = await ReferenceItemAsync(
            snapshot, databaseUsable, databaseMissing, cancellationToken);

        var items = new ReadinessItem[]
        {
            SchemaItem(schema),
            RuntimeItem(runtime),
            GameItem(install),
            scanItem,
            toolsItem,
            extractionItem,
            indexItem,
            sceneItem,
            apiItem,
            referenceItem
        };

        var nextStep = NextStep(items);
        return new ReadinessReport(
            items,
            nextStep,
            nextStep.IsReady,
            ReadinessFixCommands.ExampleQuery,
            missingToolIds);
    }

    private static ReadinessItem SchemaItem(AtlasSchemaStatus schema)
    {
        var (message, hint) = SchemaStatusWording.Describe(schema);
        return schema.Kind switch
        {
            AtlasSchemaStatusKind.NotCreated => new ReadinessItem(
                ReadinessItemIds.AtlasSchema,
                "Atlas schema",
                ReadinessState.NotApplicable,
                message,
                hint,
                false),
            AtlasSchemaStatusKind.Current => new ReadinessItem(
                ReadinessItemIds.AtlasSchema,
                "Atlas schema",
                ReadinessState.Ok,
                message,
                hint,
                false),
            AtlasSchemaStatusKind.Behind => new ReadinessItem(
                ReadinessItemIds.AtlasSchema,
                "Atlas schema",
                ReadinessState.Stale,
                message,
                hint,
                false),
            AtlasSchemaStatusKind.Ahead => new ReadinessItem(
                ReadinessItemIds.AtlasSchema,
                "Atlas schema",
                ReadinessState.Missing,
                message,
                hint,
                false),
            _ => new ReadinessItem(
                ReadinessItemIds.AtlasSchema,
                "Atlas schema",
                ReadinessState.Missing,
                message,
                hint,
                false)
        };
    }

    private static ReadinessItem RuntimeItem(DotNetRuntimeInfo runtime) =>
        runtime.IsSupported
            ? new ReadinessItem(
                ReadinessItemIds.DotNetRuntime,
                ".NET runtime",
                ReadinessState.Ok,
                runtime.Detail,
                null,
                false)
            : new ReadinessItem(
                ReadinessItemIds.DotNetRuntime,
                ".NET runtime",
                ReadinessState.Missing,
                "Install the .NET 8 runtime.",
                null,
                false);

    private static ReadinessItem GameItem(ScheduleOneInstallation? install) =>
        install is null
            ? new ReadinessItem(
                ReadinessItemIds.GameInstall,
                "Game install",
                ReadinessState.Missing,
                "Install Schedule I, then run 's1atlas scan'.",
                null,
                false)
            : new ReadinessItem(
                ReadinessItemIds.GameInstall,
                "Game install",
                ReadinessState.Ok,
                $"Schedule I installation found at {install.RootPath}.",
                null,
                false);

    private async Task<ReadinessItem> ScanItemAsync(
        EnvironmentSnapshot? snapshot,
        ScheduleOneInstallation? install,
        bool databaseUsable,
        bool databaseMissing,
        CancellationToken cancellationToken)
    {
        if (!databaseUsable && !databaseMissing)
        {
            return DatabaseBlockedItem(
                ReadinessItemIds.Scan, "Build scan", ReadinessFixCommands.Scan);
        }

        if (snapshot is null)
        {
            return new ReadinessItem(
                ReadinessItemIds.Scan,
                "Build scan",
                ReadinessState.Missing,
                "No environment snapshot has been scanned.",
                ReadinessFixCommands.Scan,
                false);
        }

        if (install is null)
        {
            return new ReadinessItem(
                ReadinessItemIds.Scan,
                "Build scan",
                ReadinessState.Stale,
                $"Build {ShortId.Display(snapshot.Build.BuildId)} was scanned, but no Schedule I installation is available.",
                ReadinessFixCommands.Scan,
                false);
        }

        try
        {
            var live = await _installationMetadataReader.ReadAsync(install, cancellationToken);
            var recorded = snapshot.Installation;
            if (Differs(recorded.SteamBuildId, live.SteamBuildId))
            {
                return StaleScan(
                    $"Steam build changed from {recorded.SteamBuildId} to {live.SteamBuildId}.");
            }

            if (Differs(recorded.ExecutableVersion, live.ExecutableVersion))
            {
                return StaleScan(
                    $"Executable version changed from {recorded.ExecutableVersion} to {live.ExecutableVersion}.");
            }

            if (RootsDiffer(recorded.InstallationRoot, live.InstallationRoot))
            {
                return StaleScan(
                    $"Installation moved from {recorded.InstallationRoot} to {live.InstallationRoot}.");
            }

            if (IsModifiedAfter(live.GameAssemblyPath, snapshot.CapturedAtUtc) ||
                IsModifiedAfter(live.GlobalMetadataPath, snapshot.CapturedAtUtc))
            {
                return StaleScan(
                    $"Game files were modified after the scan (captured {snapshot.CapturedAtUtc:O}).");
            }

            var sizes = DescribeInputSizes(live);
            return new ReadinessItem(
                ReadinessItemIds.Scan,
                "Build scan",
                ReadinessState.Ok,
                $"Build {ShortId.Display(snapshot.Build.BuildId)} scanned {snapshot.CapturedAtUtc:O}; " +
                $"Steam build {recorded.SteamBuildId ?? "unknown"}{sizes}.",
                null,
                false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ReadinessItem(
                ReadinessItemIds.Scan,
                "Build scan",
                ReadinessState.Ok,
                $"Build {ShortId.Display(snapshot.Build.BuildId)} scanned {snapshot.CapturedAtUtc:O}; live comparison unavailable.",
                null,
                false);
        }
    }

    private async Task<(ReadinessItem Item, IReadOnlyList<string> MissingToolIds)> ToolsItemAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ManagedToolStatus> statuses;
        try
        {
            statuses = await _toolStatusReader.GetStatusesAsync(cancellationToken);
        }
        catch (ToolOperationException exception)
        {
            return (new ReadinessItem(
                ReadinessItemIds.Tools,
                "Managed tools",
                ReadinessState.Missing,
                $"Managed tool definitions are unavailable ({FirstLine(exception.Message)}); restore the S1Atlas tool configuration.",
                null,
                false), []);
        }

        var problems = new List<string>();
        var missingIds = new List<string>();
        foreach (var toolId in RequiredToolIds)
        {
            var status = statuses.FirstOrDefault(candidate => string.Equals(
                candidate.Definition.Definition.ToolId,
                toolId,
                StringComparison.OrdinalIgnoreCase));
            if (status is not null &&
                status.Status == ToolInstallationStatus.Verified &&
                status.Installation is not null)
            {
                continue;
            }

            missingIds.Add(toolId);
            problems.Add(status is null
                ? $"{toolId} has no managed definition for this platform."
                : $"{toolId} is {DescribeToolProblem(status)}.");
        }

        if (problems.Count == 0)
        {
            return (new ReadinessItem(
                ReadinessItemIds.Tools,
                "Managed tools",
                ReadinessState.Ok,
                VerifiedToolsDetail(statuses),
                null,
                false), []);
        }

        return (new ReadinessItem(
            ReadinessItemIds.Tools,
            "Managed tools",
            ReadinessState.Missing,
            string.Join(" ", problems),
            ReadinessFixCommands.InstallTool(missingIds[0]),
            false), missingIds);
    }

    private async Task<ReadinessItem> IndexItemAsync(
        EnvironmentSnapshot? snapshot,
        string? verifiedExtractionId,
        bool databaseUsable,
        bool databaseMissing,
        CancellationToken cancellationToken)
    {
        if (!databaseUsable && !databaseMissing)
        {
            return DatabaseBlockedItem(
                ReadinessItemIds.Index, "Schedule I index", ReadinessFixCommands.Index);
        }

        if (snapshot is null || verifiedExtractionId is null)
        {
            return new ReadinessItem(
                ReadinessItemIds.Index,
                "Schedule I index",
                ReadinessState.NotApplicable,
                "Needs a verified extraction first.",
                ReadinessFixCommands.Index,
                false);
        }

        var run = await _indexRepository.GetLatestCompletedIndexBySourceIdentityAsync(
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            verifiedExtractionId,
            cancellationToken);
        if (run is null)
        {
            return new ReadinessItem(
                ReadinessItemIds.Index,
                "Schedule I index",
                ReadinessState.Missing,
                "No completed Schedule I index exists for the verified extraction.",
                ReadinessFixCommands.Index,
                false);
        }

        var codeSnapshot = await _indexRepository.GetCodeSnapshotAsync(
            run.SnapshotId, cancellationToken);
        if (codeSnapshot is null ||
            codeSnapshot.Codebase != CodebaseKind.ScheduleI ||
            codeSnapshot.Channel != CodeChannel.Installed ||
            !string.Equals(codeSnapshot.SourceIdentity, verifiedExtractionId, StringComparison.Ordinal))
        {
            return new ReadinessItem(
                ReadinessItemIds.Index,
                "Schedule I index",
                ReadinessState.Missing,
                "The completed index does not match the preferred extraction.",
                ReadinessFixCommands.IndexForce,
                false);
        }

        var associatedBuildId = await _indexRepository.GetCompletedIndexBuildIdAsync(
            run.IndexId, cancellationToken);
        if (associatedBuildId is not null &&
            !string.Equals(associatedBuildId, snapshot.Build.BuildId, StringComparison.Ordinal))
        {
            return new ReadinessItem(
                ReadinessItemIds.Index,
                "Schedule I index",
                ReadinessState.Missing,
                "The completed index belongs to a different build.",
                ReadinessFixCommands.IndexForce,
                false);
        }

        return new ReadinessItem(
            ReadinessItemIds.Index,
            "Schedule I index",
            ReadinessState.Ok,
            $"Schedule I index {run.IndexId} is complete.",
            null,
            false);
    }

    private async Task<ReadinessItem> SceneItemAsync(
        EnvironmentSnapshot? snapshot,
        bool databaseUsable,
        bool databaseMissing,
        CancellationToken cancellationToken)
    {
        if (!databaseUsable && !databaseMissing)
        {
            return DatabaseBlockedItem(
                ReadinessItemIds.Scene, "Scene snapshot (optional)", ReadinessFixCommands.IndexScene, true);
        }

        if (snapshot is null)
        {
            return new ReadinessItem(
                ReadinessItemIds.Scene,
                "Scene snapshot (optional)",
                ReadinessState.NotApplicable,
                "Optional; needs a scanned build first.",
                ReadinessFixCommands.IndexScene,
                true);
        }

        var scene = await _indexRepository.RequireSceneRepository()
            .GetLatestCompletedSceneSnapshotAsync(snapshot.Build.BuildId, cancellationToken);
        return scene is null
            ? new ReadinessItem(
                ReadinessItemIds.Scene,
                "Scene snapshot (optional)",
                ReadinessState.Missing,
                "No scene snapshot is present.",
                ReadinessFixCommands.IndexScene,
                true)
            : new ReadinessItem(
                ReadinessItemIds.Scene,
                "Scene snapshot (optional)",
                ReadinessState.Ok,
                $"Scene snapshot {scene.SceneSnapshotId} is present.",
                null,
                true);
    }

    private async Task<ReadinessItem> ApiItemAsync(
        EnvironmentSnapshot? snapshot,
        bool databaseUsable,
        bool databaseMissing,
        CancellationToken cancellationToken)
    {
        const string title = "API indexes (optional)";
        if (!databaseUsable && !databaseMissing)
        {
            return DatabaseBlockedItem(ReadinessItemIds.ApiIndex, title, null, true);
        }

        if (snapshot is null)
        {
            return new ReadinessItem(
                ReadinessItemIds.ApiIndex,
                title,
                ReadinessState.NotApplicable,
                "Optional; needs a scanned build first.",
                ReadinessFixCommands.IndexApiInstalled(CodebaseKind.S1Api),
                true);
        }

        if (snapshot.IdentityVersion != 2)
        {
            return new ReadinessItem(
                ReadinessItemIds.ApiIndex,
                title,
                ReadinessState.NotApplicable,
                "Optional; the environment snapshot predates API indexing; re-run 's1atlas scan'.",
                ReadinessFixCommands.Scan,
                true);
        }

        var environmentSnapshotId = EnvironmentSnapshotId.Create(snapshot);
        foreach (var codebase in ApiCodebases)
        {
            var installed = await _indexRepository.GetLatestCompletedIndexAsync(
                codebase,
                CodeChannel.Installed,
                environmentSnapshotId,
                cancellationToken);
            if (installed is null)
            {
                return new ReadinessItem(
                    ReadinessItemIds.ApiIndex,
                    title,
                    ReadinessState.Missing,
                    $"No installed {ApiDisplayName(codebase)} index exists for this build.",
                    ReadinessFixCommands.IndexApiInstalled(codebase),
                    true);
            }
        }

        foreach (var codebase in ApiCodebases)
        {
            var cached = _upstreamCache.GetCachedCommits(codebase).FirstOrDefault();
            if (cached is null)
            {
                continue;
            }

            foreach (var channel in UpstreamChannels)
            {
                var latest = await _indexRepository.GetLatestCompletedIndexAsync(
                    codebase,
                    channel,
                    environmentSnapshotId: null,
                    cancellationToken);
                if (latest is null)
                {
                    continue;
                }

                var codeSnapshot = await _indexRepository.GetCodeSnapshotAsync(
                    latest.SnapshotId, cancellationToken);
                if (codeSnapshot is null ||
                    !string.Equals(codeSnapshot.SourceIdentity, cached, StringComparison.Ordinal))
                {
                    return new ReadinessItem(
                        ReadinessItemIds.ApiIndex,
                        title,
                        ReadinessState.Stale,
                        $"{ApiDisplayName(codebase)} {channel} index is older than the last upstream sync.",
                        ReadinessFixCommands.IndexApiCommit(codebase, channel, cached),
                        true);
                }
            }
        }

        return new ReadinessItem(
            ReadinessItemIds.ApiIndex,
            title,
            ReadinessState.Ok,
            "S1API and S1MAPI installed indexes are current.",
            null,
            true);
    }

    private async Task<ReadinessItem> ReferenceItemAsync(
        EnvironmentSnapshot? snapshot,
        bool databaseUsable,
        bool databaseMissing,
        CancellationToken cancellationToken)
    {
        const string title = "Reference collections (optional)";
        if (!databaseUsable && !databaseMissing)
        {
            return DatabaseBlockedItem(ReadinessItemIds.ReferenceCollections, title, null, true);
        }

        if (snapshot is null)
        {
            return new ReadinessItem(
                ReadinessItemIds.ReferenceCollections,
                title,
                ReadinessState.NotApplicable,
                "Optional; needs a scanned build first.",
                null,
                true);
        }

        var runs = await _indexRepository.GetCompletedReferenceIndexesAsync(cancellationToken);
        return runs.Count == 0
            ? new ReadinessItem(
                ReadinessItemIds.ReferenceCollections,
                title,
                ReadinessState.Missing,
                "No reference collections are indexed; run 's1atlas reference index <manifest>' with a collection manifest.",
                null,
                true)
            : new ReadinessItem(
                ReadinessItemIds.ReferenceCollections,
                title,
                ReadinessState.Ok,
                runs.Count == 1
                    ? "1 reference collection is complete."
                    : $"{runs.Count} reference collections are complete.",
                null,
                true);
    }

    private static ReadinessItem DatabaseBlockedItem(
        string id,
        string title,
        string? fixCommand,
        bool optional = false) =>
        new(
            id,
            title,
            ReadinessState.NotApplicable,
            optional
                ? "Optional; cannot evaluate until the atlas schema is upgraded."
                : "Cannot evaluate until the atlas schema is upgraded.",
            fixCommand,
            optional);

    private static ReadinessNextStep NextStep(IReadOnlyList<ReadinessItem> items)
    {
        var blocker = items.FirstOrDefault(item =>
            !item.IsOptional &&
            item.State is ReadinessState.Missing or ReadinessState.Stale);
        return blocker is null
            ? new ReadinessNextStep(true, "Ready", ReadinessFixCommands.ExampleQuery)
            : new ReadinessNextStep(
                false,
                $"Next: {blocker.FixCommand ?? blocker.Detail}",
                blocker.FixCommand);
    }

    private static ReadinessItem StaleScan(string detail) =>
        new(
            ReadinessItemIds.Scan,
            "Build scan",
            ReadinessState.Stale,
            detail,
            ReadinessFixCommands.Scan,
            false);

    private static bool Differs(string? recorded, string? live) =>
        !string.IsNullOrWhiteSpace(recorded) &&
        !string.IsNullOrWhiteSpace(live) &&
        !string.Equals(recorded, live, StringComparison.Ordinal);

    private static bool RootsDiffer(string? recorded, string? live)
    {
        if (recorded is null || live is null)
        {
            return false;
        }

        return !string.Equals(
            Path.TrimEndingDirectorySeparator(recorded),
            Path.TrimEndingDirectorySeparator(live),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsModifiedAfter(string? path, DateTimeOffset capturedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.LastWriteTimeUtc > capturedAtUtc.UtcDateTime;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            ArgumentException)
        {
            return false;
        }
    }

    private static string DescribeInputSizes(InstallationObservation live)
    {
        var assembly = TryFileSize(live.GameAssemblyPath);
        var metadata = TryFileSize(live.GlobalMetadataPath);
        return assembly is null || metadata is null
            ? string.Empty
            : $"; inputs {assembly} B / {metadata} B, unmodified since scan";
    }

    private static long? TryFileSize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            ArgumentException)
        {
            return null;
        }
    }

    private static string DescribeToolProblem(ManagedToolStatus status) =>
        status.Status == ToolInstallationStatus.NotInstalled
            ? "not installed"
            : $"{status.Status.ToString().ToLowerInvariant()} ({FirstLine(status.DiagnosticMessage ?? "no detail")})";

    private static string VerifiedToolsDetail(IReadOnlyList<ManagedToolStatus> statuses)
    {
        var versions = RequiredToolIds
            .Select(id => statuses.FirstOrDefault(candidate => string.Equals(
                candidate.Definition.Definition.ToolId,
                id,
                StringComparison.OrdinalIgnoreCase))?.Definition.Definition.Version)
            .ToArray();
        return versions.All(version => !string.IsNullOrWhiteSpace(version))
            ? $"Cpp2IL {versions[0]} and Unity class database {versions[1]} are installed and verified."
            : "Cpp2IL and the Unity class database are installed and verified.";
    }

    private static string ApiDisplayName(CodebaseKind codebase) =>
        codebase switch
        {
            CodebaseKind.S1Api => "S1API",
            CodebaseKind.S1MApi => "S1MAPI",
            _ => codebase.ToString()
        };

    private static string FirstLine(string value)
    {
        var line = value.Split('\n', '\r')[0].Trim();
        return line.Length > 160 ? line[..160] + "..." : line;
    }
}
