using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Inputs;
using S1Atlas.Extraction.Tools;

namespace S1Atlas.Extraction;

/// <summary>
/// Everything the Phase 4 workflow needs to decide reuse, revalidation, or a new
/// process without ever resolving a live <c>--game-path</c> or running Cpp2IL:
/// the selected build, the resolved extraction profile and validation policy, a
/// freshly resolved (and freshly re-verified) tool instance, and the production
/// recipe ID derived from build/tool/profile alone. The validation policy is
/// deliberately absent from <see cref="RecipeId"/>: policy is provenance, not
/// production identity.
/// </summary>
internal sealed record PreparedExtractionContext(
    GameBuild Build,
    ResolvedExtractionProfile Profile,
    ResolvedValidationPolicy Policy,
    ResolvedExtractionTool Tool,
    string RecipeId);

/// <summary>
/// Resolves the extraction context the Phase 4 workflow reasons over before it
/// commits to any side effect. It resolves the build, the pinned extraction
/// profile, the committed validation policy, and a freshly re-verified Cpp2IL
/// tool instance, then derives the recipe ID from build, tool, and profile only.
/// It never resolves game input, never inspects a live game path, and never
/// starts a process, so the workflow can safely list, reuse, or revalidate
/// existing validated output on top of it without touching the game.
/// </summary>
internal sealed class ExtractionPreparationService
{
    private readonly Func<string?, CancellationToken, Task<GameBuild>> _selectBuildAsync;
    private readonly Func<string, ResolvedExtractionProfile> _getProfile;
    private readonly Func<string, ResolvedValidationPolicy> _getPolicy;
    private readonly Func<string?, CancellationToken, Task<ResolvedExtractionTool>> _resolveToolAsync;

    internal ExtractionPreparationService(
        Func<string?, CancellationToken, Task<GameBuild>> selectBuildAsync,
        Func<string, ResolvedExtractionProfile> getProfile,
        Func<string, ResolvedValidationPolicy> getPolicy,
        Func<string?, CancellationToken, Task<ResolvedExtractionTool>> resolveToolAsync)
    {
        _selectBuildAsync = selectBuildAsync ?? throw new ArgumentNullException(nameof(selectBuildAsync));
        _getProfile = getProfile ?? throw new ArgumentNullException(nameof(getProfile));
        _getPolicy = getPolicy ?? throw new ArgumentNullException(nameof(getPolicy));
        _resolveToolAsync = resolveToolAsync ?? throw new ArgumentNullException(nameof(resolveToolAsync));
    }

    public ExtractionPreparationService(
        ExtractionInputResolver inputResolver,
        IExtractionProfileProvider profileProvider,
        IValidationPolicyProvider validationPolicyProvider,
        ExtractionToolResolver toolResolver)
        : this(
            (inputResolver ?? throw new ArgumentNullException(nameof(inputResolver))).SelectBuildAsync,
            (profileProvider ?? throw new ArgumentNullException(nameof(profileProvider))).GetRequired,
            (validationPolicyProvider ?? throw new ArgumentNullException(nameof(validationPolicyProvider)))
                .GetRequired,
            (toolResolver ?? throw new ArgumentNullException(nameof(toolResolver))).ResolveAsync)
    {
    }

    public async Task<PreparedExtractionContext> PrepareAsync(
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProfileId);
        cancellationToken.ThrowIfCancellationRequested();

        var build = await _selectBuildAsync(options.BuildId, cancellationToken);

        ResolvedExtractionProfile profile;
        ResolvedValidationPolicy policy;
        try
        {
            profile = _getProfile(options.ProfileId);
            policy = _getPolicy(ExtractionOrchestrator.ValidationPolicyId);
        }
        catch (ToolOperationException exception)
        {
            throw MapToolFailure(exception);
        }

        ResolvedExtractionTool tool;
        try
        {
            tool = await _resolveToolAsync(options.CustomCpp2IlPath, cancellationToken);
        }
        catch (ToolOperationException exception)
        {
            throw MapToolFailure(exception);
        }

        var recipeId = ExtractionRecipeId.Create(new ExtractionRecipe(
            build.BuildId,
            tool.Instance.ToolInstanceId,
            profile.ProfileDigest,
            profile.Profile.AdapterVersion,
            profile.Profile.ExtractionSchemaVersion));

        return new PreparedExtractionContext(build, profile, policy, tool, recipeId);
    }

    private static ExtractionOperationException MapToolFailure(ToolOperationException exception)
    {
        var code = Enum.TryParse<ExtractionFailureCode>(
            exception.Code,
            ignoreCase: false,
            out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : ExtractionFailureCode.ToolDefinitionInvalid;
        return new ExtractionOperationException(
            ExtractionFailureStage.ToolResolution,
            code,
            exception.Message,
            attemptId: null,
            exception);
    }
}
