using System.Collections.Concurrent;
using S1Atlas.Core.Extraction;
using S1Atlas.Extraction.Validation;
using S1Atlas.ManagedAssemblyFixture;
using Xunit;

namespace S1Atlas.Extraction.Tests.Validation;

public sealed class ManagedAssemblyInspectorTests : IDisposable
{
    private readonly string _workRoot = Path.Combine(
        Path.GetTempPath(), $"s1atlas-managed-inspector-{Guid.NewGuid():N}");

    public ManagedAssemblyInspectorTests()
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
    public void Inspect_SourceBuiltManagedAssembly_ReturnsIdentityModuleAndExactTableCounts()
    {
        var path = CopyFixtureAssembly("Assembly-CSharp.dll");

        var result = ManagedAssemblyInspector.Inspect(path, "reconstructed/Assembly-CSharp.dll");

        Assert.Equal(ArtifactKind.ManagedAssembly, result.Kind);
        Assert.True(result.IsValid);
        Assert.Equal("Assembly-CSharp", result.AssemblyName);
        Assert.Equal("Assembly-CSharp.dll", result.ModuleName);
        Assert.Equal(2, result.TypeDefinitionCount);
        Assert.Equal(7, result.MethodDefinitionCount);
        Assert.Equal(2, result.FieldDefinitionCount);
        Assert.Equal(1, result.PropertyDefinitionCount);
        Assert.Equal(1, result.EventDefinitionCount);
        Assert.Null(result.FailureCode);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public void Inspect_ValidNativeDll_ReturnsNativeLibraryWithNoManagedCounts()
    {
        var kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");

        var result = ManagedAssemblyInspector.Inspect(kernel32, "reconstructed/kernel32.dll");

        Assert.Equal(ArtifactKind.NativeLibrary, result.Kind);
        Assert.True(result.IsValid);
        Assert.Null(result.AssemblyName);
        Assert.Null(result.ModuleName);
        Assert.Null(result.TypeDefinitionCount);
        Assert.Null(result.MethodDefinitionCount);
        Assert.Null(result.FieldDefinitionCount);
        Assert.Null(result.PropertyDefinitionCount);
        Assert.Null(result.EventDefinitionCount);
    }

    [Fact]
    public void Inspect_TotallyGarbageDll_ReturnsStructuredInvalidFact()
    {
        var path = Path.Combine(_workRoot, "garbage.dll");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03, 0x04]);

        var result = ManagedAssemblyInspector.Inspect(path, "reconstructed/garbage.dll");

        Assert.False(result.IsValid);
        Assert.Equal("InvalidManagedAssembly", result.FailureCode);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureMessage));
        Assert.Null(result.AssemblyName);
        Assert.Null(result.TypeDefinitionCount);
    }

    [Fact]
    public void Inspect_TruncatedManagedAssembly_ReturnsStructuredInvalidFact()
    {
        var sourceBytes = File.ReadAllBytes(CopyFixtureAssembly("source-for-truncation.dll"));
        var truncated = sourceBytes.Take(1500).ToArray();
        var path = Path.Combine(_workRoot, "truncated.dll");
        File.WriteAllBytes(path, truncated);

        var result = ManagedAssemblyInspector.Inspect(path, "reconstructed/truncated.dll");

        Assert.False(result.IsValid);
        Assert.Equal("InvalidManagedAssembly", result.FailureCode);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureMessage));
    }

    [Fact]
    public void Inspect_NonDllFile_ReturnsOtherWithoutOpeningAsPe()
    {
        var path = Path.Combine(_workRoot, "readme.txt");
        File.WriteAllText(path, "not a library");

        var result = ManagedAssemblyInspector.Inspect(path, "reconstructed/readme.txt");

        Assert.Equal(ArtifactKind.Other, result.Kind);
        Assert.True(result.IsValid);
        Assert.Null(result.AssemblyName);
        Assert.Null(result.FailureCode);
    }

    [Fact]
    public void Inspect_TrailingByteCopy_KeepsAssemblyIdentityAndTableCounts()
    {
        var original = CopyFixtureAssembly("original.dll");
        var originalBytes = File.ReadAllBytes(original);
        var withTrailer = originalBytes.Concat([(byte)0x00]).ToArray();
        var trailerPath = Path.Combine(_workRoot, "with-trailer.dll");
        File.WriteAllBytes(trailerPath, withTrailer);

        var originalResult = ManagedAssemblyInspector.Inspect(original, "reconstructed/original.dll");
        var trailerResult = ManagedAssemblyInspector.Inspect(trailerPath, "reconstructed/with-trailer.dll");

        Assert.True(trailerResult.IsValid);
        Assert.Equal(originalResult.AssemblyName, trailerResult.AssemblyName);
        Assert.Equal(originalResult.ModuleName, trailerResult.ModuleName);
        Assert.Equal(originalResult.TypeDefinitionCount, trailerResult.TypeDefinitionCount);
        Assert.Equal(originalResult.MethodDefinitionCount, trailerResult.MethodDefinitionCount);
        Assert.Equal(originalResult.FieldDefinitionCount, trailerResult.FieldDefinitionCount);
        Assert.Equal(originalResult.PropertyDefinitionCount, trailerResult.PropertyDefinitionCount);
        Assert.Equal(originalResult.EventDefinitionCount, trailerResult.EventDefinitionCount);
        Assert.NotEqual(originalBytes.Length, withTrailer.Length);
    }

    [Fact]
    public void Inspect_DoesNotIncreaseAssemblyLoadEventCount()
    {
        var managedPath = CopyFixtureAssembly("load-count-managed.dll");
        var nativePath = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        var invalidPath = Path.Combine(_workRoot, "load-count-invalid.dll");
        File.WriteAllBytes(invalidPath, [0x00, 0x01, 0x02]);

        // The real invariant: inspection must never load the *inspected* file into this
        // runtime. Counting all process-global AssemblyLoad events is racy in a parallel
        // suite (other tests, or the first-use JIT load of System.Reflection.Metadata /
        // System.Collections.Immutable, fire unrelated events). Instead, capture the
        // locations of assemblies loaded during inspection and assert none is one of the
        // paths we inspected.
        var loadedLocations = new ConcurrentBag<string>();
        void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
        {
            try
            {
                if (!string.IsNullOrEmpty(args.LoadedAssembly.Location))
                {
                    loadedLocations.Add(Path.GetFullPath(args.LoadedAssembly.Location));
                }
            }
            catch (NotSupportedException)
            {
            }
        }

        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        try
        {
            ManagedAssemblyInspector.Inspect(managedPath, "reconstructed/load-count-managed.dll");
            ManagedAssemblyInspector.Inspect(nativePath, "reconstructed/kernel32.dll");
            ManagedAssemblyInspector.Inspect(invalidPath, "reconstructed/load-count-invalid.dll");
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        }

        var inspectedPaths = new[] { managedPath, nativePath, invalidPath }
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(loadedLocations, inspectedPaths.Contains);
    }

    [Fact]
    public void Inspect_DllWithNoManagedMetadataFromFixtureAssembly_IdentitiesAreConsistentAcrossCalls()
    {
        // Calling Inspect twice on the same immutable bytes must be deterministic:
        // inspection reads metadata, it never mutates or caches state that could
        // change behavior across repeated inspection of the same artifact.
        var path = CopyFixtureAssembly("repeat.dll");

        var first = ManagedAssemblyInspector.Inspect(path, "reconstructed/repeat.dll");
        var second = ManagedAssemblyInspector.Inspect(path, "reconstructed/repeat.dll");

        Assert.Equal(first, second);
    }

    private string CopyFixtureAssembly(string fileName)
    {
        var sourcePath = typeof(FixtureRoot).Assembly.Location;
        var destinationPath = Path.Combine(_workRoot, fileName);
        File.Copy(sourcePath, destinationPath, overwrite: true);
        return destinationPath;
    }
}
