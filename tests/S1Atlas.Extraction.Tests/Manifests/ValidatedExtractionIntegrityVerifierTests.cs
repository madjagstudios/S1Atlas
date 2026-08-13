using System.Security.Cryptography;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Hashing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Extraction.Promotion;
using Xunit;

namespace S1Atlas.Extraction.Tests.Manifests;

public sealed class ValidatedExtractionIntegrityVerifierTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(), $"s1atlas-integrity-verifier-{Guid.NewGuid():N}");
    private readonly RecordingFileHasher _hasher = new();

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyAsync_CompleteMatchingExtraction_ReturnsValid()
    {
        var (extraction, manifest, _, documentsRoot) = await BuildAndWriteValidExtractionAsync();
        var verifier = CreateVerifier();

        var result = await verifier.VerifyAsync(
            _dataRoot, documentsRoot, extraction, manifest.Entries, TestContext.Current.CancellationToken);

        Assert.Equal(ValidatedExtractionIntegrityStatus.Valid, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_ExplicitFacts_RepositoryBacked_ValidExtraction_ReturnsValid()
    {
        var (extraction, manifest, _, _) = await BuildAndWriteValidExtractionAsync();
        var repository = new FakeValidatedExtractionRepository(extraction, manifest.Entries);
        var verifier = CreateVerifier(repository);

        var result = await verifier.VerifyAsync(
            _dataRoot, extraction.BuildId, extraction.ExtractionId, TestContext.Current.CancellationToken);

        Assert.Equal(ValidatedExtractionIntegrityStatus.Valid, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_RepositoryHasNoRow_ReturnsMissing()
    {
        var repository = new FakeValidatedExtractionRepository(null, []);
        var verifier = CreateVerifier(repository);

        var result = await verifier.VerifyAsync(
            _dataRoot, "build-a", new string('7', 64), TestContext.Current.CancellationToken);

        Assert.Equal(ValidatedExtractionIntegrityStatus.Missing, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_RootDoesNotExist_ReturnsMissing()
    {
        var (extraction, manifest, _, _) = await BuildAndWriteValidExtractionAsync();
        var missingRoot = Path.Combine(_dataRoot, "builds", "build-a", "extractions", "does-not-exist");
        var verifier = CreateVerifier();

        var result = await verifier.VerifyAsync(
            _dataRoot, missingRoot, extraction, manifest.Entries, TestContext.Current.CancellationToken);

        Assert.Equal(ValidatedExtractionIntegrityStatus.Missing, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_MarkerMissing_ReturnsIncomplete()
    {
        var (extraction, manifest, _, documentsRoot) = await BuildAndWriteValidExtractionAsync();
        File.Delete(Path.Combine(documentsRoot, "complete.marker"));
        var verifier = CreateVerifier();

        var result = await verifier.VerifyAsync(
            _dataRoot, documentsRoot, extraction, manifest.Entries, TestContext.Current.CancellationToken);

        Assert.Equal(ValidatedExtractionIntegrityStatus.Incomplete, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_ExtractionJsonMissing_ReturnsIncomplete()
    {
        var (extraction, manifest, _, documentsRoot) = await BuildAndWriteValidExtractionAsync();
        File.Delete(Path.Combine(documentsRoot, "extraction.json"));
        var verifier = CreateVerifier();

        var result = await verifier.VerifyAsync(
            _dataRoot, documentsRoot, extraction, manifest.Entries, TestContext.Current.CancellationToken);

        Assert.Equal(ValidatedExtractionIntegrityStatus.Incomplete, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_DocumentBytesChangedAfterWrite_ReturnsMismatchWithoutFollowingHashesBlindly()
    {
        var (extraction, manifest, _, documentsRoot) = await BuildAndWriteValidExtractionAsync();
        // Append bytes to validation.json: still strictly parseable JSON (trailing
        // whitespace), but its SHA-256 no longer matches what complete.marker bound.
        await File.AppendAllTextAsync(
            Path.Combine(documentsRoot, "validation.json"), "\n", TestContext.Current.CancellationToken);
        var verifier = CreateVerifier();

        var result = await verifier.VerifyAsync(
            _dataRoot, documentsRoot, extraction, manifest.Entries, TestContext.Current.CancellationToken);

        Assert.Equal(ValidatedExtractionIntegrityStatus.Mismatch, result.Status);
        Assert.Equal("MarkerHashMismatch", result.Code);
    }

    [Fact]
    public async Task VerifyAsync_ArtifactMissingFromReconstructedTree_ReturnsMismatch()
    {
        var (extraction, manifest, _, documentsRoot) = await BuildAndWriteValidExtractionAsync();
        File.Delete(Path.Combine(documentsRoot, "reconstructed", "Assembly-CSharp.dll"));
        var verifier = CreateVerifier();

        var result = await verifier.VerifyAsync(
            _dataRoot, documentsRoot, extraction, manifest.Entries, TestContext.Current.CancellationToken);

        Assert.Equal(ValidatedExtractionIntegrityStatus.Mismatch, result.Status);
        Assert.Equal("ArtifactPathSetMismatch", result.Code);
    }

    [Fact]
    public async Task VerifyAsync_ExtraUntrackedArtifactInReconstructedTree_ReturnsMismatch()
    {
        var (extraction, manifest, _, documentsRoot) = await BuildAndWriteValidExtractionAsync();
        File.WriteAllBytes(Path.Combine(documentsRoot, "reconstructed", "extra.dll"), [9, 9, 9]);
        var verifier = CreateVerifier();

        var result = await verifier.VerifyAsync(
            _dataRoot, documentsRoot, extraction, manifest.Entries, TestContext.Current.CancellationToken);

        Assert.Equal(ValidatedExtractionIntegrityStatus.Mismatch, result.Status);
        Assert.Equal("ArtifactPathSetMismatch", result.Code);
    }

    [Fact]
    public async Task VerifyAsync_ArtifactContentChangedSameSize_ReturnsMismatch()
    {
        var (extraction, manifest, fileBytes, documentsRoot) = await BuildAndWriteValidExtractionAsync();
        var mutated = (byte[])fileBytes.Clone();
        mutated[0] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(documentsRoot, "reconstructed", "Assembly-CSharp.dll"), mutated);
        var verifier = CreateVerifier();

        var result = await verifier.VerifyAsync(
            _dataRoot, documentsRoot, extraction, manifest.Entries, TestContext.Current.CancellationToken);

        Assert.Equal(ValidatedExtractionIntegrityStatus.Mismatch, result.Status);
        Assert.Equal("ArtifactContentMismatch", result.Code);
    }

    [Fact]
    public async Task VerifyAsync_ReparsePointInReconstructedTree_ReturnsMismatchWithoutFollowing()
    {
        var (extraction, manifest, _, documentsRoot) = await BuildAndWriteValidExtractionAsync();
        var target = Path.Combine(_dataRoot, "reparse-target");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "hidden.dll"), [1, 2, 3]);
        var linkPath = Path.Combine(documentsRoot, "reconstructed", "linked");
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var verifier = CreateVerifier();

        var result = await verifier.VerifyAsync(
            _dataRoot, documentsRoot, extraction, manifest.Entries, TestContext.Current.CancellationToken);

        Assert.Equal(ValidatedExtractionIntegrityStatus.Mismatch, result.Status);
        Assert.Equal("ReconstructedTreeUnsafe", result.Code);
        Assert.DoesNotContain(
            _hasher.HashedPaths, path => path.StartsWith(linkPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyAsync_DatabaseExtractionRowDisagreesWithDocuments_ReturnsMismatch()
    {
        var (extraction, manifest, _, documentsRoot) = await BuildAndWriteValidExtractionAsync();
        var disagreeingExtraction = extraction with { ToolInstanceId = "a-different-tool" };
        var verifier = CreateVerifier();

        var result = await verifier.VerifyAsync(
            _dataRoot, documentsRoot, disagreeingExtraction, manifest.Entries,
            TestContext.Current.CancellationToken);

        Assert.Equal(ValidatedExtractionIntegrityStatus.Mismatch, result.Status);
        Assert.Equal("ExtractionRecordMismatch", result.Code);
    }

    [Fact]
    public async Task VerifyAsync_DatabaseArtifactRowsDisagreeWithManifest_ReturnsMismatch()
    {
        var (extraction, manifest, _, documentsRoot) = await BuildAndWriteValidExtractionAsync();
        var disagreeingArtifacts = new[] { manifest.Entries[0] with { Size = manifest.Entries[0].Size + 1 } };
        var verifier = CreateVerifier();

        var result = await verifier.VerifyAsync(
            _dataRoot, documentsRoot, extraction, disagreeingArtifacts, TestContext.Current.CancellationToken);

        Assert.Equal(ValidatedExtractionIntegrityStatus.Mismatch, result.Status);
        Assert.Equal("ArtifactRowMismatch", result.Code);
    }

    [Fact]
    public async Task VerifyAsync_DatabaseBackedRowsDisagree_RepositoryOverload_ReturnsMismatch()
    {
        var (extraction, manifest, _, _) = await BuildAndWriteValidExtractionAsync();
        var disagreeingArtifacts = new[] { manifest.Entries[0] with { Sha256 = new string('f', 64) } };
        var repository = new FakeValidatedExtractionRepository(extraction, disagreeingArtifacts);
        var verifier = CreateVerifier(repository);

        var result = await verifier.VerifyAsync(
            _dataRoot, extraction.BuildId, extraction.ExtractionId, TestContext.Current.CancellationToken);

        Assert.Equal(ValidatedExtractionIntegrityStatus.Mismatch, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_ManifestDigestRecomputationFails_ReturnsMismatch()
    {
        var (extraction, manifest, _, documentsRoot) = await BuildAndWriteValidExtractionAsync();
        var manifestJsonPath = Path.Combine(documentsRoot, "artifact-manifest.json");
        var markerPath = Path.Combine(documentsRoot, "complete.marker");
        var store = new ValidatedExtractionDocumentStore();
        var originalManifestSha256 = await store.TryComputeDocumentSha256Async(
            manifestJsonPath, TestContext.Current.CancellationToken);

        // Corrupt the manifest's sha256 value so recomputing the artifact digest
        // throws, then patch complete.marker to bind the corrupted document's new
        // bytes. This keeps the marker-hash check (an earlier step) passing, so the
        // test actually exercises extraction-ID/digest recomputation failure rather
        // than being pre-empted by a marker mismatch.
        var original = await File.ReadAllTextAsync(manifestJsonPath, TestContext.Current.CancellationToken);
        var realSha = manifest.Entries[0].Sha256;
        var corrupted = original.Replace(
            $"\"sha256\": \"{realSha}\"", "\"sha256\": \"not-a-valid-hex-digest\"", StringComparison.Ordinal);
        Assert.NotEqual(original, corrupted);
        await File.WriteAllTextAsync(manifestJsonPath, corrupted, TestContext.Current.CancellationToken);
        var corruptedManifestSha256 = await store.TryComputeDocumentSha256Async(
            manifestJsonPath, TestContext.Current.CancellationToken);

        var markerJson = await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken);
        var patchedMarker = markerJson.Replace(
            originalManifestSha256!, corruptedManifestSha256!, StringComparison.Ordinal);
        Assert.NotEqual(markerJson, patchedMarker);
        await File.WriteAllTextAsync(markerPath, patchedMarker, TestContext.Current.CancellationToken);

        var verifier = CreateVerifier();

        var result = await verifier.VerifyAsync(
            _dataRoot, documentsRoot, extraction, manifest.Entries, TestContext.Current.CancellationToken);

        Assert.Equal(ValidatedExtractionIntegrityStatus.Mismatch, result.Status);
        Assert.Equal("RecomputationFailed", result.Code);
    }

    [Fact]
    public async Task VerifyAsync_PolicyOnlyRevalidationAttempt_DoesNotChangeImmutableIntegrity()
    {
        var (extraction, manifest, _, documentsRoot) = await BuildAndWriteValidExtractionAsync();
        var verifier = CreateVerifier();
        var before = await verifier.VerifyAsync(
            _dataRoot, documentsRoot, extraction, manifest.Entries, TestContext.Current.CancellationToken);
        var validationJsonPath = Path.Combine(documentsRoot, "validation.json");
        var immutableReportBytes = await File.ReadAllBytesAsync(
            validationJsonPath, TestContext.Current.CancellationToken);

        // A policy-only revalidation writes only a brand-new attempt-level
        // validation.json at a completely different attempt root; it must never
        // touch the immutable final documents this extraction already committed.
        var revalidationAttemptId = "abcdefabcdefabcdefabcdefabcdefab";
        var attemptPaths = OwnedValidatedExtractionPaths.ForAttempt(
            _dataRoot, extraction.BuildId, revalidationAttemptId);
        var revalidationReport = new ValidationReport(
            SchemaVersion: 1,
            AttemptId: revalidationAttemptId,
            SubjectKind: ValidationSubjectKind.ValidatedExtraction,
            SubjectExtractionId: extraction.ExtractionId,
            BuildId: extraction.BuildId,
            RecipeId: extraction.RecipeId,
            PolicyId: "managed-assemblies-v1",
            PolicyVersion: 2,
            PolicyDigest: new string('6', 64),
            Outcome: ValidationOutcome.Valid,
            InputIntegrityPassed: true,
            ProcessIntegrityPassed: true,
            OutputContainmentPassed: true,
            ArtifactManifestDigest: extraction.ArtifactManifestDigest,
            Statistics: extraction.Statistics,
            BaselineExtractionId: extraction.ExtractionId,
            Comparisons: [],
            Issues: [],
            PreferenceEligible: true,
            ValidatedAtUtc: DateTimeOffset.Parse("2026-08-13T09:00:00Z"));
        var documentStore = new ValidatedExtractionDocumentStore();
        await documentStore.WriteAttemptValidationReportAsync(
            attemptPaths, revalidationReport, TestContext.Current.CancellationToken);

        var after = await verifier.VerifyAsync(
            _dataRoot, documentsRoot, extraction, manifest.Entries, TestContext.Current.CancellationToken);
        var afterBytes = await File.ReadAllBytesAsync(validationJsonPath, TestContext.Current.CancellationToken);

        Assert.Equal(ValidatedExtractionIntegrityStatus.Valid, before.Status);
        Assert.Equal(ValidatedExtractionIntegrityStatus.Valid, after.Status);
        Assert.Equal(immutableReportBytes, afterBytes);
    }

    private ValidatedExtractionIntegrityVerifier CreateVerifier(
        IValidatedExtractionRepository? repository = null) =>
        new(new ValidatedExtractionDocumentStore(), _hasher, repository);

    private async Task<(
        ValidatedExtraction Extraction,
        ArtifactManifest Manifest,
        byte[] FileBytes,
        string DocumentsRoot)> BuildAndWriteValidExtractionAsync(string buildId = "build-a")
    {
        Directory.CreateDirectory(_dataRoot);

        var recipeId = new string('1', 64);
        var fileBytes = new byte[] { 10, 20, 30, 40, 50, 60 };
        var sha256 = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();
        var entries = new[]
        {
            new ArtifactManifestEntry(
                RelativePath: "reconstructed/Assembly-CSharp.dll",
                Kind: ArtifactKind.ManagedAssembly,
                Size: fileBytes.Length,
                Sha256: sha256,
                AssemblyName: "Assembly-CSharp",
                ModuleName: "Assembly-CSharp.dll",
                TypeDefinitionCount: 5,
                MethodDefinitionCount: 10,
                FieldDefinitionCount: 2,
                PropertyDefinitionCount: 1,
                EventDefinitionCount: 0)
        };
        var manifest = new ArtifactManifest(1, entries);
        var digest = ArtifactManifestFingerprint.Create(manifest);
        var extractionId = ExtractionId.Create(recipeId, digest);
        var statistics = new ExtractionStatistics(
            ArtifactCount: 1,
            LibraryCount: 1,
            ManagedAssemblyCount: 1,
            TypeDefinitionCount: 5,
            MethodDefinitionCount: 10,
            FieldDefinitionCount: 2,
            PropertyDefinitionCount: 1,
            EventDefinitionCount: 0,
            TotalOutputBytes: fileBytes.Length,
            TotalManagedBytes: fileBytes.Length,
            Assemblies: [new AssemblyIdentityStatistics("Assembly-CSharp", 1, fileBytes.Length, 5, 10, 2, 1, 0)]);

        var extraction = new ValidatedExtraction(
            ExtractionId: extractionId,
            RecipeId: recipeId,
            BuildId: buildId,
            ToolInstanceId: "tool-1",
            SourceAttemptId: "0123456789abcdef0123456789abcdef",
            ProfileId: "profile",
            ProfileVersion: 1,
            ProfileDigest: new string('2', 64),
            AdapterVersion: 1,
            ExtractionSchemaVersion: 1,
            ArtifactManifestDigest: digest,
            RootPath: "placeholder-root",
            CreatedAtUtc: DateTimeOffset.Parse("2026-08-12T12:00:00Z"),
            TrustLevel: ToolTrustLevel.ManagedPinned,
            InitialValidationOutcome: ValidationOutcome.Valid,
            Statistics: statistics);

        var report = new ValidationReport(
            SchemaVersion: 1,
            AttemptId: "0123456789abcdef0123456789abcdef",
            SubjectKind: ValidationSubjectKind.CandidateOutput,
            SubjectExtractionId: null,
            BuildId: buildId,
            RecipeId: recipeId,
            PolicyId: "managed-assemblies-v1",
            PolicyVersion: 1,
            PolicyDigest: new string('3', 64),
            Outcome: ValidationOutcome.Valid,
            InputIntegrityPassed: true,
            ProcessIntegrityPassed: true,
            OutputContainmentPassed: true,
            ArtifactManifestDigest: digest,
            Statistics: statistics,
            BaselineExtractionId: null,
            Comparisons: [],
            Issues: [],
            PreferenceEligible: true,
            ValidatedAtUtc: DateTimeOffset.Parse("2026-08-12T12:05:00Z"));

        var extractionPaths = OwnedValidatedExtractionPaths.ForExtraction(_dataRoot, buildId, extractionId);
        var documentsRoot = extractionPaths.FinalExtractionRoot!;
        var reconstructedRoot = Path.Combine(documentsRoot, "reconstructed");
        Directory.CreateDirectory(reconstructedRoot);
        File.WriteAllBytes(Path.Combine(reconstructedRoot, "Assembly-CSharp.dll"), fileBytes);

        var store = new ValidatedExtractionDocumentStore();
        await store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, extraction, manifest, report, TestContext.Current.CancellationToken);

        return (extraction, manifest, fileBytes, documentsRoot);
    }

    private sealed class RecordingFileHasher : IFileHasher
    {
        private readonly Sha256FileHasher _inner = new();
        private readonly List<string> _hashedPaths = [];

        public IReadOnlyList<string> HashedPaths => _hashedPaths;

        public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
        {
            _hashedPaths.Add(path);
            return await _inner.ComputeSha256Async(path, cancellationToken);
        }
    }

    private sealed class FakeValidatedExtractionRepository(
        ValidatedExtraction? extraction, IReadOnlyList<ArtifactManifestEntry> artifacts)
        : IValidatedExtractionRepository
    {
        public Task<IReadOnlyList<ExtractionAttempt>> ListProcessCompletedAttemptsAsync(
            string recipeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ValidatedExtraction?> GetValidatedExtractionAsync(
            string extractionId, CancellationToken cancellationToken) =>
            Task.FromResult(extraction);

        public Task<IReadOnlyList<ArtifactManifestEntry>> GetExtractionArtifactsAsync(
            string extractionId, CancellationToken cancellationToken) =>
            Task.FromResult(artifacts);

        public Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsAsync(
            string? buildId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsByRecipeAsync(
            string recipeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoredValidationResult?> GetLatestValidationResultAsync(
            string extractionId, string policyDigest, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PreferredExtraction?> GetPreferredExtractionAsync(
            string buildId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ExtractionAttempt>> ListAttemptsAsync(
            string? buildId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveValidationFailureAsync(
            ValidationPersistence validation, ExtractionAttemptStatus expectedStatus,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CommitValidatedExtractionAsync(
            ValidatedExtractionPromotion promotion, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task LinkAttemptToValidatedExtractionAsync(
            ValidationPersistence validation, ValidatedExtraction extractionRow,
            ExtractionAttemptStatus expectedStatus, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveRevalidationAsync(
            ValidationPersistence validation, ExtractionAttemptStatus expectedStatus,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetPreferredExtractionAsync(
            PreferredExtraction preference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ClearPreferredExtractionAsync(
            string buildId, string expectedExtractionId, ExtractionPreferenceReason reason,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
