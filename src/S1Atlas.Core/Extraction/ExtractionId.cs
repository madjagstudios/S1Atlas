using S1Atlas.Core.Identity;

namespace S1Atlas.Core.Extraction;

/// <summary>
/// Computes the extraction ID, the production identity of validated output.
/// Deliberately accepts only the recipe ID and artifact manifest digest: validation
/// policy is provenance, not production identity, so policy-only revalidation can
/// never change an extraction ID.
/// </summary>
public static class ExtractionId
{
    public static string Create(
        string recipeId,
        string artifactManifestDigest)
    {
        RequireLowerCaseSha256(recipeId, nameof(recipeId));
        RequireLowerCaseSha256(artifactManifestDigest, nameof(artifactManifestDigest));

        using var writer = new CanonicalHashWriter("extraction-id", 1);
        writer.AppendString(recipeId);
        writer.AppendString(artifactManifestDigest);
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
