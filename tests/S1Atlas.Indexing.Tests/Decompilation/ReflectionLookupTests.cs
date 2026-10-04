using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Decompilation;
using Xunit;

namespace S1Atlas.Indexing.Tests.Decompilation;

public sealed class ReflectionLookupTests
{
    private static readonly Lazy<Task<ManagedDecompilation>> Decompilation = new(
        () => new IlSpyManagedDecompiler().DecompileAsync(
            Path.Combine(AppContext.BaseDirectory, "harmony-fixture", "S1Atlas.HarmonyModFixture.dll"),
            TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("AccessMethod", ManagedReflectionKind.Method, "Run")]
    [InlineData("AccessField", ManagedReflectionKind.Field, "Count")]
    [InlineData("AccessProperty", ManagedReflectionKind.Property, "Name")]
    [InlineData("AccessGetter", ManagedReflectionKind.PropertyGetter, "Name")]
    [InlineData("AccessSetter", ManagedReflectionKind.PropertySetter, "Name")]
    [InlineData("AccessConstructor", ManagedReflectionKind.Constructor, ".ctor")]
    [InlineData("AccessEmptyConstructor", ManagedReflectionKind.Constructor, ".ctor")]
    [InlineData("AccessConstructorWithFlag", ManagedReflectionKind.Constructor, ".ctor")]
    [InlineData("AccessStaticConstructor", ManagedReflectionKind.Constructor, ".cctor")]
    [InlineData("TypeMethod", ManagedReflectionKind.Method, "Run")]
    [InlineData("TypeMethodWithFlags", ManagedReflectionKind.Method, "Run")]
    [InlineData("TypeField", ManagedReflectionKind.Field, "Count")]
    [InlineData("TypeFieldWithFlags", ManagedReflectionKind.Field, "Count")]
    [InlineData("TypeProperty", ManagedReflectionKind.Property, "Name")]
    [InlineData("TypePropertyWithFlags", ManagedReflectionKind.Property, "Name")]
    public async Task Constant_lookups_produce_reflection_facts(string method, ManagedReflectionKind kind, string memberName)
    {
        var fact = Assert.Single((await Find(method)).ReflectionsOrEmpty);
        Assert.Equal("Game.Widget", fact.TargetType);
        Assert.Equal(memberName, fact.MemberName);
        Assert.Equal(kind, fact.Kind);
    }

    [Fact]
    public async Task Explicit_method_overload_preserves_parameter_types()
    {
        var fact = Assert.Single((await Find("AccessMethodOverload")).ReflectionsOrEmpty);
        Assert.Equal(["System.Int32", "System.String"], fact.ArgumentTypes);
    }

    [Fact]
    public async Task Four_argument_access_method_preserves_parameter_types()
    {
        var fact = Assert.Single((await Find("AccessMethodFourArguments")).ReflectionsOrEmpty);
        Assert.Equal("Game.Widget", fact.TargetType);
        Assert.Equal("Compute", fact.MemberName);
        Assert.Equal(["System.Int32"], fact.ArgumentTypes);
    }

    [Fact]
    public async Task Type_method_overload_preserves_parameter_types()
    {
        var fact = Assert.Single((await Find("TypeMethodOverload")).ReflectionsOrEmpty);
        Assert.Equal("Game.Widget", fact.TargetType);
        Assert.Equal("Compute", fact.MemberName);
        Assert.Equal(["System.Int32"], fact.ArgumentTypes);
    }

    [Theory]
    [InlineData("AccessConstructor", "System.Int32")]
    [InlineData("AccessEmptyConstructor")]
    [InlineData("AccessConstructorWithFlag", "System.Int32")]
    public async Task Constructor_parameter_types_are_preserved(string method, params string[] parameters)
    {
        var fact = Assert.Single((await Find(method)).ReflectionsOrEmpty);
        Assert.Equal(parameters, fact.ArgumentTypes);
    }

    [Theory]
    [InlineData("DynamicName")]
    [InlineData("IncompleteArray")]
    [InlineData("DynamicTypeMethodArguments")]
    [InlineData("BranchName")]
    [InlineData("BranchType")]
    [InlineData("BranchConstructor")]
    public async Task Dynamic_and_merge_point_values_are_not_guessed(string method)
    {
        Assert.Single((await Find("AccessMethod")).ReflectionsOrEmpty);
        Assert.Empty((await Find(method)).ReflectionsOrEmpty);
    }

    private static async Task<ManagedMemberFacts> Find(string method)
    {
        var assembly = await Decompilation.Value;
        var type = Assert.Single(assembly.Types, type => type.FullName == "Mod.ReflectionLookups");
        return Assert.Single(type.Members, member => member.Name == method);
    }
}
