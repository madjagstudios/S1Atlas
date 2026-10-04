using S1Atlas.Core;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Decompilation;

namespace S1Atlas.Indexing.ModChecking;

internal sealed class ModCheckInteropResolver
{
    private readonly Dictionary<(string Type, string Signature), List<IndexSymbolRecord>> _stored = [];
    private readonly Dictionary<string, List<(IndexSymbolRecord Symbol, ManagedMemberFacts Facts)>> _game = [];

    public ModCheckInteropResolver(IReadOnlyList<IndexSymbolRecord> symbols, IReadOnlyList<IndexCallableSurfaceRecord> surface)
    {
        var byId = symbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        foreach (var symbol in symbols.Where(symbol => symbol.Kind != "Type"))
        {
            var type = DeclaringType(symbol.QualifiedName);
            var facts = ParseMember(symbol.QualifiedName, symbol.Kind);
            if (facts is null) continue;
            if (!_game.TryGetValue(type, out var members)) _game[type] = members = [];
            members.Add((symbol, facts));
        }
        foreach (var row in surface.Where(row => row.Status == CallableSurfaceStatus.Resolved && row.InteropSignature is not null))
        {
            if (!byId.TryGetValue(row.GameSymbolId, out var symbol)) continue;
            var key = (DeclaringType(symbol.QualifiedName), InteropTypeNames.NormalizeSignature(row.InteropSignature!));
            if (!_stored.TryGetValue(key, out var targets)) _stored[key] = targets = [];
            targets.Add(symbol);
        }
    }

    public IndexSymbolRecord? ResolveReference(string signature, string kind)
    {
        var type = DeclaringType(signature);
        var reference = ParseMember(signature, kind);
        if (reference is null) return null;
        var forms = ReferenceForms(reference).ToArray();
        foreach (var form in forms)
        {
            var stored = (_stored.GetValueOrDefault((type, InteropTypeNames.NormalizeSignature(form.Signature))) ?? [])
                .DistinctBy(symbol => symbol.SymbolId).ToArray();
            if (stored.Length != 0) return stored.Length == 1 ? stored[0] : null;
        }
        if (!_game.TryGetValue(type, out var game)) return null;

        // Exact method shapes precede accessor-to-field projections. A game getter
        // and its property describe separate canonical symbols.
        foreach (var form in forms)
        {
            var candidates = game.Where(candidate => CompatibleNameAndKind(game, candidate.Facts, form)
                && InteropMemberProjection.IsSignatureCompatible(candidate.Facts, form))
                .Select(candidate => candidate.Symbol).DistinctBy(symbol => symbol.SymbolId).ToArray();
            if (candidates.Length != 0) return candidates.Length == 1 ? candidates[0] : null;
        }
        return null;
    }

    public IndexSymbolRecord? ResolveReflection(string type, string name, string kind, IReadOnlyList<string>? arguments)
    {
        type = InteropTypeNames.Normalize(type);
        if (!_game.TryGetValue(type, out var game)) return null;
        var forms = ReflectionForms(name, kind, arguments).ToArray();
        foreach (var form in forms)
        {
            var stored = _stored.Where(pair => pair.Key.Type == type && SurfaceName(pair.Key.Signature) == form.Name)
                .SelectMany(pair => pair.Value)
                .Where(symbol => form.Kind is ManagedMemberKind.Field or ManagedMemberKind.Property
                    ? symbol.Kind is "Field" or "Property" : symbol.Kind == form.Kind.ToString())
                .Where(symbol => ReflectionSignatureMatches(ParseMember(symbol.QualifiedName, symbol.Kind), form))
                .DistinctBy(symbol => symbol.SymbolId).ToArray();
            if (stored.Length != 0) return stored.Length == 1 ? stored[0] : null;
        }
        foreach (var form in forms)
        {
            var candidates = game.Where(candidate => CompatibleNameAndKind(game, candidate.Facts, form)
                    && ReflectionSignatureMatches(candidate.Facts, form))
                .Select(candidate => candidate.Symbol).DistinctBy(symbol => symbol.SymbolId).ToArray();
            if (candidates.Length != 0) return candidates.Length == 1 ? candidates[0] : null;
        }
        return null;
    }

    public IReadOnlyList<string>? ReflectionArguments(string type, string name, IReadOnlyList<string>? arguments, bool propertyAccessor)
    {
        if (!propertyAccessor || arguments is not null || !TryAccessor(name, out var setter, out var propertyName)
            || !_game.TryGetValue(InteropTypeNames.Normalize(type), out var game)) return arguments;
        var property = new ManagedMemberFacts(propertyName, ManagedMemberKind.Property, propertyName, false, []);
        var shapes = game.Where(candidate => CompatibleNameAndKind(game, candidate.Facts, property))
            .Select(candidate => candidate.Facts.ParameterTypesOrEmpty
                .Concat(setter && candidate.Facts.ValueType is not null ? [candidate.Facts.ValueType] : [])
                .ToArray())
            .DistinctBy(parameters => string.Join(',', parameters.Select(InteropMemberProjection.NormalizeType)))
            .ToArray();
        return shapes.Length == 1 ? shapes[0] : arguments;
    }

    private static bool ReflectionSignatureMatches(ManagedMemberFacts? member, ManagedMemberFacts reference) =>
        member is not null && (reference.ParameterTypes is null || ParametersMatch(member, reference.ParameterTypes))
        && (reference.ValueType is null || member.ValueType is not null
            && InteropMemberProjection.NormalizeType(member.ValueType) == InteropMemberProjection.NormalizeType(reference.ValueType));

    private static bool ParametersMatch(ManagedMemberFacts? member, IReadOnlyList<string> arguments) =>
        member is not null && member.ParameterTypesOrEmpty.Select(InteropMemberProjection.NormalizeType)
            .SequenceEqual(arguments.Select(InteropMemberProjection.NormalizeType), StringComparer.Ordinal);

    private static bool CompatibleNameAndKind(
        IReadOnlyList<(IndexSymbolRecord Symbol, ManagedMemberFacts Facts)> typeMembers, ManagedMemberFacts game, ManagedMemberFacts reference)
    {
        var hasBackingProperty = InteropMemberProjection.IsBackingField(game) && typeMembers.Any(candidate =>
            candidate.Facts.Kind == ManagedMemberKind.Property && candidate.Facts.Name == InteropMemberProjection.GetBackingPropertyName(game.Name));
        return InteropMemberProjection.CandidateKinds(game).Contains(reference.Kind)
            && InteropMemberProjection.CandidateNames(game, hasBackingProperty).Contains(reference.Name, StringComparer.Ordinal);
    }

    private static IEnumerable<ManagedMemberFacts> ReferenceForms(ManagedMemberFacts member)
    {
        yield return member;
        if (member.Kind != ManagedMemberKind.Method || !TryAccessor(member.Name, out var setter, out var propertyName)) yield break;
        string? value = null;
        if (!setter && member.ParameterTypesOrEmpty.Count == 0) value = member.ReturnType;
        else if (setter && member.ReturnType == "System.Void" && member.ParameterTypesOrEmpty.Count == 1)
            value = member.ParameterTypesOrEmpty[0];
        if (value is not null)
        {
            yield return new ManagedMemberFacts(propertyName, ManagedMemberKind.Property,
                CanonicalSignatureRenderer.RenderType(value) + " " + propertyName, false, [], ValueType: value);
        }
    }

    private static IEnumerable<ManagedMemberFacts> ReflectionForms(string name, string kind, IReadOnlyList<string>? arguments)
    {
        yield return new(name, Enum.Parse<ManagedMemberKind>(kind), name, false, [], ParameterTypes: arguments);
        if (kind != "Method" || !TryAccessor(name, out var setter, out var propertyName)
            || arguments is not null && arguments.Count != (setter ? 1 : 0)) yield break;
        yield return new(propertyName, ManagedMemberKind.Property, propertyName, false, [],
            ParameterTypes: arguments is null ? null : [], ValueType: setter ? arguments?.FirstOrDefault() : null);
    }

    private static bool TryAccessor(string name, out bool setter, out string propertyName)
    {
        setter = name.StartsWith("set_", StringComparison.Ordinal);
        propertyName = name.Length > 4 ? name[4..] : "";
        return propertyName.Length != 0 && (setter || name.StartsWith("get_", StringComparison.Ordinal));
    }

    private static string SurfaceName(string signature)
    {
        var tail = signature.Contains("::", StringComparison.Ordinal) ? signature.Split("::", 2)[1] : signature;
        var open = tail.IndexOf('(');
        return open >= 0 ? tail[..open] : tail[(tail.LastIndexOf(' ') + 1)..];
    }

    private static string DeclaringType(string signature) => InteropTypeNames.Normalize(signature.Split("::", 2)[0]);

    private static ManagedMemberFacts? ParseMember(string signature, string kind)
    {
        var tail = signature.Contains("::", StringComparison.Ordinal) ? signature.Split("::", 2)[1] : signature;
        if (kind is "Method" or "Constructor")
        {
            var open = tail.IndexOf('(');
            var close = tail.LastIndexOf(')');
            if (open < 0 || close < open || close + 1 >= tail.Length || tail[close + 1] != ':') return null;
            var name = tail[..open];
            var tick = name.LastIndexOf('`');
            var arity = tick >= 0 && int.TryParse(name[(tick + 1)..], out var count) ? count : 0;
            if (arity != 0) name = name[..tick];
            return new(name, Enum.Parse<ManagedMemberKind>(kind), signature, false, [], SplitParameters(tail[(open + 1)..close]),
                tail[(close + 2)..], GenericParameterCount: arity);
        }
        var space = tail.LastIndexOf(' ');
        if (space < 1) return null;
        return new(tail[(space + 1)..], Enum.Parse<ManagedMemberKind>(kind), tail, false, [], ValueType: tail[..space]);
    }

    private static IReadOnlyList<string> SplitParameters(string parameters)
    {
        if (parameters.Length == 0) return [];
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        for (var index = 0; index < parameters.Length; index++)
        {
            depth += parameters[index] switch { '<' or '[' => 1, '>' or ']' => -1, _ => 0 };
            if (parameters[index] == ',' && depth == 0) { result.Add(parameters[start..index]); start = index + 1; }
        }
        result.Add(parameters[start..]);
        return result;
    }
}
