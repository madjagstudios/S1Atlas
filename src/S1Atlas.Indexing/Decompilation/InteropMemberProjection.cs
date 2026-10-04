using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Decompilation;

/// <summary>Shared game-to-interop member names, kinds, and type compatibility.</summary>
public static class InteropMemberProjection
{
    private const string ArrayNamespace = "Il2CppInterop.Runtime.InteropTypes.Arrays.";

    public static IReadOnlyList<string> CandidateNames(ManagedMemberFacts gameMember, bool hasBackingProperty)
    {
        if (!IsBackingField(gameMember) || !hasBackingProperty)
            return [gameMember.Name];

        var propertyName = GetBackingPropertyName(gameMember.Name);
        return
        [
            gameMember.Name,
            propertyName + "k__BackingField",
            propertyName + "_k__BackingField",
            propertyName + "__BackingField",
            "_" + propertyName + "_k__BackingField",
            propertyName
        ];
    }

    public static IReadOnlyList<ManagedMemberKind> CandidateKinds(ManagedMemberFacts gameMember) =>
        gameMember.Kind switch
        {
            ManagedMemberKind.Field => [ManagedMemberKind.Field, ManagedMemberKind.Property],
            ManagedMemberKind.Property => [ManagedMemberKind.Property, ManagedMemberKind.Field],
            _ => [gameMember.Kind]
        };

    public static bool IsSignatureCompatible(ManagedMemberFacts gameMember, ManagedMemberFacts interopMember) =>
        gameMember.GenericParameterCount == interopMember.GenericParameterCount &&
        gameMember.ParameterTypesOrEmpty.Select(NormalizeType)
            .SequenceEqual(interopMember.ParameterTypesOrEmpty.Select(NormalizeType), StringComparer.Ordinal) &&
        CompatibleValueType(MemberValueType(gameMember), MemberValueType(interopMember));

    public static bool IsBackingField(ManagedMemberFacts member) =>
        member.Kind == ManagedMemberKind.Field &&
        member.Name.StartsWith('<') &&
        member.Name.EndsWith(">k__BackingField", StringComparison.Ordinal);

    public static string GetBackingPropertyName(string fieldName) =>
        fieldName[1..fieldName.IndexOf(">k__BackingField", StringComparison.Ordinal)];

    public static string NormalizeType(string typeName)
    {
        var canonical = CanonicalSignatureRenderer.RenderType(typeName);
        var genericStart = FindGenericStart(canonical);
        if (genericStart < 0)
        {
            var suffixStart = canonical.IndexOfAny(['[', '*', '&', '?']);
            var name = suffixStart < 0 ? canonical : canonical[..suffixStart];
            var suffix = suffixStart < 0 ? "" : canonical[suffixStart..];
            return (name == ArrayNamespace + "Il2CppStringArray"
                ? "System.String[]"
                : InteropTypeNames.Normalize(name)) + suffix;
        }

        var genericEnd = genericStart;
        var depth = 0;
        for (; genericEnd < canonical.Length; genericEnd++)
        {
            depth += canonical[genericEnd] switch { '<' => 1, '>' => -1, _ => 0 };
            if (depth == 0) break;
        }
        var genericName = canonical[..genericStart];
        var arguments = SplitTypeArguments(canonical[(genericStart + 1)..genericEnd])
            .Select(argument => argument.Length == 0 ? "" : NormalizeType(argument)).ToArray();
        var genericSuffix = canonical[(genericEnd + 1)..];
        if (arguments.Length == 1 && arguments[0].Length != 0 && (genericName == ArrayNamespace + "Il2CppReferenceArray`1"
            || genericName == ArrayNamespace + "Il2CppStructArray`1"))
            return arguments[0] + "[]" + genericSuffix;

        return InteropTypeNames.Normalize(genericName) + "<" + string.Join(',', arguments) + ">" + genericSuffix;
    }

    private static int FindGenericStart(string canonical)
    {
        // Compiler-generated names such as <>c and <Run>d__0 are literal names.
        // Canonical generic argument lists follow a numeric `arity suffix.
        for (var open = canonical.IndexOf('<'); open >= 0; open = canonical.IndexOf('<', open + 1))
        {
            var tick = open == 0 ? -1 : canonical.LastIndexOf('`', open - 1);
            if (tick >= 0 && int.TryParse(canonical.AsSpan(tick + 1, open - tick - 1), out var arity) && arity > 0)
                return open;
        }
        return -1;
    }

    private static bool CompatibleValueType(string? gameType, string? interopType) =>
        gameType is null
            ? interopType is null
            : interopType is not null && string.Equals(NormalizeType(gameType), NormalizeType(interopType), StringComparison.Ordinal);

    private static string? MemberValueType(ManagedMemberFacts member) =>
        member.Kind == ManagedMemberKind.Method ? member.ReturnType : member.ValueType;

    private static IEnumerable<string> SplitTypeArguments(string arguments)
    {
        var start = 0;
        var depth = 0;
        for (var index = 0; index < arguments.Length; index++)
        {
            depth += arguments[index] switch { '<' or '[' or '(' => 1, '>' or ']' or ')' => -1, _ => 0 };
            if (arguments[index] == ',' && depth == 0)
            {
                yield return arguments[start..index];
                start = index + 1;
            }
        }
        yield return arguments[start..];
    }
}
