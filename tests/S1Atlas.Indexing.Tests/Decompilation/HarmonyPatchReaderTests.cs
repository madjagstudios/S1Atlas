using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Decompilation;
using Xunit;

namespace S1Atlas.Indexing.Tests.Decompilation;

public sealed class HarmonyPatchReaderTests
{
    private static readonly Lazy<Task<ManagedDecompilation>> Decompilation = new(
        () => new IlSpyManagedDecompiler().DecompileAsync(
            Path.Combine(AppContext.BaseDirectory, "harmony-fixture", "S1Atlas.HarmonyModFixture.dll"),
            TestContext.Current.CancellationToken));

    public static async Task<ManagedDecompilation> ModAsync() => await Decompilation.Value;

    [Theory]
    [InlineData("Mod.RunPatch", "Prefix", HarmonyPatchKind.Prefix, "Game.Widget::Run")]
    [InlineData("Mod.ComputePatch", "Postfix", HarmonyPatchKind.Postfix, "Game.Widget::Compute(System.Int32,System.String)")]
    [InlineData("Mod.NameGetterPatch", "Prefix", HarmonyPatchKind.Prefix, "Game.Widget::get_Name")]
    [InlineData("Mod.NameSetterPatch", "Prefix", HarmonyPatchKind.Prefix, "Game.Widget::set_Name")]
    [InlineData("Mod.CtorPatch", "Postfix", HarmonyPatchKind.Postfix, "Game.Widget::.ctor")]
    [InlineData("Mod.StaticCtorPatch", "Prefix", HarmonyPatchKind.Prefix, "Game.Widget::.cctor")]
    [InlineData("Mod.RefOverloadPatch", "Prefix", HarmonyPatchKind.Prefix, "Game.Widget::Compute(System.Int32&)")]
    [InlineData("Mod.OutOverloadPatch", "Prefix", HarmonyPatchKind.Prefix, "Game.Widget::Consume(System.Int32&)")]
    [InlineData("Mod.ConventionPatch", "Transpiler", HarmonyPatchKind.Transpiler, "Game.Widget+Nested::Inner")]
    [InlineData("Mod.AmbiguousPatch", "Finalizer", HarmonyPatchKind.Finalizer, "Game.Widget::Compute")]
    [InlineData("Mod.StringNamePatch", "Prefix", HarmonyPatchKind.Prefix, "Game.Widget::Run")]
    [InlineData("Mod.InteropPrefixPatch", "Prefix", HarmonyPatchKind.Prefix, "Il2CppGame.Widget::Run")]
    [InlineData("Mod.InteropParamPatch", "Prefix", HarmonyPatchKind.Prefix, "Game.Widget::Calibrate(Il2CppGame.Gadget)")]
    [InlineData("Mod.MissingPatch", "Prefix", HarmonyPatchKind.Prefix, "Game.Widget::Missing")]
    [InlineData("Mod.MissingTypeTargetPatch", "Prefix", HarmonyPatchKind.Prefix, "Mod.ModOnlyType::Run")]
    [InlineData("Mod.NoOverloadPatch", "Prefix", HarmonyPatchKind.Prefix, "Game.Widget::Compute(System.String)")]
    public async Task Attribute_patches_carry_canonical_target_attempts(
        string typeName,
        string methodName,
        HarmonyPatchKind kind,
        string target)
    {
        var member = await FindMemberAsync(typeName, methodName);

        var fact = Assert.Single(member.Patches ?? []);
        Assert.Equal(kind, fact.Kind);
        Assert.Equal(target, fact.TargetSignature);
        Assert.Null(fact.Reason);
        Assert.Equal(RelationshipEvidence.Metadata, fact.Evidence);
        Assert.Null(fact.PatchMethodType);
    }

    [Fact]
    public async Task Multiple_kind_attributes_resolve_to_the_first_kind()
    {
        var member = await FindMemberAsync("Mod.DoublePatch", "Both");

        var fact = Assert.Single(member.Patches ?? []);
        Assert.Equal(HarmonyPatchKind.Prefix, fact.Kind);
        Assert.Equal("Game.Widget::Untouched", fact.TargetSignature);
    }

    [Theory]
    [InlineData("Mod.TargetMethodPatch", "Prefix", HarmonyPatchKind.Prefix, HarmonyPatchReasons.RuntimeComputedTarget)]
    [InlineData("Mod.TargetMethodsPatch", "Postfix", HarmonyPatchKind.Postfix, HarmonyPatchReasons.RuntimeComputedTarget)]
    [InlineData("Mod.EmptyTargetPatch", "Prefix", HarmonyPatchKind.Prefix, HarmonyPatchReasons.NoTargetSpecified)]
    [InlineData("Mod.PartialPatch", "Prefix", HarmonyPatchKind.Prefix, HarmonyPatchReasons.UnknownDeclaringType)]
    [InlineData("Mod.GetterNoNamePatch", "Prefix", HarmonyPatchKind.Prefix, HarmonyPatchReasons.UnknownMemberName)]
    public async Task Unbuildable_attribute_targets_carry_reasons(
        string typeName,
        string methodName,
        HarmonyPatchKind kind,
        string reason)
    {
        var member = await FindMemberAsync(typeName, methodName);

        var fact = Assert.Single(member.Patches ?? []);
        Assert.Equal(kind, fact.Kind);
        Assert.Null(fact.TargetSignature);
        Assert.Equal(reason, fact.Reason);
    }

    [Fact]
    public async Task Unsupported_method_types_keep_the_unmapped_target_text()
    {
        var member = await FindMemberAsync("Mod.EnumeratorPatch", "Prefix");

        var fact = Assert.Single(member.Patches ?? []);
        Assert.Equal(HarmonyPatchKind.Prefix, fact.Kind);
        Assert.Equal("Game.Widget::Run", fact.TargetSignature);
        Assert.Equal(HarmonyPatchReasons.UnsupportedMethodType, fact.Reason);
    }

    [Theory]
    [InlineData("Mod.ManualPatch", "Install", HarmonyPatchKind.Prefix, "Game.Widget::Run", "Mod.ManualPatch", "ManualPrefix")]
    [InlineData("Mod.ManualEmptyTypesPatch", "Install", HarmonyPatchKind.Prefix, "Game.Widget::Run()", "Mod.ManualEmptyTypesPatch", "EmptyPrefix")]
    [InlineData("Mod.ManualAmbiguousMethodPatch", "Install", HarmonyPatchKind.Prefix, "Game.Widget::Run", "Mod.ManualAmbiguousMethodPatch", "Do")]
    [InlineData("Mod.ManualUnknownMethodPatch", "Install", HarmonyPatchKind.Prefix, "Game.Widget::Run", "Mod.ManualUnknownMethodPatch", "Missing")]
    [InlineData("Mod.ManualMethodInfoPatch", "Install", HarmonyPatchKind.Prefix, "Game.Widget::Run", "Mod.ManualMethodInfoPatch", "InfoPrefix")]
    [InlineData("Mod.ManualOverloadDisambiguationPatch", "Install", HarmonyPatchKind.Prefix, "Game.Widget::Run", "Mod.ManualOverloadDisambiguationPatch", "Do")]
    [InlineData("Mod.ManualFloatConstantPatch", "Install", HarmonyPatchKind.Prefix, "Game.Widget::Run", "Mod.ManualFloatConstantPatch", "FloatPrefix")]
    public async Task Manual_patches_resolve_from_constant_calls(
        string typeName,
        string methodName,
        HarmonyPatchKind kind,
        string target,
        string patchType,
        string patchMethod)
    {
        var member = await FindMemberAsync(typeName, methodName);

        var fact = Assert.Single(member.Patches ?? []);
        Assert.Equal(kind, fact.Kind);
        Assert.Equal(target, fact.TargetSignature);
        Assert.Null(fact.Reason);
        Assert.Equal(RelationshipEvidence.RecoveredIL, fact.Evidence);
        Assert.Equal(patchType, fact.PatchMethodType);
        Assert.Equal(patchMethod, fact.PatchMethodName);
    }

    [Fact]
    public async Task Manual_patch_with_argument_types_disambiguates_the_patch_method()
    {
        var member = await FindMemberAsync("Mod.ManualOverloadDisambiguationPatch", "Install");

        var fact = Assert.Single(member.Patches ?? []);
        Assert.Equal(["System.Int32"], fact.PatchMethodArgumentTypes);
    }

    [Fact]
    public async Task Multiple_manual_calls_in_one_body_emit_one_fact_each()
    {
        var member = await FindMemberAsync("Mod.ManualOverloadPatch", "Install");

        Assert.Equal(2, (member.Patches ?? []).Count);
        var postfix = member.Patches!.Single(fact => fact.Kind == HarmonyPatchKind.Postfix);
        Assert.Equal("Game.Widget::Compute(System.Int32,System.String)", postfix.TargetSignature);
        Assert.Null(postfix.Reason);
        var prefix = member.Patches!.Single(fact => fact.Kind == HarmonyPatchKind.Prefix);
        Assert.Null(prefix.TargetSignature);
        Assert.Equal(HarmonyPatchReasons.NonConstantArguments, prefix.Reason);
        Assert.Equal("Mod.ManualOverloadPatch", prefix.PatchMethodType);
    }

    [Fact]
    public async Task Non_constant_patch_method_keeps_the_target_text()
    {
        var member = await FindMemberAsync("Mod.ManualNonConstantMethodPatch", "Install");

        var fact = Assert.Single(member.Patches ?? []);
        Assert.Equal(HarmonyPatchKind.Prefix, fact.Kind);
        Assert.Equal("Game.Widget::Untouched", fact.TargetSignature);
        Assert.Equal(HarmonyPatchReasons.NonConstantArguments, fact.Reason);
        Assert.Null(fact.PatchMethodType);
    }

    [Fact]
    public async Task Object_construction_before_a_manual_call_keeps_precision()
    {
        var member = await FindMemberAsync("Mod.ManualNewobjPrecisionPatch", "Install");

        var fact = Assert.Single(member.Patches ?? []);
        Assert.Equal(HarmonyPatchKind.Prefix, fact.Kind);
        Assert.Null(fact.TargetSignature);
        Assert.Equal(HarmonyPatchReasons.NonConstantArguments, fact.Reason);
        Assert.Equal("Mod.ManualNewobjPrecisionPatch", fact.PatchMethodType);
        Assert.Equal("NewobjPrefix", fact.PatchMethodName);
    }

    [Fact]
    public async Task Non_constant_target_types_are_not_wildcards()
    {
        var member = await FindMemberAsync("Mod.ManualNonConstantTypesPatch", "Install");

        var fact = Assert.Single(member.Patches ?? []);
        Assert.Equal(HarmonyPatchKind.Prefix, fact.Kind);
        Assert.Null(fact.TargetSignature);
        Assert.Equal(HarmonyPatchReasons.NonConstantArguments, fact.Reason);
        Assert.Equal("Mod.ManualNonConstantTypesPatch", fact.PatchMethodType);
        Assert.Equal("TypesPrefix", fact.PatchMethodName);
    }

    [Fact]
    public async Task Non_constant_patch_types_forget_the_patch_method()
    {
        var member = await FindMemberAsync("Mod.ManualNonConstantPatchTypesPatch", "Install");

        var fact = Assert.Single(member.Patches ?? []);
        Assert.Equal(HarmonyPatchKind.Prefix, fact.Kind);
        Assert.Equal("Game.Widget::Run", fact.TargetSignature);
        Assert.Equal(HarmonyPatchReasons.NonConstantArguments, fact.Reason);
        Assert.Null(fact.PatchMethodType);
        Assert.Null(fact.PatchMethodName);
    }

    [Fact]
    public async Task Ternary_target_types_are_not_resolved_from_one_arm()
    {
        var member = await FindMemberAsync("Mod.ManualTernaryPatch", "Install");

        var fact = Assert.Single(member.Patches ?? []);
        Assert.Equal(HarmonyPatchKind.Prefix, fact.Kind);
        Assert.Null(fact.TargetSignature);
        Assert.Equal(HarmonyPatchReasons.UnrecognizedManualShape, fact.Reason);
        Assert.Equal("Mod.ManualTernaryPatch", fact.PatchMethodType);
        Assert.Equal("TernaryPrefix", fact.PatchMethodName);
    }

    [Fact]
    public async Task Branched_manual_calls_are_not_guessed()
    {
        var member = await FindMemberAsync("Mod.ManualBranchPatch", "Install");

        var fact = Assert.Single(member.Patches ?? []);
        Assert.Equal(HarmonyPatchKind.Prefix, fact.Kind);
        Assert.Null(fact.TargetSignature);
        Assert.Equal(HarmonyPatchReasons.UnrecognizedManualShape, fact.Reason);
        Assert.Equal("Mod.ManualBranchPatch", fact.PatchMethodType);
    }

    [Theory]
    [InlineData("Mod.ManualPatch", "ManualPrefix")]
    [InlineData("Mod.ManualOverloadPatch", "HelperName")]
    [InlineData("Mod.ManualNonConstantMethodPatch", "GetPatch")]
    [InlineData("Mod.TargetMethodPatch", "TargetMethod")]
    [InlineData("Mod.TargetMethodsPatch", "TargetMethods")]
    [InlineData("Mod.DecoderProbe", ".ctor")]
    [InlineData("Mod.RunPatch", ".ctor")]
    [InlineData("Mod.LonelyPatch", "Prefix")]
    public async Task Non_patch_members_carry_no_patch_facts(string typeName, string methodName)
    {
        var member = await FindMemberAsync(typeName, methodName);

        Assert.Empty(member.Patches ?? []);
    }

    private static async Task<ManagedMemberFacts> FindMemberAsync(string typeName, string methodName)
    {
        var decompilation = await ModAsync();
        var type = decompilation.Types.Single(candidate => candidate.FullName == typeName);
        return type.Members.Single(candidate => candidate.Name == methodName);
    }
}
