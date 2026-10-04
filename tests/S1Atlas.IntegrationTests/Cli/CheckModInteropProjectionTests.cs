using System.Text.Json;
using S1Atlas.Cli;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.IntegrationTests.Cli;

public sealed class CheckModInteropProjectionTests
{
    [Theory]
    [InlineData(true, "System.String <StoredValue>k__BackingField", "System.String _StoredValue_k__BackingField", "Backing")]
    [InlineData(false, "System.String <StoredValue>k__BackingField", "System.String _StoredValue_k__BackingField", "Backing")]
    [InlineData(true, "System.Int32 Count", "System.Int32 Count", "Plain")]
    [InlineData(false, "System.Int32 Count", "System.Int32 Count", "Plain")]
    public async Task Interop_property_accessors_report_removed_game_fields(
        bool includeCallableSurface, string gameSignature, string interopSignature, string sourceSuffix)
    {
        await using var atlas = await InteropProjectionAtlas.CreateAsync(includeCallableSurface);
        var cancellationToken = TestContext.Current.CancellationToken;
        var surface = await atlas.Repository.GetCompletedCallableSurfaceAsync(atlas.Seed.FromIndexId, cancellationToken);
        if (includeCallableSurface)
            Assert.NotEmpty(surface);
        else
            Assert.Empty(surface);

        var result = atlas.Run();
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal("check-mod", root.GetProperty("command").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
        var data = root.GetProperty("data");
        Assert.Equal(atlas.Seed.FromBuildId, data.GetProperty("fromBuildId").GetString());
        Assert.Equal(atlas.Seed.ToBuildId, data.GetProperty("toBuildId").GetString());
        Assert.Equal(atlas.Seed.FromIndexId, data.GetProperty("fromIndexId").GetString());
        Assert.Equal(atlas.Seed.ToIndexId, data.GetProperty("toIndexId").GetString());
        Assert.False(data.GetProperty("singleBuild").GetBoolean());
        var dependencies = data.GetProperty("dependencies").EnumerateArray().ToArray();
        var field = Assert.Single(dependencies, dependency =>
            dependency.GetProperty("kind").GetString() == "Field"
            && dependency.GetProperty("name").GetString() == "Demo.ProjectedState::" + gameSignature);
        Assert.Equal("removed", field.GetProperty("status").GetString());
        Assert.Equal(gameSignature, field.GetProperty("beforeSignature").GetString());
        Assert.Equal(JsonValueKind.Null, field.GetProperty("afterSignature").ValueKind);
        Assert.Equal(JsonValueKind.Null, field.GetProperty("reason").ValueKind);
        Assert.Equal("FACT", field.GetProperty("evidence").GetString());
        var sources = field.GetProperty("sources").EnumerateArray().ToArray();
        Assert.Equal(2, sources.Length);
        foreach (var operation in new[] { "Read", "Write" })
        {
            var source = Assert.Single(sources, item => item.GetProperty("member").GetString()!
                .Contains("Plugin::" + operation + sourceSuffix + "(", StringComparison.Ordinal));
            Assert.Equal("direct_reference", source.GetProperty("kind").GetString());
            Assert.Equal("FACT", source.GetProperty("evidence").GetString());
        }
        Assert.DoesNotContain(dependencies, dependency => dependency.GetProperty("kind").GetString() == "Method"
            && (dependency.GetProperty("name").GetString()!.StartsWith("Demo.ProjectedState::get_", StringComparison.Ordinal)
                || dependency.GetProperty("name").GetString()!.StartsWith("Demo.ProjectedState::set_", StringComparison.Ordinal)));
        Assert.Equal(2, data.GetProperty("summary").GetProperty("breakingDependencies").GetInt32());
        Assert.Equal(2, data.GetProperty("summary").GetProperty("counts").GetProperty("removed").GetInt32());
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(3, root.GetProperty("exitCode").GetInt32());
        Assert.False(root.GetProperty("success").GetBoolean());

        var symbols = await atlas.Repository.GetCompletedSymbolsAsync(atlas.Seed.FromIndexId, cancellationToken);
        var gameField = Assert.Single(symbols, symbol => symbol.Kind == "Field" && symbol.Signature == gameSignature);
        Assert.Equal(gameField.CanonicalKey, field.GetProperty("canonicalKey").GetString());
        if (includeCallableSurface)
        {
            var mapping = Assert.Single(surface, row => row.GameSymbolId == gameField.SymbolId);
            Assert.Equal(gameField.CanonicalKey, mapping.GameCanonicalKey);
            Assert.Equal(CallableSurfaceStatus.Resolved, mapping.Status);
            Assert.Equal(CallableSurfaceKind.PublicPropertyAccessor, mapping.Kind);
            Assert.Equal(interopSignature, mapping.InteropSignature);
            Assert.Equal("InteropProjection.dll", mapping.InteropAssemblyName);
            Assert.False(string.IsNullOrWhiteSpace(mapping.InteropInputSha256));
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Interop_array_and_collection_types_resolve_before_comparing_removed_members(
        bool includeCallableSurface, bool method)
    {
        await using var atlas = await InteropProjectionAtlas.CreateAsync(includeCallableSurface, includeTypeProjections: true);
        var cancellationToken = TestContext.Current.CancellationToken;
        var surface = await atlas.Repository.GetCompletedCallableSurfaceAsync(atlas.Seed.FromIndexId, cancellationToken);
        if (includeCallableSurface)
            Assert.NotEmpty(surface);
        else
            Assert.Empty(surface);

        var result = atlas.Run();
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");
        var gameSignature = method
            ? "Demo.ProjectedState::Transform(Demo.Value[],System.Int32[],System.Collections.Generic.List`1<Demo.Value[]>):System.String[]"
            : "Demo.Value[] <Entries>k__BackingField";
        var kind = method ? "Method" : "Field";
        var qualifiedName = method ? gameSignature : "Demo.ProjectedState::" + gameSignature;
        var dependencies = data.GetProperty("dependencies").EnumerateArray().ToArray();
        var removed = Assert.Single(dependencies, dependency => dependency.GetProperty("kind").GetString() == kind
            && dependency.GetProperty("name").GetString() == qualifiedName);
        Assert.Equal("removed", removed.GetProperty("status").GetString());
        Assert.Equal(gameSignature, removed.GetProperty("beforeSignature").GetString());
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("afterSignature").ValueKind);
        Assert.Equal("FACT", removed.GetProperty("evidence").GetString());
        var sources = removed.GetProperty("sources").EnumerateArray().ToArray();
        Assert.Equal(method ? 1 : 2, sources.Length);
        foreach (var source in sources)
        {
            Assert.Equal("direct_reference", source.GetProperty("kind").GetString());
            Assert.Equal("FACT", source.GetProperty("evidence").GetString());
        }
        if (method)
            Assert.Contains("ArrayPlugin::CallTransform(", sources[0].GetProperty("member").GetString(), StringComparison.Ordinal);
        else
            foreach (var operation in new[] { "ReadArray", "WriteArray" })
                Assert.Contains(sources, source => source.GetProperty("member").GetString()!
                    .Contains("ArrayPlugin::" + operation + "(", StringComparison.Ordinal));
        Assert.Equal(4, data.GetProperty("summary").GetProperty("breakingDependencies").GetInt32());
        Assert.Equal(4, data.GetProperty("summary").GetProperty("counts").GetProperty("removed").GetInt32());
        Assert.Equal(3, result.ExitCode);

        var symbols = await atlas.Repository.GetCompletedSymbolsAsync(atlas.Seed.FromIndexId, cancellationToken);
        var gameMember = Assert.Single(symbols, symbol => symbol.Kind == kind && symbol.Signature == gameSignature);
        Assert.Equal(gameMember.CanonicalKey, removed.GetProperty("canonicalKey").GetString());
        if (includeCallableSurface)
        {
            var mapping = Assert.Single(surface, row => row.GameSymbolId == gameMember.SymbolId);
            Assert.Equal(CallableSurfaceStatus.Resolved, mapping.Status);
            Assert.Equal(method ? CallableSurfaceKind.PublicMethodWrapper : CallableSurfaceKind.PublicPropertyAccessor, mapping.Kind);
            Assert.Contains("Il2CppInterop.Runtime.InteropTypes.Arrays.", mapping.InteropSignature, StringComparison.Ordinal);
            Assert.Contains(method ? "Il2CppStringArray" : "_Entries_k__BackingField", mapping.InteropSignature, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Public_field_projection_resolves_when_another_member_has_a_stored_wrapper()
    {
        await using var atlas = await InteropProjectionAtlas.CreateAsync(includeCallableSurface: true, publicField: true);
        var cancellationToken = TestContext.Current.CancellationToken;
        var symbols = await atlas.Repository.GetCompletedSymbolsAsync(atlas.Seed.FromIndexId, cancellationToken);
        var count = Assert.Single(symbols, symbol => symbol.Kind == "Field" && symbol.Signature == "System.Int32 Count");
        Assert.True(count.IsPublic);
        var backing = Assert.Single(symbols, symbol => symbol.Kind == "Field" && symbol.Signature == "System.String <StoredValue>k__BackingField");
        var surface = await atlas.Repository.GetCompletedCallableSurfaceAsync(atlas.Seed.FromIndexId, cancellationToken);
        var direct = Assert.Single(surface, row => row.GameSymbolId == count.SymbolId);
        Assert.Equal(CallableSurfaceStatus.Resolved, direct.Status);
        Assert.Equal(CallableSurfaceKind.DirectGameMember, direct.Kind);
        Assert.Null(direct.InteropSignature);
        var wrapper = Assert.Single(surface, row => row.GameSymbolId == backing.SymbolId);
        Assert.Equal(CallableSurfaceStatus.Resolved, wrapper.Status);
        Assert.Equal(CallableSurfaceKind.PublicPropertyAccessor, wrapper.Kind);
        Assert.Equal("System.String _StoredValue_k__BackingField", wrapper.InteropSignature);

        var result = atlas.Run();
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
        var dependencies = root.GetProperty("data").GetProperty("dependencies").EnumerateArray().ToArray();
        var removed = Assert.Single(dependencies, dependency => dependency.GetProperty("canonicalKey").GetString() == count.CanonicalKey);
        Assert.Equal("Field", removed.GetProperty("kind").GetString());
        Assert.Equal("Demo.ProjectedState::System.Int32 Count", removed.GetProperty("name").GetString());
        Assert.Equal("removed", removed.GetProperty("status").GetString());
        Assert.Equal("System.Int32 Count", removed.GetProperty("beforeSignature").GetString());
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("afterSignature").ValueKind);
        var sources = removed.GetProperty("sources").EnumerateArray().ToArray();
        Assert.Equal(2, sources.Length);
        foreach (var operation in new[] { "ReadPlain", "WritePlain" })
            Assert.Contains(sources, source => source.GetProperty("kind").GetString() == "direct_reference"
                && source.GetProperty("member").GetString()!.Contains("Plugin::" + operation + "(", StringComparison.Ordinal));
        Assert.Equal(2, root.GetProperty("data").GetProperty("summary").GetProperty("breakingDependencies").GetInt32());
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(3, root.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Stored_property_accessor_methods_take_precedence_over_property_projection()
    {
        await using var atlas = await InteropProjectionAtlas.CreateAsync(includeCallableSurface: true,
            includeTypeProjections: true, includePropertyAccessors: true);
        var cancellationToken = TestContext.Current.CancellationToken;
        var symbols = await atlas.Repository.GetCompletedSymbolsAsync(atlas.Seed.FromIndexId, cancellationToken);
        var getter = Assert.Single(symbols, symbol => symbol.Kind == "Method"
            && symbol.Signature == "Demo.ProjectedState::get_Entries():Demo.Value[]");
        var setter = Assert.Single(symbols, symbol => symbol.Kind == "Method"
            && symbol.Signature == "Demo.ProjectedState::set_Entries(Demo.Value[]):System.Void");
        var property = Assert.Single(symbols, symbol => symbol.Kind == "Property" && symbol.Signature == "Demo.Value[] Entries");
        var surface = await atlas.Repository.GetCompletedCallableSurfaceAsync(atlas.Seed.FromIndexId, cancellationToken);
        var propertyMapping = Assert.Single(surface, row => row.GameSymbolId == property.SymbolId);
        Assert.Equal(CallableSurfaceStatus.Resolved, propertyMapping.Status);
        Assert.Equal(CallableSurfaceKind.PublicPropertyAccessor, propertyMapping.Kind);
        foreach (var method in new[] { getter, setter })
        {
            var mapping = Assert.Single(surface, row => row.GameSymbolId == method.SymbolId);
            Assert.Equal(CallableSurfaceStatus.Resolved, mapping.Status);
            Assert.Equal(CallableSurfaceKind.PublicMethodWrapper, mapping.Kind);
            Assert.Contains("Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray", mapping.InteropSignature, StringComparison.Ordinal);
            Assert.Contains(method == getter ? "::get_Entries(" : "::set_Entries(", mapping.InteropSignature, StringComparison.Ordinal);
        }

        var result = atlas.Run();
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
        var dependencies = root.GetProperty("data").GetProperty("dependencies").EnumerateArray().ToArray();
        foreach (var method in new[] { getter, setter })
        {
            var removed = Assert.Single(dependencies, dependency => dependency.GetProperty("canonicalKey").GetString() == method.CanonicalKey);
            Assert.Equal("Method", removed.GetProperty("kind").GetString());
            Assert.Equal(method.QualifiedName, removed.GetProperty("name").GetString());
            Assert.Equal("removed", removed.GetProperty("status").GetString());
            Assert.Equal(method.Signature, removed.GetProperty("beforeSignature").GetString());
            Assert.Equal(JsonValueKind.Null, removed.GetProperty("afterSignature").ValueKind);
            var source = Assert.Single(removed.GetProperty("sources").EnumerateArray());
            Assert.Equal("direct_reference", source.GetProperty("kind").GetString());
            Assert.Contains(method == getter ? "PropertyPlugin::ReadProperty(" : "PropertyPlugin::WriteProperty(",
                source.GetProperty("member").GetString(), StringComparison.Ordinal);
        }
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(3, root.GetProperty("exitCode").GetInt32());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reflected_interop_property_accessors_merge_into_the_removed_game_field(bool includeCallableSurface)
    {
        await using var atlas = await InteropProjectionAtlas.CreateAsync(includeCallableSurface, includeReflectionAccessors: true);
        var cancellationToken = TestContext.Current.CancellationToken;
        var symbols = await atlas.Repository.GetCompletedSymbolsAsync(atlas.Seed.FromIndexId, cancellationToken);
        var count = Assert.Single(symbols, symbol => symbol.Kind == "Field" && symbol.Signature == "System.Int32 Count");
        var surface = await atlas.Repository.GetCompletedCallableSurfaceAsync(atlas.Seed.FromIndexId, cancellationToken);
        if (includeCallableSurface)
            Assert.NotEmpty(surface);
        else
            Assert.Empty(surface);

        var result = atlas.Run();
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
        var dependencies = root.GetProperty("data").GetProperty("dependencies").EnumerateArray().ToArray();
        var removed = Assert.Single(dependencies, dependency => dependency.GetProperty("canonicalKey").GetString() == count.CanonicalKey);
        Assert.Equal("Field", removed.GetProperty("kind").GetString());
        Assert.Equal("removed", removed.GetProperty("status").GetString());
        Assert.Equal("System.Int32 Count", removed.GetProperty("beforeSignature").GetString());
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("afterSignature").ValueKind);
        Assert.Equal("FACT", removed.GetProperty("evidence").GetString());
        var sources = removed.GetProperty("sources").EnumerateArray().ToArray();
        var reflection = sources.Where(source => source.GetProperty("kind").GetString() == "reflection").ToArray();
        Assert.Equal(2, reflection.Length);
        foreach (var operation in new[] { "ReflectGetter", "ReflectSetter" })
        {
            var source = Assert.Single(reflection, item => item.GetProperty("member").GetString()!
                .Contains("ReflectionPlugin::" + operation + "(", StringComparison.Ordinal));
            Assert.Equal("DERIVED", source.GetProperty("evidence").GetString());
        }
        foreach (var operation in new[] { "ReadPlain", "WritePlain" })
            Assert.Contains(sources, source => source.GetProperty("kind").GetString() == "direct_reference"
                && source.GetProperty("member").GetString()!.Contains("Plugin::" + operation + "(", StringComparison.Ordinal));
        Assert.Equal(4, sources.Length);
        Assert.DoesNotContain(dependencies, dependency => dependency.GetProperty("status").GetString() == "unresolved"
            && (dependency.GetProperty("name").GetString()!.Contains("::get_Count", StringComparison.Ordinal)
                || dependency.GetProperty("name").GetString()!.Contains("::set_Count", StringComparison.Ordinal)));
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(3, root.GetProperty("exitCode").GetInt32());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reflected_property_getter_ignores_a_surviving_accessor_named_method_with_parameters(bool includeCallableSurface)
    {
        await using var atlas = await InteropProjectionAtlas.CreateAsync(includeCallableSurface,
            includeReflectionAccessors: true, getterDecoy: true);
        var cancellationToken = TestContext.Current.CancellationToken;
        var symbols = await atlas.Repository.GetCompletedSymbolsAsync(atlas.Seed.FromIndexId, cancellationToken);
        var count = Assert.Single(symbols, symbol => symbol.Kind == "Field" && symbol.Signature == "System.Int32 Count");
        var decoy = Assert.Single(symbols, symbol => symbol.Kind == "Method"
            && symbol.Signature == "Demo.ProjectedState::get_Count(System.Int32):System.Int32");
        var after = await atlas.Repository.GetCompletedSymbolsAsync(atlas.Seed.ToIndexId, cancellationToken);
        Assert.Contains(after, symbol => symbol.CanonicalKey == decoy.CanonicalKey);
        var surface = await atlas.Repository.GetCompletedCallableSurfaceAsync(atlas.Seed.FromIndexId, cancellationToken);
        if (includeCallableSurface)
        {
            var mapping = Assert.Single(surface, row => row.GameSymbolId == decoy.SymbolId);
            Assert.Equal(CallableSurfaceStatus.Resolved, mapping.Status);
            Assert.Equal(CallableSurfaceKind.PublicMethodWrapper, mapping.Kind);
            Assert.Contains("::get_Count(System.Int32)", mapping.InteropSignature, StringComparison.Ordinal);
        }
        else
            Assert.Empty(surface);

        var result = atlas.Run();
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
        var dependencies = root.GetProperty("data").GetProperty("dependencies").EnumerateArray().ToArray();
        var removed = Assert.Single(dependencies, dependency => dependency.GetProperty("canonicalKey").GetString() == count.CanonicalKey);
        Assert.Equal("Field", removed.GetProperty("kind").GetString());
        Assert.Equal("removed", removed.GetProperty("status").GetString());
        Assert.Equal("System.Int32 Count", removed.GetProperty("beforeSignature").GetString());
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("afterSignature").ValueKind);
        Assert.Equal("DERIVED", removed.GetProperty("evidence").GetString());
        var sources = removed.GetProperty("sources").EnumerateArray().ToArray();
        Assert.Equal(2, sources.Length);
        foreach (var operation in new[] { "ReflectGetter", "ReflectSetter" })
        {
            var source = Assert.Single(sources, item => item.GetProperty("member").GetString()!
                .Contains("ReflectionPlugin::" + operation + "(", StringComparison.Ordinal));
            Assert.Equal("reflection", source.GetProperty("kind").GetString());
            Assert.Equal("DERIVED", source.GetProperty("evidence").GetString());
        }
        Assert.DoesNotContain(dependencies, dependency => dependency.GetProperty("canonicalKey").GetString() == decoy.CanonicalKey);
        Assert.Equal(1, root.GetProperty("data").GetProperty("summary").GetProperty("breakingDependencies").GetInt32());
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(3, root.GetProperty("exitCode").GetInt32());
    }

    private sealed class InteropProjectionAtlas : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _dataRoot;

        private InteropProjectionAtlas(string root)
        {
            _root = root;
            _dataRoot = Path.Combine(root, "atlas");
            Repository = new SqliteAtlasRepository(Path.Combine(_dataRoot, "atlas.db"));
        }

        public SqliteAtlasRepository Repository { get; }
        public ModCheckSeed Seed { get; private set; } = null!;

        public static async Task<InteropProjectionAtlas> CreateAsync(bool includeCallableSurface, bool includeTypeProjections = false,
            bool publicField = false, bool includePropertyAccessors = false, bool includeReflectionAccessors = false, bool getterDecoy = false)
        {
            var atlas = new InteropProjectionAtlas(Path.Combine(Path.GetTempPath(), "s1atlas-interop-projection-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(atlas._dataRoot);
            var cancellationToken = TestContext.Current.CancellationToken;
            await atlas.Repository.InitializeAsync(cancellationToken);
            atlas.Seed = await ModCheckAtlas.SeedInteropProjectionAsync(atlas.Repository, atlas._dataRoot,
                includeCallableSurface, cancellationToken, includeTypeProjections, publicField, includePropertyAccessors, includeReflectionAccessors, getterDecoy);
            return atlas;
        }

        public (int ExitCode, string StandardOutput, string StandardError) Run()
        {
            var application = new CliApplication(_dataRoot, "0.1.0-test");
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = application.Invoke(["check-mod", Seed.ModPath, "--from", Seed.FromBuildId,
                "--to", Seed.ToBuildId, "--json"], output, error, TestContext.Current.CancellationToken);
            return (exitCode, output.ToString(), error.ToString());
        }

        public async ValueTask DisposeAsync() => await TestDirectory.DeleteTreeAsync(_root);
    }
}
