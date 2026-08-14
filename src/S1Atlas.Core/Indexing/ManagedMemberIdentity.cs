namespace S1Atlas.Core.Indexing;

public static class ManagedMemberIdentity
{
    public static string Render(string containingType, ManagedMemberFacts member)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containingType);
        ArgumentNullException.ThrowIfNull(member);

        if (member.Kind is ManagedMemberKind.Constructor or ManagedMemberKind.Method && member.ReturnType is not null)
        {
            return CanonicalSignatureRenderer.RenderMethod(
                containingType,
                member.Name,
                member.ReturnType,
                member.ParameterTypesOrEmpty,
                member.GenericParameterCount);
        }

        return containingType + "::" + member.Signature;
    }
}
