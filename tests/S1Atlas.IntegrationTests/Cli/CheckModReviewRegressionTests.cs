using System.Text.Json;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.IntegrationTests.Cli;

public sealed class CheckModReviewRegressionTests
{
    [Fact]
    public async Task Dynamic_harmony_targets_remain_unresolved_patch_rows()
    {
        await using var baseline = await CheckModCliAtlas.CreateAsync();
        await using var variant = await CheckModCliAtlas.CreateAsync(regressionVariant: ModCheckRegressionVariant.DynamicPatch);
        var baselineData = Report(baseline, 3, baseline.Seed.FromBuildId, baseline.Seed.ToBuildId);
        var data = Report(variant, 3, variant.Seed.FromBuildId, variant.Seed.ToBuildId);
        var patches = data.GetProperty("dependencies").EnumerateArray()
            .Where(dep => dep.GetProperty("status").GetString() == "unresolved"
                && dep.GetProperty("sources").EnumerateArray().Any(source => source.GetProperty("kind").GetString() == "harmony_patch"))
            .ToArray();
        Assert.Contains(patches, dep => dep.GetProperty("reason").GetString() == "non-constant-target"
            && dep.GetProperty("sources").EnumerateArray().Any(source => source.GetProperty("member").GetString()!.Contains("DynamicPatch::Prefix", StringComparison.Ordinal)));
        Assert.Contains(patches, dep => dep.GetProperty("reason").GetString() == "runtime-computed-target"
            && dep.GetProperty("sources").EnumerateArray().Any(source => source.GetProperty("member").GetString()!.Contains("TargetMethodPatch::Prefix", StringComparison.Ordinal)));
        Assert.True(data.GetProperty("summary").GetProperty("patchTargets").GetProperty("unresolved").GetInt32()
            >= baselineData.GetProperty("summary").GetProperty("patchTargets").GetProperty("unresolved").GetInt32() + 2);
    }

    [Fact]
    public async Task Missing_game_type_used_only_in_mod_signature_is_unresolved()
    {
        await using var atlas = await CheckModCliAtlas.CreateAsync(includeFrom: false, regressionVariant: ModCheckRegressionVariant.MissingSignatureType);
        var data = Report(atlas, 0);
        Assert.True(data.GetProperty("singleBuild").GetBoolean());
        var missing = Assert.Single(data.GetProperty("dependencies").EnumerateArray(), dep =>
            dep.GetProperty("name").GetString() == "Demo.MissingType" && dep.GetProperty("kind").GetString() == "Type");
        Assert.Equal("unresolved", missing.GetProperty("status").GetString());
        Assert.Equal("target-type-not-found", missing.GetProperty("reason").GetString());
        Assert.Contains(missing.GetProperty("sources").EnumerateArray(), source =>
            source.GetProperty("member").GetString()!.Contains("MissingField", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Typeof_token_without_signature_reference_preserves_missing_and_existing_game_types()
    {
        await using var atlas = await CheckModCliAtlas.CreateAsync(includeFrom: false, regressionVariant: ModCheckRegressionVariant.TypeTokenOnly);
        var data = Report(atlas, 0);
        var dependencies = data.GetProperty("dependencies").EnumerateArray().ToArray();
        var missing = Assert.Single(dependencies, dep =>
            dep.GetProperty("name").GetString() == "Demo.MissingType" && dep.GetProperty("kind").GetString() == "Type");
        Assert.Equal("unresolved", missing.GetProperty("status").GetString());
        Assert.Equal("target-type-not-found", missing.GetProperty("reason").GetString());
        Assert.Contains(missing.GetProperty("sources").EnumerateArray(), source =>
            source.GetProperty("kind").GetString() == "direct_reference"
            && source.GetProperty("member").GetString()!.Contains("MissingTokenOnly", StringComparison.Ordinal));
        var existing = Assert.Single(dependencies, dep =>
            dep.GetProperty("name").GetString() == "Demo.Api" && dep.GetProperty("kind").GetString() == "Type");
        Assert.Equal("resolved", existing.GetProperty("status").GetString());
        Assert.Contains(existing.GetProperty("sources").EnumerateArray(), source =>
            source.GetProperty("kind").GetString() == "direct_reference"
            && source.GetProperty("member").GetString()!.Contains("ExistingTokenOnly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Other_mod_reference_is_excluded_and_counted_as_external()
    {
        await using var baseline = await CheckModCliAtlas.CreateAsync();
        await using var variant = await CheckModCliAtlas.CreateAsync(regressionVariant: ModCheckRegressionVariant.OtherModReference);
        var baselineData = Report(baseline, 3, baseline.Seed.FromBuildId, baseline.Seed.ToBuildId);
        var data = Report(variant, 3, variant.Seed.FromBuildId, variant.Seed.ToBuildId);
        Assert.DoesNotContain(data.GetProperty("dependencies").EnumerateArray(), dep =>
            dep.GetProperty("name").GetString()!.StartsWith("OtherMod.", StringComparison.Ordinal));
        Assert.True(data.GetProperty("summary").GetProperty("externalReferencesNotChecked").GetInt32()
            > baselineData.GetProperty("summary").GetProperty("externalReferencesNotChecked").GetInt32());
    }

    [Fact]
    public async Task Harmony_patch_for_other_mod_is_external_without_game_patch_row()
    {
        await using var baseline = await CheckModCliAtlas.CreateAsync();
        await using var variant = await CheckModCliAtlas.CreateAsync(regressionVariant: ModCheckRegressionVariant.ExternalHarmony);
        var baselineData = Report(baseline, 3, baseline.Seed.FromBuildId, baseline.Seed.ToBuildId);
        var data = Report(variant, 3, variant.Seed.FromBuildId, variant.Seed.ToBuildId);
        Assert.DoesNotContain(data.GetProperty("dependencies").EnumerateArray(), dep =>
            dep.GetProperty("name").GetString()!.Contains("OtherMod.", StringComparison.Ordinal));
        var baselinePatches = baselineData.GetProperty("summary").GetProperty("patchTargets");
        var variantPatches = data.GetProperty("summary").GetProperty("patchTargets");
        foreach (var status in baselinePatches.EnumerateObject())
            Assert.Equal(status.Value.GetInt32(), variantPatches.GetProperty(status.Name).GetInt32());
        Assert.True(data.GetProperty("summary").GetProperty("externalReferencesNotChecked").GetInt32()
            > baselineData.GetProperty("summary").GetProperty("externalReferencesNotChecked").GetInt32());
    }

    [Fact]
    public async Task Interop_reflection_parameter_type_matches_game_overload_before_comparison()
    {
        await using var atlas = await CheckModCliAtlas.CreateAsync(regressionVariant: ModCheckRegressionVariant.InteropReflection);
        var data = Report(atlas, 3, atlas.Seed.FromBuildId, atlas.Seed.ToBuildId);
        var changed = Assert.Single(data.GetProperty("dependencies").EnumerateArray(), dep =>
            dep.GetProperty("name").GetString()!.Contains("ReflectChanged", StringComparison.Ordinal)
            && dep.GetProperty("kind").GetString() == "Method");
        Assert.Equal("signature_changed", changed.GetProperty("status").GetString());
        Assert.Equal("Demo.Api::ReflectChanged(Demo.Arg):System.Void", changed.GetProperty("beforeSignature").GetString());
        Assert.Equal("Demo.Api::ReflectChanged(Demo.Arg,System.String):System.Void", changed.GetProperty("afterSignature").GetString());
        Assert.Contains(changed.GetProperty("sources").EnumerateArray(), source =>
            source.GetProperty("kind").GetString() == "reflection"
            && source.GetProperty("member").GetString()!.Contains("InteropReflectionDependency::Reflect", StringComparison.Ordinal));
    }

    private static JsonElement Report(CheckModCliAtlas atlas, int exitCode, params string[] builds)
    {
        var args = new List<string> { "check-mod", atlas.Seed.ModPath };
        if (builds.Length == 2)
            args.AddRange(["--from", builds[0], "--to", builds[1]]);
        args.Add("--json");
        var result = atlas.Run([.. args]);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("check-mod", document.RootElement.GetProperty("command").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("error").ValueKind);
        return document.RootElement.GetProperty("data").Clone();
    }
}
