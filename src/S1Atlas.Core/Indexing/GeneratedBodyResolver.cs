using S1Atlas.Core.Storage;

namespace S1Atlas.Core.Indexing;

public static class GeneratedBodyResolver
{
    public static bool IsGeneratedSymbol(string qualifiedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedName);
        var name = qualifiedName;
        var slash = name.IndexOf('/');
        if (slash >= 0)
            name = name[(slash + 1)..];
        var separator = name.IndexOf("::", StringComparison.Ordinal);
        if (separator < 0)
            return IsGeneratedTypeName(name);
        if (SimpleMemberName(name[(separator + 2)..]).StartsWith('<'))
            return true;
        return IsGeneratedTypeName(name[..separator]);
    }

    public static IReadOnlyDictionary<string, GeneratedBodyMapping> ResolveAll(
        ManagedDecompilation decompilation,
        CodebaseKind codebase,
        CodeChannel channel)
    {
        ArgumentNullException.ThrowIfNull(decompilation);
        var entries = new List<Entry>();
        foreach (var type in decompilation.Types)
        {
            foreach (var member in type.Members)
            {
                if (member.Kind is not (ManagedMemberKind.Method or ManagedMemberKind.Constructor))
                    continue;
                var kind = member.Kind == ManagedMemberKind.Constructor ? SymbolKind.Constructor : SymbolKind.Method;
                var qualifiedName = ManagedMemberIdentity.Render(type.FullName, member);
                entries.Add(new Entry(
                    SymbolIdentity.Create(codebase, channel, kind, qualifiedName).CanonicalKey,
                    qualifiedName,
                    type.FullName,
                    member.Name,
                    member.References));
            }
        }

        return ResolveEntries(entries);
    }

    public static IReadOnlyDictionary<string, GeneratedBodyMapping> MapSymbols(
        IEnumerable<IndexSymbolRecord> symbols,
        CodebaseKind codebase,
        CodeChannel channel)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var entries = new List<Entry>();
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("Method" or "Constructor"))
                continue;
            var separator = symbol.QualifiedName.IndexOf("::", StringComparison.Ordinal);
            if (separator < 0)
                continue;
            entries.Add(new Entry(
                symbol.CanonicalKey,
                symbol.QualifiedName,
                symbol.QualifiedName[..separator],
                StripArity(SimpleMemberName(symbol.QualifiedName[(separator + 2)..])),
                []));
        }

        return ResolveEntries(entries);
    }

    private sealed record Entry(
        string Key,
        string QualifiedName,
        string ContainingType,
        string MemberName,
        IReadOnlyList<ManagedReferenceFact> References);

    private sealed record ParsedMember(string Anchor, string Step, bool HasAnchor);

    private static IReadOnlyDictionary<string, GeneratedBodyMapping> ResolveEntries(IReadOnlyList<Entry> entries)
    {
        var users = new Dictionary<(string UserType, string Name), List<Entry>>();
        var locals = new Dictionary<(string UserType, string Local), Entry>();
        foreach (var entry in entries)
        {
            if (IsGeneratedSymbol(entry.QualifiedName))
                continue;
            var key = (UserTypeOf(entry.ContainingType), entry.MemberName);
            if (!users.TryGetValue(key, out var list))
            {
                list = [];
                users[key] = list;
            }

            list.Add(entry);
        }

        foreach (var entry in entries)
        {
            if (!IsGeneratedSymbol(entry.QualifiedName))
                continue;
            if (TryParseLocal(entry.MemberName, out _, out var local))
                locals.TryAdd((UserTypeOf(entry.ContainingType), local), entry);
        }

        var output = new Dictionary<string, GeneratedBodyMapping>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!IsGeneratedSymbol(entry.QualifiedName))
                continue;
            ResolveEntry(entry, users, locals, output, []);
        }

        return output;
    }

    private static GeneratedBodyMapping ResolveEntry(
        Entry entry,
        Dictionary<(string UserType, string Name), List<Entry>> users,
        Dictionary<(string UserType, string Local), Entry> locals,
        Dictionary<string, GeneratedBodyMapping> output,
        HashSet<string> visited)
    {
        if (output.TryGetValue(entry.Key, out var done))
            return done;
        if (!visited.Add(entry.Key))
            return Unmapped(entry.Key, output, $"declaring method '{entry.MemberName}' is not indexed");

        var parsed = ParseMember(entry);
        if (!parsed.HasAnchor)
            return Unmapped(entry.Key, output, $"declaring method '{entry.MemberName}' is not indexed");

        var userType = UserTypeOf(entry.ContainingType);
        if (users.TryGetValue((userType, parsed.Anchor), out var candidates))
        {
            if (candidates.Count == 1)
                return Mapped(entry.Key, output, candidates[0].Key, StepFor(parsed, candidates[0].QualifiedName));
            return Unmapped(entry.Key, output, $"ambiguous overloads sharing '{parsed.Anchor}'");
        }

        if (locals.TryGetValue((userType, parsed.Anchor), out var local))
        {
            var parent = ResolveEntry(local, users, locals, output, visited);
            if (parent.DeclaringKey is null)
                return Unmapped(entry.Key, output, parent.Detail);
            return Mapped(entry.Key, output, parent.DeclaringKey, parent.Detail + ", " + StepFor(parsed, local.QualifiedName));
        }

        return Unmapped(entry.Key, output, $"declaring method '{parsed.Anchor}' is not indexed");
    }

    private static ParsedMember ParseMember(Entry entry)
    {
        if (TryParseLambda(entry.MemberName, out var lambdaAnchor))
            return new ParsedMember(lambdaAnchor, "in lambda", true);
        if (TryParseLocal(entry.MemberName, out var localAnchor, out var local))
            return new ParsedMember(localAnchor, $"in local function {local}", true);
        if (TrailingTypeAnchor(entry.ContainingType, out var typeAnchor))
            return new ParsedMember(typeAnchor, string.Empty, true);
        return new ParsedMember(entry.MemberName, string.Empty, false);
    }

    private static string StepFor(ParsedMember parsed, string declaringQualifiedName) =>
        parsed.Step.Length > 0 ? parsed.Step : StateMachineStep(declaringQualifiedName);

    private static string StateMachineStep(string declaringQualifiedName)
    {
        var separator = declaringQualifiedName.IndexOf("::", StringComparison.Ordinal);
        var rest = separator < 0 ? declaringQualifiedName : declaringQualifiedName[(separator + 2)..];
        var colon = rest.LastIndexOf(':');
        var returns = colon < 0 ? string.Empty : rest[(colon + 1)..];
        return returns.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal)
            || returns.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal)
            ? "in async state machine"
            : "in iterator state machine";
    }

    private static GeneratedBodyMapping Mapped(
        string key, Dictionary<string, GeneratedBodyMapping> output, string declaringKey, string detail)
    {
        var mapping = new GeneratedBodyMapping(declaringKey, detail, false);
        output[key] = mapping;
        return mapping;
    }

    private static GeneratedBodyMapping Unmapped(
        string key, Dictionary<string, GeneratedBodyMapping> output, string detail)
    {
        var mapping = new GeneratedBodyMapping(null, "unmapped: " + detail, false);
        output[key] = mapping;
        return mapping;
    }

    private static bool TryParseLambda(string memberName, out string anchor)
    {
        anchor = string.Empty;
        if (!memberName.StartsWith('<'))
            return false;
        var end = memberName.IndexOf(">b__", StringComparison.Ordinal);
        if (end < 0)
            return false;
        anchor = memberName[1..end];
        return anchor.Length > 0;
    }

    private static bool TryParseLocal(string memberName, out string anchor, out string local)
    {
        anchor = string.Empty;
        local = string.Empty;
        if (!memberName.StartsWith('<'))
            return false;
        var end = memberName.IndexOf(">g__", StringComparison.Ordinal);
        if (end < 0)
            return false;
        var pipe = memberName.IndexOf('|', end);
        if (pipe < 0)
            return false;
        anchor = memberName[1..end];
        local = memberName[(end + 4)..pipe];
        return anchor.Length > 0 && local.Length > 0;
    }

    private static bool TrailingTypeAnchor(string containingType, out string anchor)
    {
        anchor = string.Empty;
        var plus = containingType.LastIndexOf('+');
        var segment = plus < 0 ? containingType : containingType[(plus + 1)..];
        if (!segment.StartsWith('<'))
            return false;
        var end = segment.IndexOf(">d__", StringComparison.Ordinal);
        if (end < 0)
            return false;
        anchor = segment[1..end];
        return anchor.Length > 0;
    }

    private static string UserTypeOf(string containingType)
    {
        var segments = containingType.Split('+');
        var end = segments.Length;
        while (end > 0 && IsGeneratedSegment(segments[end - 1]))
            end--;
        return end == 0 ? string.Empty : string.Join('+', segments[..end]);
    }

    private static bool IsGeneratedTypeName(string fullName)
    {
        foreach (var segment in fullName.Split('+'))
        {
            if (IsGeneratedSegment(segment))
                return true;
        }

        return false;
    }

    private static bool IsGeneratedSegment(string segment)
    {
        var dot = segment.LastIndexOf('.');
        var simple = dot < 0 ? segment : segment[(dot + 1)..];
        return simple.StartsWith('<');
    }

    private static string SimpleMemberName(string memberPart)
    {
        var paren = memberPart.IndexOf('(');
        var head = paren < 0 ? memberPart : memberPart[..paren];
        var space = head.LastIndexOf(' ');
        return space < 0 ? head : head[(space + 1)..];
    }

    private static string StripArity(string simpleName)
    {
        var tick = simpleName.LastIndexOf('`');
        if (tick >= 0 && int.TryParse(simpleName[(tick + 1)..], out _))
            return simpleName[..tick];
        return simpleName;
    }
}
