using System.Buffers.Binary;
using System.Text;
using S1Atlas.Cli.Configuration;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Scene;
using Xunit;

namespace S1Atlas.IntegrationTests.Scene;

public sealed class SceneOutputIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-scene-isolation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Scene_index_output_paths_are_contained_by_the_configured_Atlas_data_root_not_the_repository()
    {
        var repositoryRoot = Path.Combine(_root, "repository");
        var dataRoot = Path.Combine(_root, "atlas-data");
        var paths = new AtlasPaths(dataRoot);
        var buildId = new string('a', 64);
        var sceneSnapshotId = new string('b', 64);

        var finalRoot = paths.GetBuildSceneIndexDirectory(buildId, sceneSnapshotId);
        var stagingRoot = paths.GetBuildSceneIndexStagingDirectory(buildId, sceneSnapshotId);

        Assert.True(IsDescendantOf(dataRoot, finalRoot));
        Assert.True(IsDescendantOf(dataRoot, stagingRoot));
        Assert.False(IsDescendantOf(repositoryRoot, finalRoot));
        Assert.False(IsDescendantOf(repositoryRoot, stagingRoot));
    }

    [Fact]
    public async Task Scene_input_capture_reads_the_game_install_without_copying_it_to_the_repository()
    {
        var repositoryRoot = Path.Combine(_root, "repository");
        var installRoot = Path.Combine(_root, "game-install");
        var containerPath = Path.Combine(installRoot, "Schedule I_Data", "level0");
        Directory.CreateDirectory(repositoryRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(containerPath)!);
        WriteSerializedFile(containerPath);

        var verified = await new SceneInputVerifier(new Sha256FileHasher()).CaptureAsync(
            installRoot,
            [new SceneContainerDeclaration("Schedule I_Data/level0", [])],
            TestContext.Current.CancellationToken);

        var container = Assert.Single(verified.Containers);
        Assert.Equal(Path.GetFullPath(containerPath), container.PrimaryPath);
        Assert.True(IsDescendantOf(installRoot, container.PrimaryPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(repositoryRoot, "*", SearchOption.AllDirectories));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static bool IsDescendantOf(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteSerializedFile(string path)
    {
        var metadata = Encoding.ASCII.GetBytes("2022.3.62f1\0");
        var fileSize = 48 + metadata.Length;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        WriteBigEndian(writer, 0u);
        WriteBigEndian(writer, (uint)fileSize);
        WriteBigEndian(writer, 22u);
        WriteBigEndian(writer, 48u);
        writer.Write(false);
        writer.Write(new byte[3]);
        WriteBigEndian(writer, (uint)metadata.Length);
        WriteBigEndian(writer, (long)fileSize);
        WriteBigEndian(writer, 48L);
        writer.Write(new byte[8]);
        writer.Write(metadata);
    }

    private static void WriteBigEndian(BinaryWriter writer, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        writer.Write(bytes);
    }

    private static void WriteBigEndian(BinaryWriter writer, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        writer.Write(bytes);
    }
}
