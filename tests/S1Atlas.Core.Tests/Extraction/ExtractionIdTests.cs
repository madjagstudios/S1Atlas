using S1Atlas.Core.Extraction;
using Xunit;

namespace S1Atlas.Core.Tests.Extraction;

public sealed class ExtractionIdTests
{
    [Fact]
    public void Create_WithSameRecipeAndManifest_ReturnsSameLowercaseSha256()
    {
        var recipeId = new string('a', 64);
        var manifestDigest = new string('b', 64);

        var first = ExtractionId.Create(recipeId, manifestDigest);
        var second = ExtractionId.Create(recipeId, manifestDigest);

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void Create_WhenRecipeOrManifestChanges_ReturnsDifferentId()
    {
        var baseline = ExtractionId.Create(new string('a', 64), new string('b', 64));
        var changedRecipe = ExtractionId.Create(new string('c', 64), new string('b', 64));
        var changedManifest = ExtractionId.Create(new string('a', 64), new string('d', 64));

        Assert.NotEqual(baseline, changedRecipe);
        Assert.NotEqual(baseline, changedManifest);
        Assert.NotEqual(changedRecipe, changedManifest);
    }

    [Fact]
    public void Create_DoesNotAcceptPolicyAsAnInput()
    {
        var method = typeof(ExtractionId).GetMethod(nameof(ExtractionId.Create));
        Assert.NotNull(method);

        var parameters = method!.GetParameters();

        Assert.Equal(2, parameters.Length);
        Assert.All(parameters, parameter =>
            Assert.DoesNotContain("policy", parameter.Name!, StringComparison.OrdinalIgnoreCase));
    }
}
