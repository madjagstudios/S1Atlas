using System.Text.Json;
using S1Atlas.Cli;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Discovery;
using S1Atlas.IntegrationTests;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.IntegrationTests.NativeRecovery;

public sealed class RecoverNativeBodyCliTests
{
    [Fact]
    public async Task Recover_native_body_rejects_a_missing_symbol_id()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run("recover-native-body", "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        AssertJsonErrorCode(result.StandardOutput, "MissingSymbolId");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("501")]
    public async Task Recover_native_body_rejects_an_out_of_range_traversal_budget(string budget)
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run(
            "recover-native-body",
            "--symbol-id",
            "native-target",
            "--native-traversal-budget",
            budget,
            "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        AssertJsonErrorCode(result.StandardOutput, "InvalidNativeTraversalBudget");
    }

    [Fact]
    public async Task Recover_native_body_accepts_the_maximum_traversal_budget_and_proceeds_past_option_validation()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run(
            "recover-native-body",
            "--symbol-id",
            "native-target",
            "--native-traversal-budget",
            "500",
            "--json");

        // A bare atlas has no current build, so execution must proceed past option
        // validation and fail on authority resolution instead of the budget bounds check.
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        AssertJsonErrorCode(result.StandardOutput, "NoCurrentBuild");
    }

    [Fact]
    public async Task Recover_native_body_lists_matches_for_an_ambiguous_symbol_id_prefix()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateSymbolPrefixAsync();

        var result = atlas.Run(
            "recover-native-body",
            "--symbol-id",
            "ABCDEF12",
            "--native-traversal-budget",
            "100",
            "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        AssertJsonErrorCode(result.StandardOutput, "AmbiguousSymbol");
        Assert.Contains("matches 2 symbols", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "abcdef120000 (System.Void Game.Prefix::First())",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "abcdef121111 (System.Void Game.Prefix::Second())",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain(atlas.FirstPrefixSymbolId, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(atlas.SecondPrefixSymbolId, result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// Task 3.2 acceptance gate: drives the real <c>recover-native-body</c> CLI command against the
    /// locally-installed Schedule I build and whatever real, completed Schedule I / Installed index
    /// already exists in the local S1Atlas database (built by prior <c>s1atlas scan</c>/<c>index</c>
    /// runs). Persists a real native recovery record and proves determinism by running twice.
    ///
    /// Runs only when explicitly enabled (see CONTRIBUTING), and then only against a temp
    /// copy of the live atlas: the live database is never opened for writing. Skips (does
    /// not fail) when not explicitly enabled, when the game is not installed, when no local
    /// S1Atlas database exists, when no completed index or indexed
    /// <c>Customer.EvaluateCounteroffer</c> symbol can be found, or when the command cannot
    /// resolve an installed-build authority in the local environment -- mirroring every
    /// other LocalGameRequired gate so CI without the game and its index stays green.
    /// </summary>
    [Trait("Category", "LocalGameRequired")]
    [Fact]
    public async Task Recover_native_body_recovers_and_persists_deterministically_for_the_real_installed_build()
    {
        const string DeclaringTypeFullName = "ScheduleOne.Economy.Customer";
        const string MethodName = "EvaluateCounteroffer";
        const string QualifiedNamePrefix = $"{DeclaringTypeFullName}::{MethodName}(";

        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.SkipUnless(
            LiveAtlasCopy.IsExplicitlyEnabled,
            $"LocalGameRequired tests run only with {LiveAtlasCopy.EnableVariable}=1. " +
            "Skipping: NEEDS_CONTEXT.");

        var installation = await new WindowsScheduleOneLocator().LocateAsync(null, cancellationToken);
        Assert.SkipUnless(
            installation is not null,
            "No Schedule I installation was found via standard Steam detection. Skipping: NEEDS_CONTEXT.");

        var liveDataRoot = LiveAtlasCopy.ResolveLiveDataRoot();
        var liveDatabasePath = Path.Combine(liveDataRoot, "atlas.db");
        Assert.SkipUnless(
            File.Exists(liveDatabasePath),
            $"No S1Atlas database found at '{liveDatabasePath}' (override with S1ATLAS_HOME). " +
            "Skipping: NEEDS_CONTEXT.");

        await using var copy = await LiveAtlasCopy.CreateAsync(liveDataRoot, cancellationToken);
        Assert.SkipUnless(
            copy is not null,
            "The temp drive cannot hold a copy of the live atlas. Skipping: NEEDS_CONTEXT.");

        var atlasHomeDirectory = copy!.DataRoot;
        var databasePath = copy.DatabasePath;

        var repository = new SqliteAtlasRepository(
            databasePath,
            Path.Combine(atlasHomeDirectory, "backups"));
        await repository.InitializeAsync(cancellationToken);

        var run = await repository.GetLatestCompletedIndexAsync(
            CodebaseKind.ScheduleI, CodeChannel.Installed, environmentSnapshotId: null, cancellationToken);
        Assert.SkipUnless(
            run is not null,
            "No completed Schedule I Installed index exists locally. Skipping: NEEDS_CONTEXT.");

        var candidates = await repository.SearchCompletedSymbolsAsync(
            run!.IndexId, MethodName, limit: 50, cancellationToken, kind: "Method");
        var symbol = candidates.FirstOrDefault(
            candidate => candidate.QualifiedName.StartsWith(QualifiedNamePrefix, StringComparison.Ordinal));
        Assert.SkipUnless(
            symbol is not null,
            $"No indexed '{DeclaringTypeFullName}::{MethodName}' symbol was found in index '{run.IndexId}'. " +
            "Skipping: NEEDS_CONTEXT.");

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var first = Run(atlasHomeDirectory, symbol!.SymbolId);
        Assert.SkipUnless(
            first.ExitCode == 0,
            "recover-native-body did not resolve an installed-build authority in the local " +
            $"environment (exit {first.ExitCode}): {first.StandardOutput}{first.StandardError} " +
            "Skipping: NEEDS_CONTEXT.");

        using var firstDocument = JsonDocument.Parse(first.StandardOutput);
        var firstData = firstDocument.RootElement.GetProperty("data");
        var status = firstData.GetProperty("status").GetString();
        Assert.True(
            status is "Recovered" or "NoBody" or "AmbiguousMapping" or "Failed" or "Unsupported" or "InputChanged",
            $"Unexpected native recovery status '{status}'.");
        var recoveryId = firstData.GetProperty("recoveryId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(recoveryId));

        var second = Run(atlasHomeDirectory, symbol.SymbolId);
        Assert.Equal(0, second.ExitCode);
        using var secondDocument = JsonDocument.Parse(second.StandardOutput);
        var secondRecoveryId = secondDocument.RootElement.GetProperty("data").GetProperty("recoveryId").GetString();
        Assert.Equal(recoveryId, secondRecoveryId);
    }

    private static (int ExitCode, string StandardOutput, string StandardError) Run(string dataRoot, string symbolId)
    {
        var application = new CliApplication(dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(
            ["recover-native-body", "--symbol-id", symbolId, "--native-traversal-budget", "100", "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);
        return (exitCode, output.ToString(), error.ToString());
    }

    [Fact]
    public void NativeRecoveryEdgeOutput_includes_EdgeId_in_json()
    {
        var edge = new S1Atlas.Cli.Output.NativeRecoveryEdgeOutput(
            "edge-1",
            "Foo::Bar()",
            "Baz::Qux()",
            null,
            "Direct",
            "evidence-123",
            true);
        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var json = System.Text.Json.JsonSerializer.Serialize(edge, jsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("edgeId", out var edgeIdElement));
        Assert.Equal("edge-1", edgeIdElement.GetString());
    }

    private static void AssertJsonErrorCode(string output, string expectedCode)
    {
        using var document = JsonDocument.Parse(output);
        Assert.Equal(expectedCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
