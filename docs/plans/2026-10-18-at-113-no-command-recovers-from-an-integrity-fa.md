# AT-113: Recover an integrity-failed preferred extraction

Use `s1atlas extract --retry` to quarantine corrupt owned output and reinstall freshly verified bytes under the same extraction ID, inside the existing extraction lock. Keep historical records and filesystem evidence; return a coded failure if any repair step fails. Plain extraction must fail cleanly without losing the corrupt preference, and readiness, authority hints, and setup must point to retry.

Scope: retain Tasks 1 and 4, with one replacement Task 2 for retry. No repair journal, crash-recovery table, startup repair recovery, or different-recipe retirement. New regression coverage is limited to one test per acceptance criterion (plain coded failure and successful explicit retry), plus one doctor progression check. Update existing assertions affected by changed guidance and retain existing regression coverage.

### Task 1: Make plain extraction fail cleanly and preserve the corrupt preference

**Files**

- Modify: `src/S1Atlas.Extraction/ValidatedExtractionWorkflow.cs`
- Modify: `src/S1Atlas.Extraction/ExtractionValidationService.cs`
- Inspect; change only if the regression demonstrates a gap: `src/S1Atlas.Cli/Commands/CommandExecution.cs`, `src/S1Atlas.Cli/Commands/ExtractCommand.cs`
- Test: `tests/S1Atlas.IntegrationTests/Extraction/Phase4ExtractionCliTests.cs`

**Context and contract**

`ValidatedExtractionWorkflow.TryReuseSelectedAsync` and `ExtractionValidationService.RevalidateExistingExtractionAsync` currently clear the preferred pointer before throwing `ExtractionOperationException(Recovery, IntegrityMismatch, ...)`. `CommandExecution.Run` already catches that exception and emits exit code 1, with `error.code`, `error.stage`, and a message. Establish the actual CLI regression first; do not add a second broad exception handler. The workflow may throw a typed operational exception internally; the CLI must return a normal failure envelope or human-readable error.

Keep the preferred pointer on these two ordinary reuse failures. It remains non-authoritative because consumers verify integrity, and preserving it lets doctor continue to identify corruption rather than suggesting an ineffective ordinary extraction. Keep preference clearing for policy invalidation and interrupted-promotion recovery separate.

**Implementation and tests**

1. Add one managed-tool integration test for the plain-failure acceptance criterion, checking JSON and human modes against the same modified artifact. Reuse `Phase4ExtractionCliFixture`, `InstallManagedTool`, `SeedBuildAsync`, and `ValidatedExtractionRoot`. The essential JSON case is:

   ```csharp
   [Fact]
   public async Task Extract_CorruptPreference_ReturnsCodedFailure()
   {
       var ct = TestContext.Current.CancellationToken;
       await using var fixture = new Phase4ExtractionCliFixture(
           new ScriptedProcessExtractor(ScriptedProcessOutcome.ValidManagedAssembly));
       fixture.InstallManagedTool();
       var buildId = await fixture.SeedBuildAsync();
       var first = fixture.Invoke("extract", "--json");
       Assert.Equal(0, first.ExitCode);
       using var firstJson = JsonDocument.Parse(first.StandardOutput);
       var extractionId = firstJson.RootElement.GetProperty("data")
           .GetProperty("extractionId").GetString()!;
       var artifact = Path.Combine(
           fixture.ValidatedExtractionRoot(buildId, extractionId),
           "reconstructed", "Assembly-CSharp.dll");
       await File.AppendAllTextAsync(artifact, "corruption", ct);

       var result = fixture.Invoke("extract", "--json");
       Assert.Equal(1, result.ExitCode);
       using var json = JsonDocument.Parse(result.StandardOutput);
       var error = json.RootElement.GetProperty("error");
       Assert.Equal("IntegrityMismatch", error.GetProperty("code").GetString());
       Assert.Equal("Recovery", error.GetProperty("stage").GetString());
       Assert.Contains("extract --retry", error.GetProperty("message").GetString()!);
       var repository = await fixture.OpenRepositoryAsync();
       Assert.Equal(extractionId,
           (await repository.GetPreferredExtractionAsync(buildId, ct))!.ExtractionId);
   }
   ```

   In the same test invoke human mode and assert exit 1, readable integrity/retry guidance, no stack trace, unchanged preferred ID, and unchanged extraction-attempt count. A successful `Invoke` returning these values also establishes that no exception escapes the CLI action.

2. In both integrity-failure branches remove the call to `ClearPreferredIfSelectedAsync` with reason `IntegrityInvalidated`. Keep the typed failure, including the verifier's detail, and append a retry command targeting the selected build. Use the full build ID in extraction-layer text so a request for a historical build cannot accidentally operate on the current build:

   ```csharp
   throw new ExtractionOperationException(
       ExtractionFailureStage.Recovery,
       ExtractionFailureCode.IntegrityMismatch,
       $"The validated extraction '{extraction.ExtractionId}' failed integrity " +
       $"verification: {integrity.Message} Run 's1atlas extract --retry " +
       $"--build {extraction.BuildId}' to regenerate it.");
   ```

   In `TryReuseSelectedAsync` use its `selected` variable in place of `extraction`. Remove the workflow's clearing helper if it becomes unused; retain the validation service's helper for policy failures.

3. Keep existing healthy no-op, policy revalidation, and policy-invalid preference-clearing coverage passing; do not add a separate workflow test matrix.

**Checks**

Run the new focused tests before the changes and observe the failing preference/guidance assertions. Then run:

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter FullyQualifiedName~ValidatedExtractionWorkflowTests
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter FullyQualifiedName~Phase4ExtractionCliTests
```

Expected: all pass; the existing retry-after-corruption test still describes the old retry behavior until Task 2. This task ends with a clean coded plain-extraction failure and its regression coverage.

### Task 2: Repair matching corrupt output during explicit retry

**Files**

- Modify: `src/S1Atlas.Extraction/ValidatedExtractionWorkflow.cs`
- Modify: `src/S1Atlas.Extraction/ExtractionValidationService.cs`
- Modify: `src/S1Atlas.Extraction/Promotion/ValidatedExtractionPromoter.cs`
- Reuse; modify only if needed for an owned quarantine destination: `src/S1Atlas.Extraction/Promotion/OwnedValidatedExtractionPaths.cs`
- Modify: `src/S1Atlas.Cli/Commands/ExtractCommand.cs`
- Test: `tests/S1Atlas.IntegrationTests/Extraction/Phase4ExtractionCliTests.cs`
- Extend only if needed: `tests/S1Atlas.IntegrationTests/Extraction/Phase4ExtractionCliFixture.cs`

**Context and contract**

`--retry` already runs Cpp2IL again. Identical candidate bytes resolve to the existing `DeduplicationTarget`, but `ValidatedExtractionPromoter.LinkExistingAsync` rejects its corrupt final directory. Repair that same-ID target only for explicit retry. `ExtractionValidationService.ValidateAndPromoteCandidateAsync` already holds the shared extraction lock through promotion: keep verification, quarantine, replacement, attempt persistence, and preference handling inside that lease.

Retain immutable extraction/artifact rows and the original `SourceAttemptId`; link a new successful attempt and fresh validation report to the same extraction ID. Preserve the existing preferred pointer and selection reason, including a manual selection; integrity checks continue to prevent corrupt or incomplete output from being authoritative. Do not delete rows, randomize identity, weaken validation, or introduce another lock.

**Implementation and test**

1. Replace `Extract_RetryAfterCorruption_FailsIntegrityMismatchWithoutRepairing` with one acceptance test, `Extract_RetryAfterCorruption_RepairsSameExtraction`. Use the existing managed-tool fixture and deterministic valid assembly output. Save the original ID, attempt ID, and artifact bytes; append corruption; invoke `extract --retry --json`. Assert:

   ```csharp
   Assert.Equal(0, retry.ExitCode);
   using var retryJson = JsonDocument.Parse(retry.StandardOutput);
   var data = retryJson.RootElement.GetProperty("data");
   Assert.True(data.GetProperty("processWasRun").GetBoolean());
   Assert.True(data.GetProperty("validationWasRun").GetBoolean());
   Assert.True(data.GetProperty("authoritative").GetBoolean());
   Assert.True(data.GetProperty("preferred").GetBoolean());
   Assert.False(data.GetProperty("reusedExistingExtraction").GetBoolean());
   Assert.Equal(originalExtractionId, data.GetProperty("extractionId").GetString());
   Assert.NotEqual(originalAttemptId, data.GetProperty("attemptId").GetString());
   ```

   In this same test verify restored artifact bytes, quarantine containing the damaged file, and successful `extractions show <id> --json`. Verify the preferred ID and original extraction/artifact/history records remain, with a new successful attempt and validation evidence. A subsequent plain extract must be a verified no-op. Leave doctor progression to Task 4.

2. Pass explicit retry permission from the workflow's fresh-process branch through validation into promotion:

   ```csharp
   public async Task<ExtractionWorkflowResult> ValidateAndPromoteCandidateAsync(
       ExtractionAttempt processCompleted,
       PreparedExtractionContext context,
       bool processWasRun,
       CancellationToken cancellationToken,
       bool allowIntegrityRepair = false)
   ```

   Append `bool AllowIntegrityRepair = false` to `ValidatedExtractionPromotionRequest` (declared in `ValidatedExtractionPromoter.cs`). Pass `allowIntegrityRepair: options.Retry` from the workflow and forward it into that request. Existing-candidate consumption defaults to false. Add `RepairedExistingExtraction` to `PromotionDisposition` and map it to a fresh authoritative result, not a reused extraction. No new CLI flag or output field is needed.

3. In `LinkExistingAsync`, keep healthy deduplication unchanged. For a corrupt matching target and `AllowIntegrityRepair`, perform a small private repair operation in the existing promoter:

   - Re-read the target and stored artifacts. Require candidate recipe, build, tool, manifest digest, and artifact entries to match the target. Reinspect candidate bytes against the validated manifest before moving them. Invalid candidates and different-ID output retain existing validation/promotion rules.
   - Derive final, attempt staging, and `quarantine/<extraction-id>-<attempt-id>` paths from owned IDs. Reuse containment/reparse-point checks for paths and moved trees. Refuse occupied staging/quarantine destinations and inaccessible or unsafe evidence; never overwrite them.
   - Retain the new attempt report; stage candidate bytes with the existing extraction record, candidate manifest, and fresh report, writing the complete marker last. Verify staging against stored extraction/artifact rows before disturbing the corrupt directory:

     ```csharp
     await _documentStore.WriteFinalDocumentsAsync(
         _dataRoot, stagingRoot, target, manifest, report, cancellationToken);
     var staged = await _integrityVerifier.VerifyAsync(
         _dataRoot, stagingRoot, target, storedArtifacts, cancellationToken);
     if (staged.Status != ValidatedExtractionIntegrityStatus.Valid)
         throw new ExtractionOperationException(
             ExtractionFailureStage.FilesystemPromotion,
             ExtractionFailureCode.FilesystemPromotionFailed,
             $"The staged replacement failed integrity verification: {staged.Message}");
     ```

   - Move the corrupt final directory to quarantine, then rename verified staging into the same final path. If the final directory is missing, install without inventing quarantine evidence. Reverify installed output with the repository-backed verifier.
   - Only after verification, build the successful attempt and call `LinkAttemptToValidatedExtractionAsync(new ValidationPersistence(succeeded, report), target, ExtractionAttemptStatus.Validating, cancellationToken)`. Preserve the existing preference; if none exists, use existing preference policy. Read the actual pointer for `IsPreferred`. Never grant an unselected custom output automatic preference.
   - On any repair failure retain all available corrupt, staged, installed, report, and attempt evidence, irrespective of `KeepFailedArtifacts`. Do not delete quarantine or restore corrupt bytes as authoritative. Map filesystem/verification failures to `FilesystemPromotionFailed` and persistence/preference failures to `DatabasePromotionFailed` using `ExtractionOperationException`; cancellation follows existing coded CLI handling. Return success only after every step succeeds. Include attempt ID and retained evidence locations in failure guidance. Add no automatic resume machinery.

4. Update `--retry` help to: `Run a new Cpp2IL process and repair corrupt matching output after validation.` Keep input checks, archived replay certification, custom-output rules, and `SameRecipeDifferentOutput` behavior intact. Do not implement different-recipe retirement.

**Checks**

Run the replacement acceptance test before implementation to confirm the old integrity rejection, then run:

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter "FullyQualifiedName~Extraction|FullyQualifiedName~InputSnapshot"
```

Expected: all pass. Review repair failure exits for coded reporting and retained evidence; add no failure-injection matrix or separate repair test suite.

### Task 4: Advertise the recovery command consistently in readiness, authority, and setup

**Files**

- Modify: `src/S1Atlas.Application/Readiness/AtlasReadinessService.cs`
- Modify: `src/S1Atlas.Application/Readiness/ReadinessFixCommands.cs`
- Modify: `src/S1Atlas.Application/Authority/InstalledBuildAuthorityResolver.cs`
- Inspect; modify only if tests expose a gap: `src/S1Atlas.Cli/Commands/SetupCommand.cs`
- Test: `tests/S1Atlas.Indexing.Tests/Readiness/AtlasReadinessServiceTests.cs`
- Test: `tests/S1Atlas.Indexing.Tests/InstalledBuildAuthorityHintTests.cs`
- Test: `tests/S1Atlas.Indexing.Tests/AuthorityEnvelopeHintTests.cs`
- Test: `tests/S1Atlas.IntegrationTests/AuthorityHintCliTests.cs`
- Test: `tests/S1Atlas.IntegrationTests/StatusReadinessTests.cs`
- Test: `tests/S1Atlas.IntegrationTests/Extraction/Phase4ExtractionCliTests.cs`
- Test: `tests/S1Atlas.IntegrationTests/SetupCommandTests.cs`
- Test: `tests/S1Atlas.Mcp.Tests/AuthorityHintStdioTests.cs`
- Update: `docs/USAGE.md`, `docs/REFERENCE.md`

**Context and interfaces**

`ReadinessFixCommands.ExtractRetry` already equals `s1atlas extract --retry`. `SetupCommand` matches this exact constant to construct `ExtractionOptions` with `Retry = true`; scene setup additionally sets `SnapshotInputs = true` and displays `ExtractSnapshotInputsRetry`. Reuse that mapping. Tasks 1 and 2 establish coded plain failures and same-ID retry repair; identical bytes can keep the extraction ID while a fresh attempt and validation report establish recovery.

**Implementation and tests**

1. Replace the integrity-failed extraction readiness item's no-command/delete-data guidance with a repair explanation and `ReadinessFixCommands.ExtractRetry`. Keep the item non-OK until verification succeeds. Update tests currently asserting a null fix command and `No command rebuilds it in place`:

   ```csharp
   Assert.Equal(ReadinessState.Missing, extraction.State);
   Assert.Equal(ReadinessFixCommands.ExtractRetry, extraction.FixCommand);
   Assert.Contains("integrity", extraction.Detail, StringComparison.OrdinalIgnoreCase);
   Assert.DoesNotContain("delete the atlas data", extraction.Detail,
       StringComparison.OrdinalIgnoreCase);
   ```

2. Add a build-targeted helper and use it for a non-current build's integrity failure. Full IDs avoid ambiguous short-prefix hints:

   ```csharp
   public static string ExtractRetryBuild(string buildId)
   {
       ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
       return $"s1atlas extract --retry --build {buildId}";
   }
   ```

   Update the authority-status switch arm:

   ```csharp
   InstalledBuildAuthorityStatus.ExtractionIntegrityFailure =>
       resolvedBuildId is not null && !targetsCurrentBuild
           ? ExtractRetryBuild(resolvedBuildId)
           : ExtractRetry,
   ```

   In `InstalledBuildAuthorityResolver.ResolveAsync`, pass `await CurrentBuildIdAsync(ct)` into `Fail` for `ExtractionIntegrityFailure`, just as the missing-preference branch already does. Without this change even a current-build failure gets mistaken for a historical-build request. Preserve null hints for unrelated ambiguous/mismatched authority states. For explicit historical builds, the targeted command still enforces existing matching-live-input requirements; do not invent a replay snapshot ID or silently switch builds.

3. Update existing authority/envelope assertions that expect null integrity-failure hints to expect retry (targeted with the full ID for a historical build). Keep existing CLI/MCP error codes and read-only behavior. Update existing setup assertions to retain the canonical `ExtractRetry` mapping to `Retry = true`, scene setup passing both snapshot and retry flags, and failures stopping before indexing. Do not add separate authority, setup, or historical-build test matrices.

4. Add one doctor progression regression in `tests/S1Atlas.IntegrationTests/Extraction/Phase4ExtractionCliTests.cs`: corrupt artifact -> doctor suggests retry -> plain extraction fails cleanly and leaves the same retry guidance -> explicit retry succeeds -> doctor marks extraction OK and suggests `s1atlas index`. Start without a completed index so the assertion is deterministic. Doctor may still return nonzero readiness because indexing is missing; assert the extraction item's state and next command, not overall readiness success.

5. Update the extraction/recovery sections in `docs/USAGE.md` and `docs/REFERENCE.md` with `s1atlas extract --retry`, quarantine behavior, new-attempt/same-content-ID semantics, normal matching-input/tool requirements, coded plain failure, and doctor/setup progression. Document that custom outputs and same-recipe differing output still obey existing promotion rules. Remove obsolete statements that integrity corruption requires deleting the data root. Do not change unrelated native-body recovery documentation.

**Checks**

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter "FullyQualifiedName~Readiness|FullyQualifiedName~Authority"
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter "FullyQualifiedName~SetupCommandTests|FullyQualifiedName~StatusReadinessTests|FullyQualifiedName~AuthorityHintCliTests|FullyQualifiedName~Phase4ExtractionCliTests"
dotnet test tests/S1Atlas.Mcp.Tests/S1Atlas.Mcp.Tests.csproj --filter FullyQualifiedName~AuthorityHintStdioTests
dotnet test S1Atlas.sln
git diff --check
```

Expected: focused and solution tests pass, with one regression test per acceptance criterion and the doctor progression check exercised through the real CLI fixture. Report any environment-dependent skipped tests or unavailable test prerequisites explicitly. This task ends with code, guidance, documentation, and checks in place.
