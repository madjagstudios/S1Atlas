using S1Atlas.Cli;
using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Upstream;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.IntegrationTests.Indexing;

public sealed class IndexCliTests : IAsyncDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "s1atlas-index-cli-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Index_json_is_registered_and_fails_closed_without_authority()
    {
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["index", "--json"], output, error, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("NoEnvironmentSnapshot", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Interop_path_rejects_api_indexing_options()
    {
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["index", "--codebase", "s1api", "--channel", "installed", "--interop-path", "interop.dll", "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("InvalidOptionCombination", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_installed_s1api_uses_the_current_snapshot_dependency_and_persists_provenance()
    {
        var assemblyPath = Path.Combine(_dataDirectory, "inputs", "S1API.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath)!);
        File.Copy(typeof(S1Atlas.ManagedAssemblyFixture.FixtureRoot).Assembly.Location, assemblyPath);
        var snapshot = await SaveSnapshotAsync(
            new DependencyVersion(DependencyKind.S1Api, "fixture", assemblyPath, true));
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["index", "--codebase", "s1api", "--channel", "installed", "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("S1Api", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Installed", output.ToString(), StringComparison.Ordinal);

        var repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
        var environmentSnapshotId = EnvironmentSnapshotId.Create(snapshot);
        var completed = await repository.GetLatestCompletedIndexAsync(
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            environmentSnapshotId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(completed);
        Assert.NotEmpty(await repository.GetCompletedSymbolsAsync(completed!.IndexId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Index_installed_api_returns_stable_missing_dependency_error_without_substitution()
    {
        await SaveSnapshotAsync(new DependencyVersion(DependencyKind.S1Api, null, null, false));
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["index", "--codebase", "s1api", "--channel", "installed", "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("InstalledDependencyMissing", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Index_cached_release_uses_only_the_existing_cache_and_reports_the_commit_identity()
    {
        var commitSha = new string('f', 40);
        var source = Encoding.UTF8.GetBytes("namespace Demo; public sealed class Cached { public void Run() { } }");
        await new UpstreamSnapshotCache(_dataDirectory).SaveAsync(
            new UpstreamSnapshot(
                new UpstreamRepositoryConfiguration(CodebaseKind.S1Api, "owner", "repo"),
                commitSha,
                [new UpstreamFile("src/Cached.cs", source, Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant())]),
            TestContext.Current.CancellationToken);
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["index", "--codebase", "s1api", "--channel", "release", "--commit", commitSha, "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains(commitSha, output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Release", output.ToString(), StringComparison.Ordinal);
    }

    private async Task<EnvironmentSnapshot> SaveSnapshotAsync(DependencyVersion apiDependency)
    {
        var snapshot = new EnvironmentSnapshot(
            2,
            new GameBuild(
                new string('a', 64),
                new string('b', 64),
                new string('c', 64),
                DateTimeOffset.UtcNow,
                true),
            new InstallationObservation("2022.3.62", "3164500", "fixture", _dataDirectory, null, null),
            [
                apiDependency,
                new DependencyVersion(DependencyKind.S1Mapi, null, null, false),
                new DependencyVersion(DependencyKind.MelonLoader, null, null, false),
                new DependencyVersion(DependencyKind.Sideload, null, null, false)
            ],
            "0.1.0-test",
            DateTimeOffset.UtcNow);
        var repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        await repository.SaveSnapshotAsync(snapshot, TestContext.Current.CancellationToken);
        return snapshot;
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
        return ValueTask.CompletedTask;
    }
}
