using S1Atlas.Core.Identity;

namespace S1Atlas.Core.Extraction;

public static class ValidationPolicyFingerprint
{
    public static string Create(ValidationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        using var writer = new CanonicalHashWriter("validation-policy", 1);
        writer.AppendInt32(policy.SchemaVersion);
        writer.AppendString(policy.PolicyId);
        writer.AppendInt32(policy.PolicyVersion);
        var identities = policy.RequiredAssemblyIdentities.Order(StringComparer.Ordinal).ToArray();
        writer.AppendInt32(identities.Length);
        foreach (var identity in identities) writer.AppendString(identity);
        writer.AppendInt32(policy.MinimumManagedAssemblyCount);
        writer.AppendInt32(policy.MinimumTypeDefinitionCount);
        writer.AppendInt32(policy.MinimumMethodDefinitionCount);
        writer.AppendInt64(policy.MinimumTotalManagedBytes);
        writer.AppendString(policy.ComparativeWarningRelativeChange.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        writer.AppendString(policy.CatastrophicDecreaseRelativeChange.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        return writer.Complete();
    }
}
