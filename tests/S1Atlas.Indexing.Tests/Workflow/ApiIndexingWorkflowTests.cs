using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Upstream;
using S1Atlas.Indexing.Workflow;
using S1Atlas.ManagedAssemblyFixture;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Indexing.Tests.Workflow;

public sealed class ApiIndexingWorkflowTests
{
    [Fact]
    public void Installed_api_workflow_exposes_the_narrow_v1_contract_and_public_constructor()
    {
        var workflowType = GetWorkflowType();
        var constructor = workflowType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types:
            [
                typeof(string),
                typeof(IIndexRepository),
                typeof(IManagedDecompiler)
            ],
            modifiers: null);

        Assert.NotNull(constructor);

        var root = Path.Combine(Path.GetTempPath(), "s1atlas-api-workflow-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var repository = new SqliteAtlasRepository(Path.Combine(root, "atlas.db"));
            var instance = constructor.Invoke([root, repository, new IlSpyManagedDecompiler()]);
            Assert.IsType(workflowType, instance);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        var method = workflowType.GetMethod(
            "RunInstalledAsync",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types:
            [
                typeof(CodebaseKind),
                typeof(string),
                typeof(string),
                typeof(bool),
                typeof(CancellationToken)
            ],
            modifiers: null);
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<IndexingWorkflowResult>), method.ReturnType);
    }

    [Fact]
    public async Task RunInstalledAsync_indexes_exact_fixture_assembly_and_persists_installed_api_provenance()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-api-workflow-" + Guid.NewGuid().ToString("N"));
        var assemblyPath = Path.Combine(root, "inputs", "managed", "S1API.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath)!);
        File.Copy(typeof(FixtureRoot).Assembly.Location, assemblyPath);

        var repository = new SqliteAtlasRepository(Path.Combine(root, "atlas.db"));

        try
        {
            await repository.InitializeAsync(TestContext.Current.CancellationToken);

            var environmentSnapshot = new EnvironmentSnapshot(
                2,
                new GameBuild(
                    new string('a', 64),
                    new string('b', 64),
                    new string('c', 64),
                    DateTimeOffset.UtcNow,
                    true),
                new InstallationObservation("2022.3.62", "3164500", "fixture", root, null, null),
                [],
                "0.1.0-test",
                DateTimeOffset.UtcNow);
            await repository.SaveSnapshotAsync(environmentSnapshot, TestContext.Current.CancellationToken);
            var environmentSnapshotId = EnvironmentSnapshotId.Create(environmentSnapshot);

            var workflow = CreateWorkflow(root, repository, new IlSpyManagedDecompiler());
            var result = await InvokeRunInstalledAsync(
                workflow,
                CodebaseKind.S1Api,
                environmentSnapshotId,
                assemblyPath,
                force: false,
                TestContext.Current.CancellationToken);

            Assert.False(result.Reused);
            Assert.True(result.SymbolCount > 0);
            Assert.True(result.SourceFileCount > 0);

            var snapshot = await repository.GetCodeSnapshotAsync(result.SnapshotId, TestContext.Current.CancellationToken);
            Assert.NotNull(snapshot);
            Assert.Equal(CodebaseKind.S1Api, snapshot.Codebase);
            Assert.Equal(CodeChannel.Installed, snapshot.Channel);
            Assert.Equal(environmentSnapshotId, snapshot.EnvironmentSnapshotId);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.SourceIdentity));

            var latest = await repository.GetLatestCompletedIndexAsync(
                CodebaseKind.S1Api,
                CodeChannel.Installed,
                environmentSnapshotId,
                TestContext.Current.CancellationToken);
            Assert.NotNull(latest);
            Assert.Equal(result.IndexId, latest.IndexId);

            var queryLatest = await repository.GetLatestCompletedIndexAsync(
                CodebaseKind.S1Api,
                CodeChannel.Installed,
                environmentSnapshotId: null,
                TestContext.Current.CancellationToken);
            Assert.NotNull(queryLatest);
            Assert.Equal(result.IndexId, queryLatest!.IndexId);

            var otherEnvironment = await repository.GetLatestCompletedIndexAsync(
                CodebaseKind.S1Api,
                CodeChannel.Installed,
                "env-installed-api-other",
                TestContext.Current.CancellationToken);
            Assert.Null(otherEnvironment);

            var symbols = await repository.GetCompletedSymbolsAsync(result.IndexId, TestContext.Current.CancellationToken);
            Assert.NotEmpty(symbols);
            Assert.All(symbols, symbol => Assert.Equal(result.SnapshotId, symbol.SnapshotId));
            Assert.Equal(
                BodyRecoveryStatus.Recovered,
                Assert.Single(
                    symbols,
                    symbol => symbol.Signature.Contains("DerivedFixture::Overload(System.Int32)", StringComparison.Ordinal)).BodyRecoveryStatus);
            Assert.Equal(
                BodyRecoveryStatus.StubOrUnavailable,
                Assert.Single(
                    symbols,
                    symbol => symbol.Signature.Contains("FixtureRoot::GetValue", StringComparison.Ordinal)).BodyRecoveryStatus);
            Assert.Equal(
                BodyRecoveryStatus.NoBodyByDesign,
                Assert.Single(
                    symbols,
                    symbol => symbol.Signature.Contains("IFixtureContract::get_ContractValue", StringComparison.Ordinal)).BodyRecoveryStatus);
            Assert.All(
                symbols.Where(symbol => symbol.Kind is "Type" or "Field" or "Property" or "Event"),
                symbol => Assert.Null(symbol.BodyRecoveryStatus));

            var sourceFiles = await repository.GetCompletedSourceFilesAsync(result.IndexId, TestContext.Current.CancellationToken);
            var sourceFile = Assert.Single(sourceFiles);
            Assert.Equal(result.SnapshotId, sourceFile.SnapshotId);
            Assert.False(string.IsNullOrWhiteSpace(sourceFile.RelativePath));
            Assert.Equal(64, sourceFile.Sha256.Length);
            Assert.True(sourceFile.ByteCount > 0);

            var sourceLocations = await repository.GetCompletedSourceLocationsAsync(result.IndexId, TestContext.Current.CancellationToken);
            Assert.NotEmpty(sourceLocations);
            Assert.All(sourceLocations, location => Assert.Equal(sourceFile.SourceFileId, location.SourceFileId));
            Assert.All(
                sourceLocations,
                location =>
                {
                    Assert.True(location.StartLine > 0);
                    Assert.True(location.StartColumn > 0);
                    Assert.NotNull(location.EndLine);
                    Assert.NotNull(location.EndColumn);
                });
            Assert.Contains(sourceLocations, location => location.StartColumn > 1);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunCachedSourceAsync_indexes_only_cached_csharp_files_and_persists_commit_backed_source_metadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-api-cached-workflow-" + Guid.NewGuid().ToString("N"));
        var repository = new SqliteAtlasRepository(Path.Combine(root, "atlas.db"));
        var commitSha = new string('d', 40);
        var alpha = CachedFile("src/Demo/Alpha.cs", """
                    namespace Demo;

                    public sealed class Alpha
                    {
                        public int GetValue() => 1;
                    }
                    """);
        var beta = CachedFile("src/Demo/Beta.cs", """
                    namespace Demo;

                    public static class Beta
                    {
                        public static string Name => "atlas";
                    }
                    """);

        try
        {
            await repository.InitializeAsync(TestContext.Current.CancellationToken);
            await SeedCachedSnapshotAsync(
                root,
                CodebaseKind.S1Api,
                commitSha,
                [
                    alpha,
                    beta,
                    CachedFile("docs/notes.txt", "ignored")
                ],
                TestContext.Current.CancellationToken);

            var workflow = CreateWorkflow(root, repository, new IlSpyManagedDecompiler());
            var result = await InvokeRunCachedSourceAsync(
                workflow,
                CodebaseKind.S1Api,
                CodeChannel.Release,
                commitSha,
                force: false,
                TestContext.Current.CancellationToken);

            Assert.False(result.Reused);
            Assert.True(result.SymbolCount > 0);
            Assert.Equal(2, result.SourceFileCount);

            var snapshot = await repository.GetCodeSnapshotAsync(result.SnapshotId, TestContext.Current.CancellationToken);
            Assert.NotNull(snapshot);
            Assert.Equal(CodebaseKind.S1Api, snapshot.Codebase);
            Assert.Equal(CodeChannel.Release, snapshot.Channel);
            Assert.Contains(commitSha, snapshot.SourceIdentity, StringComparison.Ordinal);

            var latest = await repository.GetLatestCompletedIndexAsync(
                CodebaseKind.S1Api,
                CodeChannel.Release,
                environmentSnapshotId: null,
                TestContext.Current.CancellationToken);
            Assert.NotNull(latest);
            Assert.Equal(result.IndexId, latest!.IndexId);

            var sourceFiles = await repository.GetCompletedSourceFilesAsync(result.IndexId, TestContext.Current.CancellationToken);
            Assert.Equal(2, sourceFiles.Count);
            Assert.All(sourceFiles, file => Assert.EndsWith(".cs", file.RelativePath, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(sourceFiles, file => file.RelativePath == "src/Demo/Alpha.cs");
            Assert.Contains(sourceFiles, file => file.RelativePath == "src/Demo/Beta.cs");
            Assert.DoesNotContain(sourceFiles, file => file.RelativePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(sourceFiles, file => file.Sha256 == alpha.Sha256);
            Assert.Contains(sourceFiles, file => file.Sha256 == beta.Sha256);

            var symbols = await repository.GetCompletedSymbolsAsync(result.IndexId, TestContext.Current.CancellationToken);
            Assert.NotEmpty(symbols);
            Assert.Contains(symbols, symbol => symbol.QualifiedName.Contains("Demo.Alpha", StringComparison.Ordinal));
            Assert.Contains(symbols, symbol => symbol.QualifiedName.Contains("Demo.Beta", StringComparison.Ordinal));

            var sourceLocations = await repository.GetCompletedSourceLocationsAsync(result.IndexId, TestContext.Current.CancellationToken);
            Assert.NotEmpty(sourceLocations);
            var fileIds = sourceFiles.Select(file => file.SourceFileId).ToHashSet(StringComparer.Ordinal);
            Assert.All(sourceLocations, location => Assert.Contains(location.SourceFileId, fileIds));
            Assert.All(sourceLocations, location =>
            {
                Assert.True(location.StartLine > 0);
                Assert.True(location.StartColumn > 0);
                Assert.NotNull(location.EndLine);
                Assert.NotNull(location.EndColumn);
            });
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunCachedSourceAsync_indexes_the_same_commit_into_distinct_release_and_preview_snapshots()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-api-cached-channels-" + Guid.NewGuid().ToString("N"));
        var repository = new SqliteAtlasRepository(Path.Combine(root, "atlas.db"));
        var commitSha = new string('e', 40);

        try
        {
            await repository.InitializeAsync(TestContext.Current.CancellationToken);
            await SeedCachedSnapshotAsync(
                root,
                CodebaseKind.S1Api,
                commitSha,
                [
                    CachedFile("src/Demo/Shared.cs", """
                    namespace Demo;

                    public sealed class Shared
                    {
                        public int Value() => 42;
                    }
                    """)
                ],
                TestContext.Current.CancellationToken);

            var workflow = CreateWorkflow(root, repository, new IlSpyManagedDecompiler());
            var release = await InvokeRunCachedSourceAsync(
                workflow,
                CodebaseKind.S1Api,
                CodeChannel.Release,
                commitSha,
                force: false,
                TestContext.Current.CancellationToken);
            var preview = await InvokeRunCachedSourceAsync(
                workflow,
                CodebaseKind.S1Api,
                CodeChannel.Preview,
                commitSha,
                force: false,
                TestContext.Current.CancellationToken);

            Assert.NotEqual(release.SnapshotId, preview.SnapshotId);
            Assert.NotEqual(release.IndexId, preview.IndexId);

            var releaseSnapshot = await repository.GetCodeSnapshotAsync(release.SnapshotId, TestContext.Current.CancellationToken);
            var previewSnapshot = await repository.GetCodeSnapshotAsync(preview.SnapshotId, TestContext.Current.CancellationToken);
            Assert.NotNull(releaseSnapshot);
            Assert.NotNull(previewSnapshot);
            Assert.Equal(CodeChannel.Release, releaseSnapshot!.Channel);
            Assert.Equal(CodeChannel.Preview, previewSnapshot!.Channel);
            Assert.Contains(commitSha, releaseSnapshot.SourceIdentity, StringComparison.Ordinal);
            Assert.Contains(commitSha, previewSnapshot.SourceIdentity, StringComparison.Ordinal);

            var releaseLatest = await repository.GetLatestCompletedIndexAsync(
                CodebaseKind.S1Api,
                CodeChannel.Release,
                environmentSnapshotId: null,
                TestContext.Current.CancellationToken);
            var previewLatest = await repository.GetLatestCompletedIndexAsync(
                CodebaseKind.S1Api,
                CodeChannel.Preview,
                environmentSnapshotId: null,
                TestContext.Current.CancellationToken);
            Assert.NotNull(releaseLatest);
            Assert.NotNull(previewLatest);
            Assert.Equal(release.IndexId, releaseLatest!.IndexId);
            Assert.Equal(preview.IndexId, previewLatest!.IndexId);

            Assert.NotEmpty(await repository.GetCompletedSymbolsAsync(release.IndexId, TestContext.Current.CancellationToken));
            Assert.NotEmpty(await repository.GetCompletedSymbolsAsync(preview.IndexId, TestContext.Current.CancellationToken));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Type GetWorkflowType()
    {
        var workflowType = typeof(IndexingWorkflow).Assembly.GetType(
            "S1Atlas.Indexing.Workflow.ApiIndexingWorkflow",
            throwOnError: false);

        Assert.NotNull(workflowType);
        return workflowType!;
    }

    private static object CreateWorkflow(
        string root,
        IIndexRepository repository,
        IManagedDecompiler decompiler)
    {
        var constructor = GetWorkflowType().GetConstructor(
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types:
            [
                typeof(string),
                typeof(IIndexRepository),
                typeof(IManagedDecompiler)
            ],
            modifiers: null);

        Assert.NotNull(constructor);
        return constructor.Invoke([root, repository, decompiler]);
    }

    private static async Task<IndexingWorkflowResult> InvokeRunInstalledAsync(
        object workflow,
        CodebaseKind codebase,
        string environmentSnapshotId,
        string assemblyPath,
        bool force,
        CancellationToken cancellationToken)
    {
        var method = workflow.GetType().GetMethod(
            "RunInstalledAsync",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types:
            [
                typeof(CodebaseKind),
                typeof(string),
                typeof(string),
                typeof(bool),
                typeof(CancellationToken)
            ],
            modifiers: null);

        Assert.NotNull(method);
        var invocation = method.Invoke(
            workflow,
            [codebase, environmentSnapshotId, assemblyPath, force, cancellationToken]);
        var task = Assert.IsAssignableFrom<Task<IndexingWorkflowResult>>(invocation);
        return await task;
    }

    private static async Task<IndexingWorkflowResult> InvokeRunCachedSourceAsync(
        object workflow,
        CodebaseKind codebase,
        CodeChannel channel,
        string commitSha,
        bool force,
        CancellationToken cancellationToken)
    {
        var method = workflow.GetType().GetMethod(
            "RunCachedSourceAsync",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types:
            [
                typeof(CodebaseKind),
                typeof(CodeChannel),
                typeof(string),
                typeof(bool),
                typeof(CancellationToken)
            ],
            modifiers: null);

        Assert.NotNull(method);
        var invocation = method.Invoke(
            workflow,
            [codebase, channel, commitSha, force, cancellationToken]);
        var task = Assert.IsAssignableFrom<Task<IndexingWorkflowResult>>(invocation);
        return await task;
    }

    private static async Task SeedCachedSnapshotAsync(
        string root,
        CodebaseKind codebase,
        string commitSha,
        IReadOnlyList<UpstreamFile> files,
        CancellationToken cancellationToken)
    {
        var cache = new UpstreamSnapshotCache(root);
        await cache.SaveAsync(
            new UpstreamSnapshot(
                new UpstreamRepositoryConfiguration(codebase, "owner", "repo"),
                commitSha,
                files),
            cancellationToken);
    }

    private static UpstreamFile CachedFile(string relativePath, string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        return new UpstreamFile(relativePath, bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

}
