using System.Text;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Extraction.Promotion;
using Xunit;

namespace S1Atlas.Extraction.Tests.Manifests;

public sealed class ValidatedExtractionDocumentStoreTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(), $"s1atlas-validated-doc-store-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WriteFinalDocumentsAsync_ValidInputs_WritesAllFourStrictDocuments()
    {
        var documentsRoot = CreateDocumentsRoot();
        var (extraction, manifest, report) = BuildValidSet();
        var store = new ValidatedExtractionDocumentStore();

        var result = await store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, extraction, manifest, report,
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(result.Paths.ExtractionJsonPath));
        Assert.True(File.Exists(result.Paths.ArtifactManifestJsonPath));
        Assert.True(File.Exists(result.Paths.ValidationJsonPath));
        Assert.True(File.Exists(result.Paths.CompleteMarkerPath));
        Assert.Equal(extraction.ArtifactManifestDigest, result.ArtifactManifestDigest);
        Assert.Equal(extraction.ExtractionId, result.ExtractionId);
    }

    [Fact]
    public async Task WriteFinalDocumentsAsync_WritesUtf8NoBomIndentedCamelCaseJson()
    {
        var documentsRoot = CreateDocumentsRoot();
        var (extraction, manifest, report) = BuildValidSet();
        var store = new ValidatedExtractionDocumentStore();

        var result = await store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, extraction, manifest, report,
            TestContext.Current.CancellationToken);

        foreach (var path in new[]
                 {
                     result.Paths.ExtractionJsonPath, result.Paths.ArtifactManifestJsonPath,
                     result.Paths.ValidationJsonPath, result.Paths.CompleteMarkerPath
                 })
        {
            var bytes = File.ReadAllBytes(path);
            Assert.False(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                $"'{path}' must not start with a UTF-8 BOM.");
            var text = Encoding.UTF8.GetString(bytes);
            Assert.Contains("\n", text, StringComparison.Ordinal);
            Assert.Contains("\"schemaVersion\": 1", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"SchemaVersion\"", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WriteFinalDocumentsAsync_MarkerBindsExactShaOfTheThreeOtherDocuments()
    {
        var documentsRoot = CreateDocumentsRoot();
        var (extraction, manifest, report) = BuildValidSet();
        var store = new ValidatedExtractionDocumentStore();

        var result = await store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, extraction, manifest, report,
            TestContext.Current.CancellationToken);

        var marker = await store.TryReadCompleteMarkerAsync(
            result.Paths.CompleteMarkerPath, TestContext.Current.CancellationToken);
        Assert.NotNull(marker);
        Assert.Equal(
            await store.TryComputeDocumentSha256Async(
                result.Paths.ExtractionJsonPath, TestContext.Current.CancellationToken),
            marker!.ExtractionDocumentSha256);
        Assert.Equal(
            await store.TryComputeDocumentSha256Async(
                result.Paths.ArtifactManifestJsonPath, TestContext.Current.CancellationToken),
            marker.ArtifactManifestDocumentSha256);
        Assert.Equal(
            await store.TryComputeDocumentSha256Async(
                result.Paths.ValidationJsonPath, TestContext.Current.CancellationToken),
            marker.ValidationDocumentSha256);
        Assert.Equal(extraction.ExtractionId, marker.ExtractionId);
        Assert.Equal(extraction.ArtifactManifestDigest, marker.ArtifactManifestDigest);
    }

    [Fact]
    public async Task WriteFinalDocumentsAsync_MarkerIsWrittenLast_PriorFailureLeavesNoMarker()
    {
        var documentsRoot = CreateDocumentsRoot();
        var (extraction, manifest, report) = BuildValidSet();
        // Pre-seed a validation.json so the store's own non-overwriting move for the
        // third document collides, simulating a mid-sequence failure.
        Directory.CreateDirectory(documentsRoot);
        var paths = ValidatedExtractionDocumentPathsForTest(documentsRoot);
        await File.WriteAllTextAsync(paths.ValidationJsonPath, "stray", TestContext.Current.CancellationToken);
        var store = new ValidatedExtractionDocumentStore();

        await Assert.ThrowsAsync<IOException>(() => store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, extraction, manifest, report,
            TestContext.Current.CancellationToken));

        Assert.True(File.Exists(paths.ExtractionJsonPath));
        Assert.True(File.Exists(paths.ArtifactManifestJsonPath));
        Assert.False(File.Exists(paths.CompleteMarkerPath));
    }

    [Fact]
    public async Task WriteFinalDocumentsAsync_SecondCallOnSameRoot_ThrowsWithoutOverwriting()
    {
        var documentsRoot = CreateDocumentsRoot();
        var (extraction, manifest, report) = BuildValidSet();
        var store = new ValidatedExtractionDocumentStore();
        await store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, extraction, manifest, report,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(() => store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, extraction, manifest, report,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteFinalDocumentsAsync_ArtifactManifestDigestMismatch_ThrowsAndWritesNothing()
    {
        var documentsRoot = CreateDocumentsRoot();
        var (extraction, manifest, report) = BuildValidSet();
        var wrongExtraction = extraction with { ArtifactManifestDigest = new string('9', 64) };
        var store = new ValidatedExtractionDocumentStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, wrongExtraction, manifest, report,
            TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(documentsRoot) && Directory.GetFiles(documentsRoot).Length > 0);
    }

    [Fact]
    public async Task WriteFinalDocumentsAsync_ExtractionIdMismatch_Throws()
    {
        var documentsRoot = CreateDocumentsRoot();
        var (extraction, manifest, report) = BuildValidSet();
        var wrongExtraction = extraction with { ExtractionId = new string('8', 64) };
        var store = new ValidatedExtractionDocumentStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, wrongExtraction, manifest, report,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteFinalDocumentsAsync_RootOutsideDataRoot_Throws()
    {
        var (extraction, manifest, report) = BuildValidSet();
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"s1atlas-outside-{Guid.NewGuid():N}");
        var store = new ValidatedExtractionDocumentStore();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteFinalDocumentsAsync(
                _dataRoot, outsideRoot, extraction, manifest, report,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WriteAttemptValidationReportAsync_WritesSiblingOfAttemptDocument()
    {
        Directory.CreateDirectory(_dataRoot);
        var attemptId = "0123456789abcdef0123456789abcdef";
        var owned = OwnedAttemptPaths.Create(_dataRoot, "build-a", attemptId);
        var attemptPaths = OwnedValidatedExtractionPaths.ForAttempt(_dataRoot, "build-a", attemptId);
        var (_, _, report) = BuildValidSet();
        var store = new ValidatedExtractionDocumentStore();

        await store.WriteAttemptValidationReportAsync(
            attemptPaths, report, TestContext.Current.CancellationToken);

        Assert.Equal(
            Path.Combine(owned.AttemptRoot, "validation.json"), attemptPaths.AttemptValidationReportPath);
        Assert.True(File.Exists(attemptPaths.AttemptValidationReportPath));
        var roundTripped = await store.TryReadValidationReportAsync(
            attemptPaths.AttemptValidationReportPath!, TestContext.Current.CancellationToken);
        Assert.NotNull(roundTripped);
        AssertReportEqual(report, roundTripped!);
    }

    [Fact]
    public async Task WriteAttemptValidationReportAsync_RejectsExtractionScopedPaths()
    {
        Directory.CreateDirectory(_dataRoot);
        var extractionPaths = OwnedValidatedExtractionPaths.ForExtraction(_dataRoot, "build-a", new string('5', 64));
        var (_, _, report) = BuildValidSet();
        var store = new ValidatedExtractionDocumentStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAttemptValidationReportAsync(
            extractionPaths, report, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryReadExtractionAsync_RoundTrip_ReturnsEquivalentContent()
    {
        var documentsRoot = CreateDocumentsRoot();
        var (extraction, manifest, report) = BuildValidSet();
        var store = new ValidatedExtractionDocumentStore();
        var result = await store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, extraction, manifest, report,
            TestContext.Current.CancellationToken);

        var content = await store.TryReadExtractionAsync(
            result.Paths.ExtractionJsonPath, TestContext.Current.CancellationToken);

        Assert.NotNull(content);
        Assert.Equal(extraction.ExtractionId, content!.ExtractionId);
        Assert.Equal(extraction.RecipeId, content.RecipeId);
        Assert.Equal(extraction.SourceAttemptId, content.SourceAttemptId);
        AssertStatisticsEqual(extraction.Statistics, content.Statistics);
        Assert.Equal(extraction.TrustLevel, content.TrustLevel);
    }

    [Fact]
    public async Task TryReadArtifactManifestAsync_RoundTrip_ReturnsEquivalentManifest()
    {
        var documentsRoot = CreateDocumentsRoot();
        var (extraction, manifest, report) = BuildValidSet();
        var store = new ValidatedExtractionDocumentStore();
        var result = await store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, extraction, manifest, report,
            TestContext.Current.CancellationToken);

        var content = await store.TryReadArtifactManifestAsync(
            result.Paths.ArtifactManifestJsonPath, TestContext.Current.CancellationToken);

        Assert.NotNull(content);
        Assert.Equal(manifest.Entries, content!.Manifest.Entries);
        Assert.Equal(extraction.ArtifactManifestDigest, content.DeclaredDigest);
    }

    [Fact]
    public async Task TryReadValidationReportAsync_RoundTrip_ReturnsEquivalentReport()
    {
        var documentsRoot = CreateDocumentsRoot();
        var (extraction, manifest, report) = BuildValidSet();
        var store = new ValidatedExtractionDocumentStore();
        var result = await store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, extraction, manifest, report,
            TestContext.Current.CancellationToken);

        var roundTripped = await store.TryReadValidationReportAsync(
            result.Paths.ValidationJsonPath, TestContext.Current.CancellationToken);

        Assert.NotNull(roundTripped);
        AssertReportEqual(report, roundTripped!);
    }

    [Fact]
    public async Task TryReadExtractionAsync_MissingFile_ReturnsNull()
    {
        var store = new ValidatedExtractionDocumentStore();

        var content = await store.TryReadExtractionAsync(
            Path.Combine(_dataRoot, "does-not-exist.json"), TestContext.Current.CancellationToken);

        Assert.Null(content);
    }

    [Fact]
    public async Task TryReadExtractionAsync_UnknownProperty_ReturnsNull()
    {
        var documentsRoot = CreateDocumentsRoot();
        var (extraction, manifest, report) = BuildValidSet();
        var store = new ValidatedExtractionDocumentStore();
        var result = await store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, extraction, manifest, report,
            TestContext.Current.CancellationToken);
        var original = await File.ReadAllTextAsync(
            result.Paths.ExtractionJsonPath, TestContext.Current.CancellationToken);
        var mutated = original.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n  \"unexpectedProperty\": true,",
            StringComparison.Ordinal);
        Assert.NotEqual(original, mutated);
        var mutatedPath = Path.Combine(documentsRoot, "extraction.mutated.json");
        await File.WriteAllTextAsync(mutatedPath, mutated, TestContext.Current.CancellationToken);

        var content = await store.TryReadExtractionAsync(mutatedPath, TestContext.Current.CancellationToken);

        Assert.Null(content);
    }

    [Fact]
    public async Task TryReadExtractionAsync_DuplicateProperty_ReturnsNull()
    {
        var documentsRoot = CreateDocumentsRoot();
        var (extraction, manifest, report) = BuildValidSet();
        var store = new ValidatedExtractionDocumentStore();
        var result = await store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, extraction, manifest, report,
            TestContext.Current.CancellationToken);
        var original = await File.ReadAllTextAsync(
            result.Paths.ExtractionJsonPath, TestContext.Current.CancellationToken);
        var mutated = original.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n  \"schemaVersion\": 1,",
            StringComparison.Ordinal);
        Assert.NotEqual(original, mutated);
        var mutatedPath = Path.Combine(documentsRoot, "extraction.mutated.json");
        await File.WriteAllTextAsync(mutatedPath, mutated, TestContext.Current.CancellationToken);

        var content = await store.TryReadExtractionAsync(mutatedPath, TestContext.Current.CancellationToken);

        Assert.Null(content);
    }

    [Fact]
    public async Task TryReadArtifactManifestAsync_DuplicateNestedEntryProperty_ReturnsNull()
    {
        var documentsRoot = CreateDocumentsRoot();
        var (extraction, manifest, report) = BuildValidSet();
        var store = new ValidatedExtractionDocumentStore();
        var result = await store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, extraction, manifest, report,
            TestContext.Current.CancellationToken);
        var original = await File.ReadAllTextAsync(
            result.Paths.ArtifactManifestJsonPath, TestContext.Current.CancellationToken);
        var mutated = original.Replace(
            "\"relativePath\": \"reconstructed/Assembly-CSharp.dll\",",
            "\"relativePath\": \"reconstructed/Assembly-CSharp.dll\",\n      " +
            "\"relativePath\": \"reconstructed/Assembly-CSharp.dll\",",
            StringComparison.Ordinal);
        Assert.NotEqual(original, mutated);
        var mutatedPath = Path.Combine(documentsRoot, "artifact-manifest.mutated.json");
        await File.WriteAllTextAsync(mutatedPath, mutated, TestContext.Current.CancellationToken);

        var content = await store.TryReadArtifactManifestAsync(
            mutatedPath, TestContext.Current.CancellationToken);

        Assert.Null(content);
    }

    [Fact]
    public async Task TryReadValidationReportAsync_WrongSchemaVersion_ReturnsNull()
    {
        var documentsRoot = CreateDocumentsRoot();
        var (extraction, manifest, report) = BuildValidSet();
        var store = new ValidatedExtractionDocumentStore();
        var result = await store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, extraction, manifest, report,
            TestContext.Current.CancellationToken);
        var original = await File.ReadAllTextAsync(
            result.Paths.ValidationJsonPath, TestContext.Current.CancellationToken);
        var mutated = original.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 2,", StringComparison.Ordinal);
        Assert.NotEqual(original, mutated);
        var mutatedPath = Path.Combine(documentsRoot, "validation.mutated.json");
        await File.WriteAllTextAsync(mutatedPath, mutated, TestContext.Current.CancellationToken);

        var content = await store.TryReadValidationReportAsync(mutatedPath, TestContext.Current.CancellationToken);

        Assert.Null(content);
    }

    [Fact]
    public async Task TryReadFinalDocumentsAsync_AllFourPresent_ReturnsBundle()
    {
        var documentsRoot = CreateDocumentsRoot();
        var (extraction, manifest, report) = BuildValidSet();
        var store = new ValidatedExtractionDocumentStore();
        await store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, extraction, manifest, report,
            TestContext.Current.CancellationToken);

        var bundle = await store.TryReadFinalDocumentsAsync(documentsRoot, TestContext.Current.CancellationToken);

        Assert.NotNull(bundle);
        Assert.Equal(extraction.ExtractionId, bundle!.Extraction.ExtractionId);
    }

    [Fact]
    public async Task TryReadFinalDocumentsAsync_MissingOneDocument_ReturnsNull()
    {
        var documentsRoot = CreateDocumentsRoot();
        var (extraction, manifest, report) = BuildValidSet();
        var store = new ValidatedExtractionDocumentStore();
        var result = await store.WriteFinalDocumentsAsync(
            _dataRoot, documentsRoot, extraction, manifest, report,
            TestContext.Current.CancellationToken);
        File.Delete(result.Paths.ValidationJsonPath);

        var bundle = await store.TryReadFinalDocumentsAsync(documentsRoot, TestContext.Current.CancellationToken);

        Assert.Null(bundle);
    }

    private static void AssertStatisticsEqual(ExtractionStatistics expected, ExtractionStatistics actual)
    {
        Assert.Equal(expected.ArtifactCount, actual.ArtifactCount);
        Assert.Equal(expected.LibraryCount, actual.LibraryCount);
        Assert.Equal(expected.ManagedAssemblyCount, actual.ManagedAssemblyCount);
        Assert.Equal(expected.TypeDefinitionCount, actual.TypeDefinitionCount);
        Assert.Equal(expected.MethodDefinitionCount, actual.MethodDefinitionCount);
        Assert.Equal(expected.FieldDefinitionCount, actual.FieldDefinitionCount);
        Assert.Equal(expected.PropertyDefinitionCount, actual.PropertyDefinitionCount);
        Assert.Equal(expected.EventDefinitionCount, actual.EventDefinitionCount);
        Assert.Equal(expected.TotalOutputBytes, actual.TotalOutputBytes);
        Assert.Equal(expected.TotalManagedBytes, actual.TotalManagedBytes);
        Assert.Equal(expected.Assemblies, actual.Assemblies);
    }

    private static void AssertReportEqual(ValidationReport expected, ValidationReport actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.AttemptId, actual.AttemptId);
        Assert.Equal(expected.SubjectKind, actual.SubjectKind);
        Assert.Equal(expected.SubjectExtractionId, actual.SubjectExtractionId);
        Assert.Equal(expected.BuildId, actual.BuildId);
        Assert.Equal(expected.RecipeId, actual.RecipeId);
        Assert.Equal(expected.PolicyId, actual.PolicyId);
        Assert.Equal(expected.PolicyVersion, actual.PolicyVersion);
        Assert.Equal(expected.PolicyDigest, actual.PolicyDigest);
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.InputIntegrityPassed, actual.InputIntegrityPassed);
        Assert.Equal(expected.ProcessIntegrityPassed, actual.ProcessIntegrityPassed);
        Assert.Equal(expected.OutputContainmentPassed, actual.OutputContainmentPassed);
        Assert.Equal(expected.ArtifactManifestDigest, actual.ArtifactManifestDigest);
        AssertStatisticsEqual(expected.Statistics, actual.Statistics);
        Assert.Equal(expected.BaselineExtractionId, actual.BaselineExtractionId);
        Assert.Equal(expected.Comparisons, actual.Comparisons);
        Assert.Equal(expected.Issues, actual.Issues);
        Assert.Equal(expected.PreferenceEligible, actual.PreferenceEligible);
        Assert.Equal(expected.ValidatedAtUtc, actual.ValidatedAtUtc);
    }

    private string CreateDocumentsRoot()
    {
        Directory.CreateDirectory(_dataRoot);
        var extractionPaths = OwnedValidatedExtractionPaths.ForExtraction(
            _dataRoot, "build-a", new string('5', 64));
        return extractionPaths.FinalExtractionRoot!;
    }

    private static ValidatedExtractionDocumentPathsForTestHelper ValidatedExtractionDocumentPathsForTest(
        string documentsRoot) => new(
        Path.Combine(documentsRoot, "extraction.json"),
        Path.Combine(documentsRoot, "artifact-manifest.json"),
        Path.Combine(documentsRoot, "validation.json"),
        Path.Combine(documentsRoot, "complete.marker"));

    private sealed record ValidatedExtractionDocumentPathsForTestHelper(
        string ExtractionJsonPath,
        string ArtifactManifestJsonPath,
        string ValidationJsonPath,
        string CompleteMarkerPath);

    private static (ValidatedExtraction Extraction, ArtifactManifest Manifest, ValidationReport Report) BuildValidSet()
    {
        var recipeId = new string('1', 64);
        var entries = new[]
        {
            new ArtifactManifestEntry(
                RelativePath: "reconstructed/Assembly-CSharp.dll",
                Kind: ArtifactKind.ManagedAssembly,
                Size: 100,
                Sha256: new string('a', 64),
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
            TotalOutputBytes: 100,
            TotalManagedBytes: 100,
            Assemblies: [new AssemblyIdentityStatistics("Assembly-CSharp", 1, 100, 5, 10, 2, 1, 0)]);

        var extraction = new ValidatedExtraction(
            ExtractionId: extractionId,
            RecipeId: recipeId,
            BuildId: "build-a",
            ToolInstanceId: "tool-1",
            SourceAttemptId: "0123456789abcdef0123456789abcdef",
            ProfileId: "profile",
            ProfileVersion: 1,
            ProfileDigest: new string('2', 64),
            AdapterVersion: 1,
            ExtractionSchemaVersion: 1,
            ArtifactManifestDigest: digest,
            RootPath: @"C:\ignored\root",
            CreatedAtUtc: DateTimeOffset.Parse("2026-08-12T12:00:00Z"),
            TrustLevel: ToolTrustLevel.ManagedPinned,
            InitialValidationOutcome: ValidationOutcome.Valid,
            Statistics: statistics);

        var report = new ValidationReport(
            SchemaVersion: 1,
            AttemptId: "0123456789abcdef0123456789abcdef",
            SubjectKind: ValidationSubjectKind.CandidateOutput,
            SubjectExtractionId: null,
            BuildId: "build-a",
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

        return (extraction, manifest, report);
    }
}
