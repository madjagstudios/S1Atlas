using S1Atlas.Core.Indexing;
using S1Atlas.Core.Scenes;
using S1Atlas.Indexing.Query;
using S1Atlas.Indexing.Scene;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.IntegrationTests.GoldenFacts;

[Trait("Category", "LocalGameRequired")]
public sealed class GoldenFactsTests
{
    [Fact]
    public async Task Scene_facts_match_the_live_build()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await GoldenFactsContext.OpenAsync(
            requireFactsFile: true, cancellationToken);
        var facts = context.Facts!.SceneFacts;
        Assert.SkipUnless(
            facts.Count > 0,
            "The golden facts file holds no scene facts. Skipping: NEEDS_CONTEXT.");

        var current = await context.Repository.GetCurrentSnapshotAsync(cancellationToken);
        Assert.SkipUnless(
            current is not null,
            "The copied atlas has no current snapshot. Skipping: NEEDS_CONTEXT.");
        var snapshot = await context.Repository.GetLatestCompletedSceneSnapshotAsync(
            current!.Build.BuildId, cancellationToken);
        Assert.SkipUnless(
            snapshot is not null,
            "The copied atlas has no completed scene index. Skipping: NEEDS_CONTEXT.");

        var service = new SceneQueryService(context.Repository, context.Repository);
        var failures = new List<string>();
        foreach (var fact in facts)
            await CheckSceneFactAsync(service, snapshot!.SceneSnapshotId, fact, failures, cancellationToken);

        Assert.True(
            failures.Count == 0,
            $"Golden scene facts failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    [Fact]
    public async Task Callee_facts_match_the_live_build()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await GoldenFactsContext.OpenAsync(
            requireFactsFile: true, cancellationToken);
        var facts = context.Facts!.CalleeFacts;
        Assert.SkipUnless(
            facts.Count > 0,
            "The golden facts file holds no callee facts. Skipping: NEEDS_CONTEXT.");

        var run = await context.Repository.GetLatestCompletedIndexAsync(
            CodebaseKind.ScheduleI, CodeChannel.Installed, environmentSnapshotId: null, cancellationToken);
        Assert.SkipUnless(
            run is not null,
            "No completed Schedule I Installed index exists locally. Skipping: NEEDS_CONTEXT.");

        var service = new IndexQueryService(context.Repository);
        var failures = new List<string>();
        foreach (var fact in facts)
            await CheckCalleeFactAsync(service, run!, fact, failures, cancellationToken);

        Assert.True(
            failures.Count == 0,
            $"Golden callee facts failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static async Task CheckSceneFactAsync(
        SceneQueryService service,
        string sceneSnapshotId,
        SceneFieldFact fact,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        var fields = await ResolveFieldSetAsync(service, sceneSnapshotId, fact, failures, cancellationToken);
        if (fields is null)
            return;

        var field = fields.Fields.FirstOrDefault(
            candidate => string.Equals(candidate.Path, fact.FieldPath, StringComparison.Ordinal));
        if (field is null)
        {
            failures.Add(
                $"'{fact.Name}': field path '{fact.FieldPath}' not found; " +
                $"available: {string.Join(", ", fields.Fields.Select(candidate => candidate.Path))}.");
            return;
        }

        if (!string.IsNullOrEmpty(fact.ExpectedValue))
        {
            if (!string.Equals(field.Value, fact.ExpectedValue, StringComparison.Ordinal))
                failures.Add(
                    $"'{fact.Name}': '{fact.FieldPath}' was '{field.Value}', " +
                    $"expected '{fact.ExpectedValue}'.");
            return;
        }

        if (!string.IsNullOrEmpty(fact.ExpectedTargetName))
        {
            if (field.Kind != SceneScriptFieldValueKind.PPtr || field.Target is null)
            {
                failures.Add($"'{fact.Name}': '{fact.FieldPath}' is not an object reference.");
                return;
            }

            if (field.Target.Status != SceneScriptFieldTargetStatus.Resolved ||
                !string.Equals(field.Target.Name, fact.ExpectedTargetName, StringComparison.Ordinal))
                failures.Add(
                    $"'{fact.Name}': '{fact.FieldPath}' resolves to " +
                    $"'{field.Target.Name ?? field.Target.Status.ToString()}', " +
                    $"expected '{fact.ExpectedTargetName}'.");
            return;
        }

        failures.Add(
            $"'{fact.Name}': set exactly one of expectedValue / expectedTargetName.");
    }

    private static async Task<SceneScriptFieldSetRecord?> ResolveFieldSetAsync(
        SceneQueryService service,
        string sceneSnapshotId,
        SceneFieldFact fact,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        if (string.Equals(fact.Owner, "component", StringComparison.OrdinalIgnoreCase))
        {
            var result = await service.ComponentAsync(
                new ComponentQueryRequest(sceneSnapshotId, fact.Selector, IncludeReferences: true),
                cancellationToken);
            if (result.Status != SceneQueryStatus.Resolved || result.ScriptFields is null)
            {
                failures.Add($"'{fact.Name}': component selector did not resolve ({result.Status}).");
                return null;
            }

            return RequireDecoded(fact, result.ScriptFields, failures);
        }

        if (string.Equals(fact.Owner, "asset", StringComparison.OrdinalIgnoreCase))
        {
            var result = await service.ScriptableAssetAsync(
                new ScriptableAssetQueryRequest(sceneSnapshotId, fact.Selector),
                cancellationToken);
            if (result.Status != SceneQueryStatus.Resolved || result.ScriptFields is null)
            {
                failures.Add($"'{fact.Name}': asset selector did not resolve ({result.Status}).");
                return null;
            }

            return RequireDecoded(fact, result.ScriptFields, failures);
        }

        failures.Add($"'{fact.Name}': unknown owner '{fact.Owner}' (want component|asset).");
        return null;
    }

    private static SceneScriptFieldSetRecord? RequireDecoded(
        SceneFieldFact fact, SceneScriptFieldSetRecord fields, List<string> failures)
    {
        if (fields.Status != SceneScriptFieldSetStatus.Decoded)
        {
            failures.Add(
                $"'{fact.Name}': serialized fields unavailable ({fields.UnavailableReason}).");
            return null;
        }

        return fields;
    }

    private static async Task CheckCalleeFactAsync(
        IndexQueryService service,
        S1Atlas.Core.Storage.IndexRunRecord run,
        CalleeFact fact,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        var result = await service.CalleesInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed, fact.Selector,
            limit: 10000, cancellationToken);
        if (result.Resolution.Status != SymbolResolutionStatus.Resolved)
        {
            failures.Add($"'{fact.Name}': selector did not resolve ({result.Resolution.Status}).");
            return;
        }

        if (result.TotalCount != result.Relationships.Count)
        {
            failures.Add(
                $"'{fact.Name}': callee list truncated ({result.Relationships.Count} of " +
                $"{result.TotalCount}); raise the query limit.");
            return;
        }

        var unresolved = result.Relationships
            .Where(relationship => !relationship.Target.Resolved)
            .Select(relationship => relationship.Target.RawText ?? "?")
            .ToArray();
        if (unresolved.Length > 0)
        {
            failures.Add(
                $"'{fact.Name}': {unresolved.Length} unresolved callee(s): " +
                string.Join(", ", unresolved) + ".");
            return;
        }

        var actual = result.Relationships
            .Select(relationship => relationship.Target.QualifiedName ?? "?")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var expected = fact.ExpectedCallees.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            failures.Add(
                $"'{fact.Name}': callee set differs.{Environment.NewLine}" +
                $"  actual:   {string.Join(", ", actual)}{Environment.NewLine}" +
                $"  expected: {string.Join(", ", expected)}");
    }
}
