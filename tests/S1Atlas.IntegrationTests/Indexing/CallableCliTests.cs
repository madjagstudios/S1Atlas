using S1Atlas.Cli;
using S1Atlas.Cli.Configuration;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.IntegrationTests.Indexing;

public sealed class CallableCliTests : IAsyncDisposable
{
    private const string InteropSignature = "TMPro.TextMeshProUGUI ScheduleOne.UI.HUD::topScreenText { get; set; }";
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "s1atlas-callable-cli-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(InteropSignature, CallableSurfaceStatus.Resolved, InteropSignature)]
    [InlineData(InteropSignature, CallableSurfaceStatus.Unknown, InteropSignature)]
    [InlineData(InteropSignature, CallableSurfaceStatus.Unavailable, InteropSignature)]
    [InlineData(InteropSignature, CallableSurfaceStatus.Ambiguous, InteropSignature)]
    [InlineData(null, CallableSurfaceStatus.Unknown, "unknown")]
    [InlineData(null, CallableSurfaceStatus.Unavailable, "unavailable")]
    [InlineData(null, CallableSurfaceStatus.Ambiguous, "ambiguous")]
    [InlineData(null, CallableSurfaceStatus.Resolved, "not needed (public game member)")]
    public async Task Callable_human_output_follows_status(
        string? signature,
        CallableSurfaceStatus status,
        string expectedInterop)
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(signature, status, retainMapping: true, isPublic: signature is null && status == CallableSurfaceStatus.Resolved, ct);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["callable", "HUD.topScreenText"], output, error, ct);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        var interopLine = Assert.Single(output.ToString().Split('\n').Select(line => line.TrimEnd('\r')),
            line => line.StartsWith("Interop: ", StringComparison.Ordinal));
        Assert.Equal("Interop: " + expectedInterop, interopLine);
    }

    [Fact]
    public async Task Callable_legacy_nonpublic_member_reports_unknown_availability()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(null, CallableSurfaceStatus.Unknown, retainMapping: false, isPublic: false, ct);
        var application = new CliApplication(_dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["callable", "HUD.topScreenText"], output, error, ct);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Callable: Unknown | NonPublicWrapper | reflection required: False", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Evidence: interop availability is unknown because this legacy index retained no callable-surface mapping", output.ToString(), StringComparison.Ordinal);
    }

    private async Task SeedAsync(
        string? signature,
        CallableSurfaceStatus status,
        bool retainMapping,
        bool isPublic,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_dataRoot);
        var repository = new SqliteAtlasRepository(new AtlasPaths(_dataRoot).DatabasePath);
        await repository.InitializeAsync(ct);
        var baseTime = DateTimeOffset.Parse("2026-08-16T00:00:00Z");
        await repository.SaveSnapshotAsync(ExtractionSeed.CreateSnapshot("build-callable", baseTime), ct);
        await ExtractionSeed.SeedToolInstanceAsync(_dataRoot, "tool-callable", ct);
        var seeded = await ExtractionSeed.SeedValidatedExtractionAsync(
            repository, _dataRoot, "build-callable", ExtractionSeed.Sha256("recipe-callable"), "tool-callable",
            ExtractionSeed.Sha256("profile"), ExtractionSeed.Sha256("policy"), baseTime, ct);
        await repository.SetPreferredExtractionAsync(
            new PreferredExtraction("build-callable", seeded.Extraction.ExtractionId, seeded.Report.ValidatedAtUtc, ExtractionPreferenceReason.ManualPromotion), ct);

        const string snapshotId = "snapshot-callable";
        const string indexId = "index-callable";
        const string createdAtUtc = "2026-08-16T00:20:00Z";
        var symbol = new IndexSymbolRecord(
            "hud-field", snapshotId, "ScheduleI:Installed:Field:ScheduleOne.UI.HUD::topScreenText",
            "Field", "ScheduleOne.UI.HUD.topScreenText", "TMPro.TextMeshProUGUI ScheduleOne.UI.HUD::topScreenText", false, IsPublic: isPublic);
        await repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(snapshotId, CodebaseKind.ScheduleI, CodeChannel.Installed, seeded.Extraction.ExtractionId, createdAtUtc), ct);
        await repository.StartIndexRunAsync(new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, createdAtUtc), ct);
        IReadOnlyList<IndexCallableSurfaceRecord> mappings = retainMapping
            ? [new IndexCallableSurfaceRecord(
                "callable-hud", indexId, snapshotId, symbol.SymbolId, symbol.CanonicalKey, "Assembly-CSharp.dll",
                signature is null ? null : "interop-hash", signature,
                isPublic ? CallableSurfaceKind.DirectGameMember : CallableSurfaceKind.PublicFieldAccessor,
                false, status, InteropInputTrust.LocalOnly, "seeded callable evidence")]
            : [];
        await repository.CompleteIndexRunAsync(
            indexId, new IndexWriteSet([symbol], [], [], [], [], mappings), "2026-08-16T00:21:00Z", ct);
    }

    public async ValueTask DisposeAsync() => await TestDirectory.DeleteTreeAsync(_dataRoot);
}
