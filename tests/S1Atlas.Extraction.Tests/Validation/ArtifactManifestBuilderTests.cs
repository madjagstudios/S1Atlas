using System.Security.Cryptography;
using S1Atlas.Core.Extraction;
using S1Atlas.Extraction.Validation;
using S1Atlas.ManagedAssemblyFixture;
using Xunit;

namespace S1Atlas.Extraction.Tests.Validation;

public sealed class ArtifactManifestBuilderTests : IDisposable
{
    private readonly string _workRoot = Path.Combine(
        Path.GetTempPath(), $"s1atlas-artifact-manifest-{Guid.NewGuid():N}");

    public ArtifactManifestBuilderTests()
    {
        Directory.CreateDirectory(_workRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workRoot))
        {
            Directory.Delete(_workRoot, recursive: true);
        }
    }

    [Fact]
    public void Build_MixedArtifactKinds_AnnotatesManagedNativeAndOtherCorrectly()
    {
        var managedPath = CopyFixtureAssembly("Assembly-CSharp.dll");
        var nativePath = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        var otherPath = WriteFile("notes.txt", [1, 2, 3]);

        var inventory = BuildInventory(
            Artifact(managedPath, "reconstructed/Assembly-CSharp.dll"),
            Artifact(nativePath, "reconstructed/kernel32.dll"),
            Artifact(otherPath, "reconstructed/notes.txt"));

        var result = ArtifactManifestBuilder.Build(inventory);

        var entriesByPath = result.Manifest.Entries.ToDictionary(entry => entry.RelativePath);
        Assert.Equal(ArtifactKind.ManagedAssembly, entriesByPath["reconstructed/Assembly-CSharp.dll"].Kind);
        Assert.Equal(ArtifactKind.NativeLibrary, entriesByPath["reconstructed/kernel32.dll"].Kind);
        Assert.Equal(ArtifactKind.Other, entriesByPath["reconstructed/notes.txt"].Kind);

        Assert.Equal(3, result.Statistics.ArtifactCount);
        Assert.Equal(2, result.Statistics.LibraryCount);
        Assert.Equal(1, result.Statistics.ManagedAssemblyCount);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Build_TwoIdenticalCopiesOfSameAssembly_AggregatesStatisticsAndReturnsInformationalIssue()
    {
        var first = CopyFixtureAssembly("Assembly-CSharp.dll");
        var second = CopyFixtureAssembly("copy/Assembly-CSharp.dll");

        var inventory = BuildInventory(
            Artifact(first, "reconstructed/Assembly-CSharp.dll"),
            Artifact(second, "reconstructed/copy/Assembly-CSharp.dll"));

        var result = ArtifactManifestBuilder.Build(inventory);

        var assembly = Assert.Single(result.Statistics.Assemblies);
        Assert.Equal("Assembly-CSharp", assembly.AssemblyName);
        Assert.Equal(2, assembly.FileCount);
        Assert.Equal(12, assembly.TypeDefinitionCount);
        Assert.Equal(46, assembly.MethodDefinitionCount);
        Assert.Equal(14, assembly.FieldDefinitionCount);
        Assert.Equal(8, assembly.PropertyDefinitionCount);
        Assert.Equal(4, assembly.EventDefinitionCount);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(ValidationIssueSeverity.Information, issue.Severity);
        Assert.Equal("DuplicateAssemblyIdentity", issue.Code);
        Assert.False(issue.PreferenceBlocking);
    }

    [Fact]
    public void Build_SameIdentityDifferentHash_ReturnsHardBlockingIssue()
    {
        var originalPath = CopyFixtureAssembly("Assembly-CSharp.dll");
        var conflictingBytes = File.ReadAllBytes(originalPath).Concat([(byte)0x00]).ToArray();
        var conflictingPath = WriteFile("conflict/Assembly-CSharp.dll", conflictingBytes);

        var inventory = BuildInventory(
            Artifact(originalPath, "reconstructed/Assembly-CSharp.dll"),
            Artifact(conflictingPath, "reconstructed/conflict/Assembly-CSharp.dll"));

        var result = ArtifactManifestBuilder.Build(inventory);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(ValidationIssueSeverity.Error, issue.Severity);
        Assert.Equal("DuplicateAssemblyIdentity", issue.Code);
        Assert.True(issue.PreferenceBlocking);

        var assembly = Assert.Single(result.Statistics.Assemblies);
        Assert.Equal(2, assembly.FileCount);
    }

    [Fact]
    public void Build_InvalidManagedDll_ReturnsHardBlockingIssueAndOtherClassification()
    {
        var invalidPath = WriteFile("broken.dll", [0x00, 0x01, 0x02, 0x03]);

        var inventory = BuildInventory(Artifact(invalidPath, "reconstructed/broken.dll"));

        var result = ArtifactManifestBuilder.Build(inventory);

        var entry = Assert.Single(result.Manifest.Entries);
        Assert.Equal(ArtifactKind.Other, entry.Kind);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(ValidationIssueSeverity.Error, issue.Severity);
        Assert.Equal("InvalidManagedAssembly", issue.Code);
        Assert.True(issue.PreferenceBlocking);
        Assert.Equal("reconstructed/broken.dll", issue.ArtifactRelativePath);

        Assert.Equal(0, result.Statistics.ManagedAssemblyCount);
        Assert.Equal(1, result.Statistics.LibraryCount);
    }

    [Fact]
    public void Build_ContentDigestUnchanged_WhenOnlyAnnotationsDiffer()
    {
        var managedPath = CopyFixtureAssembly("Assembly-CSharp.dll");
        var inventory = BuildInventory(Artifact(managedPath, "reconstructed/Assembly-CSharp.dll"));

        var result = ArtifactManifestBuilder.Build(inventory);

        var reannotatedEntries = result.Manifest.Entries
            .Select(entry => entry with
            {
                Kind = ArtifactKind.Other,
                AssemblyName = null,
                ModuleName = null,
                TypeDefinitionCount = null,
                MethodDefinitionCount = null,
                FieldDefinitionCount = null,
                PropertyDefinitionCount = null,
                EventDefinitionCount = null,
            })
            .ToArray();
        var reannotatedManifest = new ArtifactManifest(result.Manifest.SchemaVersion, reannotatedEntries);
        var reannotatedDigest = ArtifactManifestFingerprint.Create(reannotatedManifest);

        Assert.Equal(result.ManifestDigest, reannotatedDigest);
        Assert.Equal(ArtifactManifestFingerprint.Create(result.Manifest), result.ManifestDigest);
    }

    [Fact]
    public void Build_EmptyInventory_ReturnsEmptyManifestAndZeroStatisticsWithoutIssues()
    {
        var inventory = BuildInventory();

        var result = ArtifactManifestBuilder.Build(inventory);

        Assert.Empty(result.Manifest.Entries);
        Assert.Equal(0, result.Statistics.ArtifactCount);
        Assert.Equal(0, result.Statistics.ManagedAssemblyCount);
        Assert.Empty(result.Statistics.Assemblies);
        Assert.Empty(result.Issues);
    }

    private static CandidateInventory BuildInventory(params CandidateArtifact[] artifacts) =>
        new("candidate-root", artifacts, artifacts.Sum(artifact => artifact.Size));

    private static CandidateArtifact Artifact(string fullPath, string relativePath) =>
        new(fullPath, relativePath, new FileInfo(fullPath).Length, ComputeSha256(fullPath));

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private string CopyFixtureAssembly(string relativeDestination)
    {
        var sourcePath = typeof(FixtureRoot).Assembly.Location;
        var destinationPath = Path.Combine(_workRoot, relativeDestination);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: true);
        return destinationPath;
    }

    private string WriteFile(string relativeDestination, byte[] bytes)
    {
        var destinationPath = Path.Combine(_workRoot, relativeDestination);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.WriteAllBytes(destinationPath, bytes);
        return destinationPath;
    }
}
