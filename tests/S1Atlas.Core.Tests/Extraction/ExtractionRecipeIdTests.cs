using S1Atlas.Core.Extraction;
using Xunit;

namespace S1Atlas.Core.Tests.Extraction;

public sealed class ExtractionRecipeIdTests
{
    public static IEnumerable<object[]> RecipeMutations()
    {
        yield return [new Func<ExtractionRecipe, ExtractionRecipe>(value => value with { BuildId = new string('f', 64) })];
        yield return [new Func<ExtractionRecipe, ExtractionRecipe>(value => value with { ToolInstanceId = new string('e', 64) })];
        yield return [new Func<ExtractionRecipe, ExtractionRecipe>(value => value with { ProfileDigest = new string('d', 64) })];
        yield return [new Func<ExtractionRecipe, ExtractionRecipe>(value => value with { AdapterVersion = 2 })];
        yield return [new Func<ExtractionRecipe, ExtractionRecipe>(value => value with { ExtractionSchemaVersion = 2 })];
    }

    [Fact]
    public void Create_ForRecipe_ReturnsLowerCaseSha256Identity()
    {
        var recipe = ValidRecipe;

        var id = ExtractionRecipeId.Create(recipe);

        Assert.Matches("^[0-9a-f]{64}$", id);
    }

    [Theory]
    [MemberData(nameof(RecipeMutations))]
    public void Create_WhenEffectiveRecipeFieldChanges_ChangesIdentity(
        Func<ExtractionRecipe, ExtractionRecipe> mutate)
    {
        Assert.NotEqual(
            ExtractionRecipeId.Create(ValidRecipe),
            ExtractionRecipeId.Create(mutate(ValidRecipe)));
    }

    private static readonly ExtractionRecipe ValidRecipe = new(
        new string('a', 64), new string('b', 64), new string('c', 64), 1, 1);
}
