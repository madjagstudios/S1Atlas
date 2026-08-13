using S1Atlas.Core.Identity;

namespace S1Atlas.Core.Extraction;

public static class ExtractionRecipeId
{
    public static string Create(ExtractionRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        RequireLowerCaseSha256(recipe.BuildId, nameof(recipe.BuildId));
        RequireLowerCaseSha256(recipe.ToolInstanceId, nameof(recipe.ToolInstanceId));
        RequireLowerCaseSha256(recipe.ProfileDigest, nameof(recipe.ProfileDigest));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recipe.AdapterVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recipe.ExtractionSchemaVersion);

        using var writer = new CanonicalHashWriter("extraction-recipe", 1);
        writer.AppendString(recipe.BuildId);
        writer.AppendString(recipe.ToolInstanceId);
        writer.AppendString(recipe.ProfileDigest);
        writer.AppendInt32(recipe.AdapterVersion);
        writer.AppendInt32(recipe.ExtractionSchemaVersion);
        return writer.Complete();
    }

    private static void RequireLowerCaseSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The value must be a lower-case SHA-256 digest.", parameterName);
        }
    }
}
