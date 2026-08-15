using S1Atlas.Indexing.Scene;
using Xunit;

namespace S1Atlas.Indexing.Tests.Scene;

public sealed class SceneSnapshotIdentityTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Create_is_deterministic_and_sorts_container_facts_by_relative_path()
    {
        var first = SceneSnapshotIdentity.Create(
            "build-a",
            "extraction-a",
            A,
            "index-a",
            "assetstools-net",
            "3.0.5:default",
            22,
            [
                new SceneSnapshotContainerFact("Schedule I_Data/level1", 20, B, "[]"),
                new SceneSnapshotContainerFact("Schedule I_Data/level0", 10, A, "[]")
            ]);
        var reordered = SceneSnapshotIdentity.Create(
            "build-a",
            "extraction-a",
            A,
            "index-a",
            "assetstools-net",
            "3.0.5:default",
            22,
            [
                new SceneSnapshotContainerFact("Schedule I_Data/level0", 10, A, "[]"),
                new SceneSnapshotContainerFact("Schedule I_Data/level1", 20, B, "[]")
            ]);

        Assert.Equal(first, reordered);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void Create_changes_when_force_changes_the_parser_settings_input()
    {
        var baseline = Create("3.0.5:default");
        var forced = Create("3.0.5:forced:0123456789abcdef");

        Assert.NotEqual(baseline, forced);
    }

    [Fact]
    public void Create_binds_every_required_authority_and_container_fact()
    {
        var baseline = Create("3.0.5:default");

        Assert.NotEqual(baseline, SceneSnapshotIdentity.Create("build-b", "extraction-a", A, "index-a", "assetstools-net", "3.0.5:default", 22, [Fact()]));
        Assert.NotEqual(baseline, SceneSnapshotIdentity.Create("build-a", "extraction-b", A, "index-a", "assetstools-net", "3.0.5:default", 22, [Fact()]));
        Assert.NotEqual(baseline, SceneSnapshotIdentity.Create("build-a", "extraction-a", B, "index-a", "assetstools-net", "3.0.5:default", 22, [Fact()]));
        Assert.NotEqual(baseline, SceneSnapshotIdentity.Create("build-a", "extraction-a", A, "index-b", "assetstools-net", "3.0.5:default", 22, [Fact()]));
        Assert.NotEqual(baseline, SceneSnapshotIdentity.Create("build-a", "extraction-a", A, "index-a", "other-parser", "3.0.5:default", 22, [Fact()]));
        Assert.NotEqual(baseline, SceneSnapshotIdentity.Create("build-a", "extraction-a", A, "index-a", "assetstools-net", "3.0.5:default", 21, [Fact()]));
        Assert.NotEqual(baseline, SceneSnapshotIdentity.Create("build-a", "extraction-a", A, "index-a", "assetstools-net", "3.0.5:default", 22, [Fact(size: 11)]));
        Assert.NotEqual(baseline, SceneSnapshotIdentity.Create("build-a", "extraction-a", A, "index-a", "assetstools-net", "3.0.5:default", 22, [Fact(sha256: B)]));
        Assert.NotEqual(baseline, SceneSnapshotIdentity.Create("build-a", "extraction-a", A, "index-a", "assetstools-net", "3.0.5:default", 22, [Fact(sidecarManifest: "[{}]")]));
    }

    private static string Create(string parserVersion) =>
        SceneSnapshotIdentity.Create(
            "build-a",
            "extraction-a",
            A,
            "index-a",
            "assetstools-net",
            parserVersion,
            22,
            [Fact()]);

    private static SceneSnapshotContainerFact Fact(
        long size = 10,
        string sha256 = A,
        string sidecarManifest = "[]") =>
        new("Schedule I_Data/level0", size, sha256, sidecarManifest);
}
