using Xunit;

namespace S1Atlas.Docs.Tests;

public sealed class AgentUsageContractTests
{
    [Fact]
    public void CanonicalSkillDefinesTheFullAgentContract()
    {
        var root = FindRepositoryRoot();
        var skill = File.ReadAllText(Path.Combine(root, "skills", "s1atlas", "SKILL.md")).ReplaceLineEndings("\n");
        var normalizedSkill = NormalizeWhitespace(skill);

        AssertPortableContract(skill);
        Assert.Contains("### Host parity and efficient use", skill, StringComparison.Ordinal);
        Assert.Contains("## Authority boundary and provenance contract", skill, StringComparison.Ordinal);
        Assert.Contains("list completed collections once, choose one explicit collection", normalizedSkill, StringComparison.Ordinal);
        Assert.Contains("Resolve the exact symbol before reading source", normalizedSkill, StringComparison.Ordinal);
        Assert.Contains("Read the focused span first, then request only the bounded callers, callees, call sites, field references, or related types needed for the question", normalizedSkill, StringComparison.Ordinal);
        Assert.Contains("Do not repeat equivalent MCP and CLI queries", normalizedSkill, StringComparison.Ordinal);
        Assert.Contains("Static relationship evidence, callability, and source runtime-verification hints remain distinct from live runtime behavior", normalizedSkill, StringComparison.Ordinal);
        Assert.Contains("Carry the returned build, extraction, index, collection, mod, relative-path, and content-hash provenance into the decision", normalizedSkill, StringComparison.Ordinal);
        Assert.Contains("behavioral question", normalizedSkill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pinned provenance", normalizedSkill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("candidate symbol and role", normalizedSkill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("body/callability coverage", normalizedSkill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authority/entity attribution", normalizedSkill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alternate/generic callers", normalizedSkill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lifecycle position and before/after state", normalizedSkill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("API-before-patch result", normalizedSkill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("remaining UNKNOWNs", normalizedSkill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bounded next action", normalizedSkill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("event names are not lifecycle proof", normalizedSkill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing or incomplete callers must not be reported as no callers", normalizedSkill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Negative-seam result", skill, StringComparison.Ordinal);
        Assert.Contains("Runtime-proof plan", skill, StringComparison.Ordinal);
        Assert.Contains("both surfaces expose the S1API/S1MAPI query path", normalizedSkill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("find_api_callers", skill, StringComparison.Ordinal);
        Assert.Contains("plan_runtime_proof", skill, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalSkillRejectsInsufficientOwnershipRecommendations()
    {
        const string insufficientOwnershipFixture = """
            Candidate symbol: InventoryPanel.RefreshSlots
            Friendly event name: inventory refreshed
            Callback order: runs after ApplyInventoryState
            Visible result: the toolbar shows the equipped item
            """;

        var root = FindRepositoryRoot();
        var skill = File.ReadAllText(Path.Combine(root, "skills", "s1atlas", "SKILL.md")).ReplaceLineEndings("\n");
        var normalizedSkill = NormalizeWhitespace(skill);

        Assert.Contains("Candidate symbol:", insufficientOwnershipFixture, StringComparison.Ordinal);
        Assert.Contains("Friendly event name:", insufficientOwnershipFixture, StringComparison.Ordinal);
        Assert.Contains("Callback order:", insufficientOwnershipFixture, StringComparison.Ordinal);
        Assert.Contains("Visible result:", insufficientOwnershipFixture, StringComparison.Ordinal);
        Assert.Contains("insufficient for an ownership recommendation", normalizedSkill, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UsagePointsToTheSkillAndRetainsOperationalSafetyFacts()
    {
        var root = FindRepositoryRoot();
        var usage = File.ReadAllText(Path.Combine(root, "docs", "USAGE.md")).ReplaceLineEndings("\n");
        var normalizedUsage = NormalizeWhitespace(usage);

        Assert.Contains("[`skills/s1atlas/SKILL.md`](../skills/s1atlas/SKILL.md)", usage, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project src/S1Atlas.Mcp -- mcp serve", usage, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project <local-S1Atlas-root>/src/S1Atlas.Mcp/S1Atlas.Mcp.csproj -- mcp serve", usage, StringComparison.Ordinal);
        Assert.Contains("Each host registration should enable the read-only server and use bounded startup/tool timeouts, with those settings kept in user-level config.", normalizedUsage, StringComparison.Ordinal);
        Assert.Contains("skill's CLI", usage, StringComparison.Ordinal);
        Assert.Contains("commands remain the fallback", usage, StringComparison.Ordinal);
        Assert.Contains("read-only server entry point", usage, StringComparison.Ordinal);
        Assert.Contains("Host configuration and reference manifests stay outside the repository", usage, StringComparison.Ordinal);
        Assert.Contains("S1Atlas does not download mods.", normalizedUsage, StringComparison.Ordinal);
        Assert.DoesNotContain("### Host parity and efficient use", usage, StringComparison.Ordinal);
        Assert.DoesNotContain("For prior-art work, list completed collections once", usage, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageDocumentsInvestigateSeamParityAndReadOnlyBoundaries()
    {
        var root = FindRepositoryRoot();
        var usage = File.ReadAllText(Path.Combine(root, "docs", "USAGE.md")).ReplaceLineEndings("\n");
        var normalizedUsage = NormalizeWhitespace(usage);

        Assert.Contains("dotnet run --project src/S1Atlas.Cli -- investigate_seam", usage, StringComparison.Ordinal);
        Assert.Contains("`investigate_seam`", usage, StringComparison.Ordinal);
        Assert.Contains("`investigate_seam <selector> --question <text>", usage, StringComparison.Ordinal);
        Assert.Contains("[--native-symbol-id <id>] [--native-traversal-budget <0-500>]", usage, StringComparison.Ordinal);
        Assert.Contains("repeated `--native-symbol-id`, `--native-traversal-budget` from `0` to `500`", normalizedUsage, StringComparison.Ordinal);
        Assert.Contains("The MCP surface accepts `selector`, `behavioralQuestion`, `buildId`, `scope`, `collection`, `relationshipLimit`, `ownerLimit`, `context`, `details`, `nativeSymbolIds`, and `nativeTraversalBudget`.", normalizedUsage, StringComparison.Ordinal);
        Assert.Contains("MCP already returns the structured result, so there is no extra `json` argument on the MCP tool.", normalizedUsage, StringComparison.Ordinal);
        Assert.Contains("CLI JSON envelope and the MCP tool share the same payload contract", normalizedUsage, StringComparison.Ordinal);
        Assert.Contains("`pinnedProvenance`, `authorityEntityAttribution`, `alternateGenericCallersAndExclusivity`, `lifecyclePositionAndBeforeAfterState`, and `apiBeforePatchResult`", normalizedUsage, StringComparison.Ordinal);
        Assert.Contains("With `details` off, `claims` and `evidenceSections` are empty while the complete decision packet and all five gate records remain present; with `details` on, only those two evidence arrays are populated.", normalizedUsage, StringComparison.Ordinal);
        Assert.Contains("CLI-only `referenceCollectionBaseProvenance`", normalizedUsage, StringComparison.Ordinal);
        Assert.Contains("The CLI reports resolved research as `success: true` with exit code `0`; MCP reports the same packet with `status: resolved`.", normalizedUsage, StringComparison.Ordinal);
        Assert.Contains("deterministic owner-candidate order", normalizedUsage, StringComparison.Ordinal);
        Assert.Contains("does not emit a confidence score", normalizedUsage, StringComparison.Ordinal);
        Assert.Contains("FACT`/`DERIVED` claims and separate `unknownDimensions`", usage, StringComparison.Ordinal);
        Assert.Contains("read-only investigation: it does not patch code, run native recovery automatically, or prove runtime behavior", normalizedUsage, StringComparison.Ordinal);
        Assert.Contains("matching stored native-evidence summary", normalizedUsage, StringComparison.Ordinal);
        Assert.Contains("The read-only MCP API parity tools are", normalizedUsage, StringComparison.Ordinal);
        Assert.Contains("`find_api_related_types`", normalizedUsage, StringComparison.Ordinal);
        Assert.Contains("`plan_runtime_proof` tool", normalizedUsage, StringComparison.Ordinal);
        Assert.Contains("must not be transferred between host roles", normalizedUsage, StringComparison.Ordinal);
    }

    [Fact]
    public void ReferenceDocumentsSeamResultStatesCoverageAndNextActionBoundaries()
    {
        var root = FindRepositoryRoot();
        var reference = File.ReadAllText(Path.Combine(root, "docs", "REFERENCE.md")).ReplaceLineEndings("\n");
        var normalizedReference = NormalizeWhitespace(reference);

        Assert.Contains("`SupportableSeam`, `NoSupportableSeam`, and `InsufficientCoverage`", reference, StringComparison.Ordinal);
        Assert.Contains("`Complete`, `Bounded`, `Incomplete`, `Unavailable`, and `NotApplicable`", reference, StringComparison.Ordinal);
        Assert.Contains("Treat every entry in `unknownDimensions` as a literal `UNKNOWN` classification, not as a confidence score.", normalizedReference, StringComparison.Ordinal);
        Assert.Contains("`InsufficientCoverage` is the service-gate outcome whenever mandatory evidence is `Incomplete` or `Unavailable`, including incomplete or unavailable caller coverage.", normalizedReference, StringComparison.Ordinal);
        Assert.Contains("`InsufficientCoverage` and `NoSupportableSeam` are both resolved research outcomes; CLI may return `success: true` and MCP may return `status: resolved` while preserving either conclusion.", normalizedReference, StringComparison.Ordinal);
        Assert.Contains("Once mandatory evidence is complete, `NoSupportableSeam` is reserved for complete evidence that establishes no supportable owner, such as no candidate, competing candidates, generic-only ownership coverage, or a remaining literal `UNKNOWN` dimension.", normalizedReference, StringComparison.Ordinal);
        Assert.Contains("Example: if complete evidence leaves `Game.Seams.CompleteEvidenceTarget` with competing owner candidates, the investigation remains a successful resolved `NoSupportableSeam` result.", normalizedReference, StringComparison.Ordinal);
        Assert.Contains("Native recovery and runtime proof are next actions only; S1Atlas never executes either automatically", normalizedReference, StringComparison.Ordinal);
        Assert.Contains("An explicit native lookup uses `nativeSymbolIds` plus a `nativeTraversalBudget` from `0` to `500`; zero means no lookup.", normalizedReference, StringComparison.Ordinal);
        Assert.Contains("The lookup also reports `Matched`, `NoMatch`, or `InputChanged` separately from recovery status", normalizedReference, StringComparison.Ordinal);
        Assert.Contains("MCP provenance entries carry `source`, `buildId`, `extractionId`, and `indexId`", normalizedReference, StringComparison.Ordinal);
        Assert.Contains("The shared CLI/MCP data packet carries `pinnedProvenance`, `authorityEntityAttribution`, `alternateGenericCallersAndExclusivity`, `lifecyclePositionAndBeforeAfterState`, and `apiBeforePatchResult` in both detail modes.", normalizedReference, StringComparison.Ordinal);
        Assert.Contains("CLI seam results additionally expose nullable `referenceCollectionBaseProvenance`", normalizedReference, StringComparison.Ordinal);
        Assert.Contains("For `scope: reference`, it identifies the installed Schedule I build/extraction/index that the selected reference collection pins as its base authority", normalizedReference, StringComparison.Ordinal);
        Assert.Contains("`details: false` keeps `claims` and `evidenceSections` empty without removing any decision, coverage, provenance, authority, or gate record; `details: true` populates those two arrays.", normalizedReference, StringComparison.Ordinal);
        Assert.Contains("MCP adapter statuses are `resolved`, `not_found`, `ambiguous`, `unavailable`, and `invalid`", normalizedReference, StringComparison.Ordinal);
        Assert.Contains("CLI failures remain nonzero error envelopes and never become resolved research packets.", normalizedReference, StringComparison.Ordinal);
        Assert.Contains("`NoSupportableSeam` is reserved for", reference, StringComparison.Ordinal);
        Assert.Contains("`find_api_callers`, `find_api_callees`, `find_api_references`", normalizedReference, StringComparison.Ordinal);
        Assert.Contains("`plan_runtime_proof` is a bounded planning surface", normalizedReference, StringComparison.Ordinal);
        Assert.Contains("`singlePlayer`, `listenHost`, `dedicatedServer`, or `client`", normalizedReference, StringComparison.Ordinal);
    }

    private static string NormalizeWhitespace(string document)
    {
        return string.Join(" ", document.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static void AssertPortableContract(string document)
    {
        Assert.Contains("list_reference_collections", document, StringComparison.Ordinal);
        Assert.Contains("CLI's JSON output", document, StringComparison.Ordinal);
        Assert.Contains("missing server as an empty index", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content hash", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read-only", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not download mods", document, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "S1Atlas.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
