using System.CommandLine;
using S1Atlas.Application.Authority;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Cli.Commands;

internal static class InvestigateSeamCommand
{
    public static Command Create(
        SeamInvestigationService service,
        ReferenceModQueryService referenceService,
        InstalledBuildAuthorityResolver authorityResolver,
        IAtlasRepository atlasRepository,
        IIndexRepository indexRepository,
        string dataRoot,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var selectorArgument = new Argument<string>("selector") { Description = "A symbol selector for the seam under investigation." };
        var questionOption = new Option<string>("--question")
        {
            Description = "The behavioral question that frames the seam investigation.",
            Required = true
        };
        var codebaseOption = new Option<string>("--codebase") { Description = "schedule-i, s1api, or s1mapi." };
        var channelOption = new Option<string>("--channel") { Description = "installed, release, preview, or all." };
        var buildOption = new Option<string?>("--build") { Description = "Select a Schedule I Installed build ID." };
        var scopeOption = new Option<string?>("--scope") { Description = "game, reference, or all." };
        var collectionOption = new Option<string?>("--collection") { Description = "A named or indexed reference collection." };
        var relationshipLimitOption = new Option<int>("--relationship-limit")
        {
            Description = "Maximum relationship evidence rows to inspect (1-50).",
            DefaultValueFactory = _ => 50
        };
        var ownerLimitOption = new Option<int>("--owner-limit")
        {
            Description = "Maximum owner candidates to return (1-50).",
            DefaultValueFactory = _ => 10
        };
        var contextOption = new Option<int>("--context")
        {
            Description = "Lines of source context to include around the selected seam.",
            DefaultValueFactory = _ => 5
        };
        var detailsOption = new Option<bool>("--details")
        {
            Description = "Include detailed claims and evidence sections in human output."
        };
        var nativeSymbolIdOption = new Option<string[]>("--native-symbol-id")
        {
            Description = "Explicit native symbol ID to include in a read-only evidence lookup; repeat for multiple IDs."
        };
        var nativeTraversalBudgetOption = new Option<int>("--native-traversal-budget")
        {
            Description = "Native evidence traversal budget (0 disables lookup; maximum 500).",
            DefaultValueFactory = _ => 0
        };
        var jsonOption = CommandOutput.CreateJsonOption();

        var command = new Command("investigate_seam", "Investigate whether a resolved symbol is a supportable ownership seam.");
        command.Arguments.Add(selectorArgument);
        command.Options.Add(questionOption);
        command.Options.Add(codebaseOption);
        command.Options.Add(channelOption);
        command.Options.Add(buildOption);
        command.Options.Add(scopeOption);
        command.Options.Add(collectionOption);
        command.Options.Add(relationshipLimitOption);
        command.Options.Add(ownerLimitOption);
        command.Options.Add(contextOption);
        command.Options.Add(detailsOption);
        command.Options.Add(nativeSymbolIdOption);
        command.Options.Add(nativeTraversalBudgetOption);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("investigate_seam", parseResult.GetValue(jsonOption), output, error);
            return CommandExecution.Run(
                () => Execute(
                    service,
                    referenceService,
                    authorityResolver,
                    atlasRepository,
                    indexRepository,
                    dataRoot,
                    parseResult.GetValue(selectorArgument)!,
                    parseResult.GetValue(questionOption)!,
                    parseResult.GetValue(codebaseOption),
                    parseResult.GetValue(channelOption),
                    parseResult.GetValue(scopeOption),
                    parseResult.GetValue(collectionOption),
                    parseResult.GetValue(buildOption),
                    parseResult.GetValue(relationshipLimitOption),
                    parseResult.GetValue(ownerLimitOption),
                    parseResult.GetValue(contextOption),
                    parseResult.GetValue(detailsOption),
                    parseResult.GetValue(nativeSymbolIdOption),
                    parseResult.GetValue(nativeTraversalBudgetOption),
                    commandOutput,
                    cancellationToken),
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    private static int Execute(
        SeamInvestigationService service,
        ReferenceModQueryService referenceService,
        InstalledBuildAuthorityResolver authorityResolver,
        IAtlasRepository atlasRepository,
        IIndexRepository indexRepository,
        string dataRoot,
        string selector,
        string question,
        string? codebase,
        string? channel,
        string? scope,
        string? collection,
        string? buildId,
        int relationshipLimit,
        int ownerLimit,
        int context,
        bool includeDetails,
        IReadOnlyList<string>? nativeSymbolIds,
        int nativeTraversalBudget,
        CommandOutput commandOutput,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return commandOutput.Failure(1, "InvalidSelector", "The seam selector cannot be blank.");
        if (string.IsNullOrWhiteSpace(question))
            return commandOutput.Failure(1, "InvalidQuestion", "The investigation question cannot be blank.");
        if (relationshipLimit is < 1 or > 50)
            return commandOutput.Failure(1, "InvalidRelationshipLimit", "--relationship-limit must be between 1 and 50.");
        if (ownerLimit is < 1 or > 50)
            return commandOutput.Failure(1, "InvalidOwnerLimit", "--owner-limit must be between 1 and 50.");
        if (context < 0)
            return commandOutput.Failure(1, "InvalidContext", "--context cannot be negative.");
        if (nativeTraversalBudget is < 0 or > 500)
            return commandOutput.Failure(1, "InvalidNativeTraversalBudget", "--native-traversal-budget must be between 0 and 500.");

        atlasRepository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
        IndexQueryOptions options;
        try
        {
            options = IndexQueryCommandFactory.ParseOptions(
                codebase,
                channel,
                relationshipLimit,
                scope,
                collection);
        }
        catch (ArgumentException exception)
        {
            return commandOutput.Failure(1, "InvalidOptionCombination", exception.Message);
        }

        SeamInvestigationResult result;
        IndexQueryCommandFactory.ExecutionAuthority authority = default;
        try
        {
            authority = IndexQueryCommandFactory.ResolveExecutionAuthority(
                authorityResolver,
                referenceService,
                options,
                buildId,
                cancellationToken);
            if (authority.ErrorCode is not null)
                return commandOutput.Failure(1, authority.ErrorCode, authority.ErrorMessage!);

            var request = new SeamInvestigationRequest(
                question,
                selector,
                options,
                relationshipLimit,
                ownerLimit,
                context,
                includeDetails,
                nativeSymbolIds,
                nativeTraversalBudget);
            result = CreatePinnedService(
                    service,
                    indexRepository,
                    atlasRepository,
                    dataRoot,
                    options.ReferenceCollection,
                    authority.Run,
                    authority.ReferenceIndexId,
                    cancellationToken)
                .InvestigateAsync(request, cancellationToken)
                .GetAwaiter()
                .GetResult();
            result = EnrichPinnedProvenance(result, authority, options);
        }
        catch (FileNotFoundException exception)
        {
            return commandOutput.Failure(1, "SourceUnavailable", exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return commandOutput.Failure(1, "SourceIntegrityFailure", exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return commandOutput.Failure(1, "InvalidCodebaseChannel", exception.Message);
        }

        return Complete(commandOutput, result, includeDetails, authority, options);
    }

    private static SeamInvestigationResult EnrichPinnedProvenance(
        SeamInvestigationResult result,
        IndexQueryCommandFactory.ExecutionAuthority authority,
        IndexQueryOptions options)
    {
        if (result.PinnedProvenance is null || authority.BuildAuthority is null)
            return result;

        var build = authority.BuildAuthority;
        var isReference = authority.ReferenceIndexId is not null &&
            (options.Scope == IndexQueryScope.Reference ||
             options.Scope == IndexQueryScope.All && IsReferenceResult(result));

        var pinned = isReference
            ? result.PinnedProvenance with
            {
                RequestedBuildId = build.RequestedBuildId ?? build.ResolvedBuildId,
                ResolvedBuildId = build.ResolvedBuildId,
                ExtractionId = null,
                IndexId = authority.ReferenceIndexId,
                Codebase = CodebaseKind.ReferenceMod.ToString(),
                Channel = CodeChannel.Installed.ToString(),
                IntegrityVerified = false
            }
            : result.PinnedProvenance with
            {
                RequestedBuildId = build.RequestedBuildId ?? build.ResolvedBuildId,
                ResolvedBuildId = build.ResolvedBuildId,
                ExtractionId = build.ExtractionId,
                IndexId = build.IndexId,
                Codebase = CodebaseKind.ScheduleI.ToString(),
                Channel = CodeChannel.Installed.ToString(),
                IntegrityVerified = true
            };

        return result with { PinnedProvenance = pinned };
    }

    private static bool IsReferenceResult(SeamInvestigationResult result) =>
        result.Resolution.Symbol?.Origin == "reference" ||
        result.Candidate?.Origin == "reference" ||
        result.OwnerCandidates.Any(candidate => candidate.Symbol.Origin == "reference");

    private static int Complete(
        CommandOutput commandOutput,
        SeamInvestigationResult result,
        bool includeDetails,
        IndexQueryCommandFactory.ExecutionAuthority authority,
        IndexQueryOptions options)
    {
        return result.Resolution.Status switch
        {
            SymbolResolutionStatus.Ambiguous => commandOutput.Failure(
                1,
                "AmbiguousSymbol",
                "The symbol selector matched multiple candidates. Use an exact symbol ID or signature.",
                new IndexQueryFailureData(result.Resolution.Candidates)),
            SymbolResolutionStatus.NoCompletedIndex => commandOutput.Failure(
                1,
                "NoCompletedIndex",
                "No completed index exists for the requested codebase and channel.",
                new IndexQueryFailureData([])),
            SymbolResolutionStatus.NotFound => commandOutput.Failure(
                1,
                "SymbolNotFound",
                "No indexed symbol matched the selector.",
                new IndexQueryFailureData([])),
            SymbolResolutionStatus.Resolved when !HasRequiredGateRecords(result) =>
                commandOutput.Failure(
                    1,
                    "IncompleteSeamResult",
                    "The resolved seam result is missing required gate records."),
            _ => commandOutput.Success(
                SeamInvestigationOutput.FromResult(
                    result,
                    BuildReferenceBaseProvenance(result, authority, options)),
                writer => WriteHuman(result, writer, includeDetails))
        };
    }

    private static SeamPinnedProvenanceOutput? BuildReferenceBaseProvenance(
        SeamInvestigationResult result,
        IndexQueryCommandFactory.ExecutionAuthority authority,
        IndexQueryOptions options)
    {
        if (authority.BuildAuthority is null ||
            authority.ReferenceIndexId is null ||
            options.Scope == IndexQueryScope.Game ||
            options.Scope == IndexQueryScope.All && !IsReferenceResult(result))
            return null;

        var build = authority.BuildAuthority;
        return new SeamPinnedProvenanceOutput(
            build.RequestedBuildId ?? build.ResolvedBuildId,
            build.ResolvedBuildId,
            build.ExtractionId,
            build.IndexId,
            CodebaseKind.ScheduleI.ToString(),
            CodeChannel.Installed.ToString(),
            true);
    }

    private static bool HasRequiredGateRecords(SeamInvestigationResult result) =>
        result.PinnedProvenance is not null &&
        result.AuthorityEntityAttribution is not null &&
        result.AlternateGenericCallersAndExclusivity is not null &&
        result.LifecyclePositionAndBeforeAfterState is not null &&
        result.ApiBeforePatchResult is not null;

    private static void WriteHuman(
        SeamInvestigationResult result,
        TextWriter writer,
        bool includeDetails)
    {
        writer.WriteLine($"Question:     {result.BehavioralQuestion}");
        writer.WriteLine($"Conclusion:   {result.Conclusion}");
        writer.WriteLine($"Candidate:    {FormatCandidate(result.Candidate, result.CandidateRole)}");
        writer.WriteLine($"Body coverage:     {result.BodyCoverage}" + (result.BodyRecoveryStatus is null ? string.Empty : $" ({result.BodyRecoveryStatus})"));
        writer.WriteLine($"Callable coverage: {result.CallableCoverage}");

        writer.WriteLine("Coverage warnings:");
        if (result.CoverageWarnings.Count == 0)
            writer.WriteLine("  none");
        else
            foreach (var warning in result.CoverageWarnings)
                writer.WriteLine($"  {warning}");

        writer.WriteLine("Owner candidates:");
        if (result.OwnerCandidates.Count == 0)
        {
            writer.WriteLine("  none");
        }
        else
        {
            foreach (var owner in result.OwnerCandidates)
            {
                writer.WriteLine(
                    $"  {owner.Symbol.QualifiedName} [{owner.Symbol.SymbolId}] | role: {owner.Role} | path: {string.Join(" -> ", owner.Path.RelationshipIds)}");
            }
        }

        writer.WriteLine("Unknown dimensions:");
        if (result.UnknownDimensions.Count == 0)
            writer.WriteLine("  none");
        else
            foreach (var value in result.UnknownDimensions)
                writer.WriteLine($"  {value}");

        writer.WriteLine("Next actions:");
        if (result.NextActions.Count == 0)
        {
            writer.WriteLine("  none");
        }
        else
        {
            foreach (var action in result.NextActions)
            {
                writer.WriteLine(
                    $"  {action.Kind} | {action.Scope} | runtime proof: {action.RequiresRuntimeProof} | {action.Reason}");
            }
        }

        if (!includeDetails)
            return;

        writer.WriteLine("Claims:");
        if (result.Claims.Count == 0)
        {
            writer.WriteLine("  none");
        }
        else
        {
            foreach (var claim in result.Claims)
            {
                writer.WriteLine(
                    $"  {claim.Dimension} | {claim.Classification} | {claim.Statement} | evidence: {string.Join(", ", claim.EvidenceIds)}");
            }
        }

        writer.WriteLine("Evidence sections:");
        if (result.EvidenceSections.Count == 0)
        {
            writer.WriteLine("  none");
            return;
        }

        foreach (var section in result.EvidenceSections)
        {
            writer.WriteLine(
                $"  {section.Family} | {section.Coverage} | {section.ReturnedCount}/{section.TotalCount} | evidence: {string.Join(", ", section.EvidenceIds)}");
            if (!string.IsNullOrWhiteSpace(section.Notice))
                writer.WriteLine($"    notice: {section.Notice}");
        }
    }

    private static string FormatCandidate(SymbolQueryResult? candidate, string role) =>
        candidate is null
            ? "none"
            : $"{candidate.QualifiedName} [{candidate.SymbolId}] | role: {role}";

    private static SeamInvestigationService CreatePinnedService(
        SeamInvestigationService fallback,
        IIndexRepository indexRepository,
        IAtlasRepository atlasRepository,
        string dataRoot,
        string? referenceCollection,
        IndexRunRecord? installedPinnedRun,
        string? referenceIndexId,
        CancellationToken cancellationToken)
    {
        if (installedPinnedRun is null && referenceIndexId is null)
            return fallback;

        var pinnedRepository = new PinnedQueryAuthorityRepository(
            indexRepository,
            installedPinnedRun,
            referenceCollection,
            referenceIndexId,
            cancellationToken);
        return new SeamInvestigationService(
            new IndexQueryService(pinnedRepository, dataRoot),
            new FederatedIndexQueryService(pinnedRepository, dataRoot),
            new ReferenceModQueryService(pinnedRepository, dataRoot),
            indexRepository,
            atlasRepository);
    }

    private sealed class PinnedQueryAuthorityRepository : IIndexRepository
    {
        private readonly IIndexRepository _inner;
        private readonly IndexRunRecord? _installedPinnedRun;
        private readonly string? _referenceCollection;
        private readonly IndexRunRecord? _referencePinnedRun;
        private readonly IndexRunRecord? _referenceGamePinnedRun;

        public PinnedQueryAuthorityRepository(
            IIndexRepository inner,
            IndexRunRecord? installedPinnedRun,
            string? referenceCollection,
            string? referenceIndexId,
            CancellationToken cancellationToken)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _installedPinnedRun = installedPinnedRun;
            _referenceCollection = referenceCollection;

            if (referenceIndexId is null)
                return;

            _referencePinnedRun = _inner.GetCompletedIndexAsync(referenceIndexId, cancellationToken).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"The resolved reference authority index '{referenceIndexId}' is unavailable.");
            var context = _inner.GetReferenceIndexContextAsync(referenceIndexId, cancellationToken).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"The resolved reference authority '{referenceIndexId}' has no persisted base game context.");
            _referenceGamePinnedRun = _inner.GetCompletedIndexAsync(context.GameIndexId, cancellationToken).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"The resolved reference authority base game index '{context.GameIndexId}' is unavailable.");
        }

        public Task CreateCodeSnapshotAsync(CodeSnapshotRecord snapshot, CancellationToken cancellationToken) =>
            _inner.CreateCodeSnapshotAsync(snapshot, cancellationToken);

        public Task<CodeSnapshotRecord?> GetCodeSnapshotAsync(string snapshotId, CancellationToken cancellationToken) =>
            _inner.GetCodeSnapshotAsync(snapshotId, cancellationToken);

        public Task StartIndexRunAsync(IndexRunRecord run, CancellationToken cancellationToken) =>
            _inner.StartIndexRunAsync(run, cancellationToken);

        public Task CompleteIndexRunAsync(string indexId, IndexWriteSet writeSet, string completedAtUtc, CancellationToken cancellationToken) =>
            _inner.CompleteIndexRunAsync(indexId, writeSet, completedAtUtc, cancellationToken);

        public Task FailIndexRunAsync(string indexId, string failureMessage, string completedAtUtc, CancellationToken cancellationToken) =>
            _inner.FailIndexRunAsync(indexId, failureMessage, completedAtUtc, cancellationToken);

        public Task<IndexRunRecord?> GetCompletedIndexAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedIndexAsync(indexId, cancellationToken);

        public Task<IndexRunRecord?> GetLatestCompletedIndexAsync(
            CodebaseKind codebase,
            CodeChannel channel,
            string? environmentSnapshotId,
            CancellationToken cancellationToken) =>
            codebase == CodebaseKind.ScheduleI && channel == CodeChannel.Installed && environmentSnapshotId is null
                ? Task.FromResult<IndexRunRecord?>(_installedPinnedRun ?? _referenceGamePinnedRun)
                : _inner.GetLatestCompletedIndexAsync(codebase, channel, environmentSnapshotId, cancellationToken);

        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedSymbolsAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolPageAsync(string indexId, int offset, int limit, CancellationToken cancellationToken) =>
            _inner.GetCompletedSymbolPageAsync(indexId, offset, limit, cancellationToken);

        public Task<int> CountCompletedSymbolsAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.CountCompletedSymbolsAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolByCanonicalKeyAsync(string indexId, string canonicalKey, CancellationToken cancellationToken) =>
            _inner.GetCompletedSymbolByCanonicalKeyAsync(indexId, canonicalKey, cancellationToken);

        public Task<IndexSymbolRecord?> GetCompletedSymbolByIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) =>
            _inner.GetCompletedSymbolByIdAsync(indexId, symbolId, cancellationToken);

        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsByIdsAsync(string indexId, IReadOnlyList<string> symbolIds, CancellationToken cancellationToken) =>
            _inner.GetCompletedSymbolsByIdsAsync(indexId, symbolIds, cancellationToken);

        public Task<int> CountCompletedSymbolMatchesAsync(string indexId, string query, CancellationToken cancellationToken, string? kind = null) =>
            _inner.CountCompletedSymbolMatchesAsync(indexId, query, cancellationToken, kind);

        public Task<IReadOnlyList<IndexSymbolRecord>> SearchCompletedSymbolsAsync(string indexId, string query, int limit, CancellationToken cancellationToken, string? kind = null) =>
            _inner.SearchCompletedSymbolsAsync(indexId, query, limit, cancellationToken, kind);

        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedRelationshipsAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsBySourceSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) =>
            _inner.GetCompletedRelationshipsBySourceSymbolIdAsync(indexId, symbolId, cancellationToken);

        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) =>
            _inner.GetCompletedRelationshipsByTargetSymbolIdAsync(indexId, symbolId, cancellationToken);

        public Task<int> CountCompletedRelationshipsByTargetTextAsync(string indexId, string targetText, RelationshipTargetTextMatchMode matchMode, string relationshipKind, CancellationToken cancellationToken) =>
            _inner.CountCompletedRelationshipsByTargetTextAsync(indexId, targetText, matchMode, relationshipKind, cancellationToken);

        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetTextAsync(string indexId, string targetText, RelationshipTargetTextMatchMode matchMode, string relationshipKind, int limit, CancellationToken cancellationToken) =>
            _inner.GetCompletedRelationshipsByTargetTextAsync(indexId, targetText, matchMode, relationshipKind, limit, cancellationToken);

        public Task<int> CountCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, string relationshipKind, CancellationToken cancellationToken) =>
            _inner.CountCompletedRelationshipsByTargetSymbolIdAsync(indexId, symbolId, relationshipKind, cancellationToken);

        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, string relationshipKind, int limit, CancellationToken cancellationToken) =>
            _inner.GetCompletedRelationshipsByTargetSymbolIdAsync(indexId, symbolId, relationshipKind, limit, cancellationToken);

        public Task<IReadOnlyList<IndexSourceFileRecord>> GetCompletedSourceFilesAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedSourceFilesAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexSourceLocationRecord>> GetCompletedSourceLocationsAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedSourceLocationsAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexFingerprintRecord>> GetCompletedFingerprintsAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedFingerprintsAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexCallableSurfaceRecord>> GetCompletedCallableSurfaceAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedCallableSurfaceAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexCallableSurfaceRecord>> GetCompletedCallableSurfaceByGameSymbolIdAsync(string indexId, string gameSymbolId, CancellationToken cancellationToken) =>
            _inner.GetCompletedCallableSurfaceByGameSymbolIdAsync(indexId, gameSymbolId, cancellationToken);

        public Task<ReferenceIndexContextRecord?> GetReferenceIndexContextAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetReferenceIndexContextAsync(indexId, cancellationToken);

        public Task<IndexRunRecord?> GetLatestCompletedReferenceIndexAsync(string collection, CancellationToken cancellationToken) =>
            _referencePinnedRun is not null &&
            (_referenceCollection is not null && string.Equals(collection, _referenceCollection, StringComparison.Ordinal) ||
             string.Equals(collection, _referencePinnedRun.IndexId, StringComparison.Ordinal))
                ? Task.FromResult<IndexRunRecord?>(_referencePinnedRun)
                : _inner.GetLatestCompletedReferenceIndexAsync(collection, cancellationToken);

        public Task<IReadOnlyList<IndexRunRecord>> GetCompletedReferenceIndexesAsync(CancellationToken cancellationToken) =>
            _inner.GetCompletedReferenceIndexesAsync(cancellationToken);

        public Task<IReadOnlyList<IndexReferenceModRecord>> GetCompletedReferenceModsAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedReferenceModsAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexReferenceDocumentRecord>> GetCompletedReferenceDocumentsAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedReferenceDocumentsAsync(indexId, cancellationToken);

        public Task<IReadOnlyList<IndexReferenceDocumentRecord>> GetCompletedReferenceDocumentsAsync(string indexId, int limit, CancellationToken cancellationToken) =>
            _inner.GetCompletedReferenceDocumentsAsync(indexId, limit, cancellationToken);

        public Task<IReadOnlyList<IndexReferenceDocumentRecord>> SearchCompletedReferenceDocumentsAsync(string indexId, string query, int limit, CancellationToken cancellationToken) =>
            _inner.SearchCompletedReferenceDocumentsAsync(indexId, query, limit, cancellationToken);

        public Task<IndexRunRecord?> GetLatestCompletedIndexBySourceIdentityAsync(CodebaseKind codebase, CodeChannel channel, string sourceIdentity, CancellationToken cancellationToken) =>
            _inner.GetLatestCompletedIndexBySourceIdentityAsync(codebase, channel, sourceIdentity, cancellationToken);

        public Task<IndexRunRecord?> GetLatestCompletedIndexForBuildAsync(CodebaseKind codebase, CodeChannel channel, string buildId, CancellationToken cancellationToken) =>
            _inner.GetLatestCompletedIndexForBuildAsync(codebase, channel, buildId, cancellationToken);

        public Task<string?> GetCompletedIndexBuildIdAsync(string indexId, CancellationToken cancellationToken) =>
            _inner.GetCompletedIndexBuildIdAsync(indexId, cancellationToken);
    }
}
