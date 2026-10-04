using System.Text.RegularExpressions;
using S1Atlas.Core;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Decompilation;
using S1Atlas.Indexing.Relationships;
using S1Atlas.Indexing.Workflow;

namespace S1Atlas.Indexing.ModChecking;

public sealed class ModCheckService(IIndexRepository indexes, IModCheckRepository comparisons, IManagedDecompiler decompiler)
{
    private static readonly string[] StatusOrder = ["removed", "signature_changed", "unresolved", "moved", "unchanged", "resolved"];
    private static readonly Regex TypeToken = new(@"[A-Za-z_][\w`]*(?:[.+][A-Za-z_][\w`]*)*", RegexOptions.Compiled);
    private static readonly string[] ExternalRoots =
        ["System", "Microsoft", "UnityEngine", "UnityEditor", "Unity", "MelonLoader", "HarmonyLib", "Il2CppInterop", "S1API", "S1MAPI", "Il2CppSystem", "FishNet"];

    public async Task<ModCheckResult> CheckAsync(
        string modPath, string? fromBuildId, string toBuildId, string? fromIndexId, string toIndexId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ManagedDecompilation mod;
        try
        {
            // The decompiler uses File.OpenRead and never loads the mod for execution.
            mod = await decompiler.DecompileAsync(modPath, cancellationToken);
        }
        catch (Exception exception) when (exception is BadImageFormatException or InvalidDataException)
        {
            throw new InvalidDataException("The mod file is not a valid .NET managed assembly.", exception);
        }

        var game = await new ReferenceGameSymbolLoader(indexes).LoadAsync(fromIndexId ?? toIndexId, cancellationToken);
        var gameSymbols = game.Symbols.Where(symbol => !IsExternal(DeclaringType(symbol.QualifiedName))).ToArray();
        var callableSurface = await indexes.GetCompletedCallableSurfaceAsync(fromIndexId ?? toIndexId, cancellationToken);
        var interop = new ModCheckInteropResolver(gameSymbols, callableSurface);
        const string modId = "check-mod";
        var modSymbols = ReferenceModIndexWorkflow.BuildSymbols(modId, mod, "check-mod");
        var lookup = ReferenceModIndexWorkflow.BuildRelationshipLookup(gameSymbols, modSymbols);
        var edges = new ReferenceRelationshipResolver().Resolve([new(modId, mod)], lookup);
        var gameById = gameSymbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        var modById = modSymbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        var gameTypes = gameSymbols.Where(symbol => symbol.Kind == "Type").ToDictionary(symbol => symbol.Signature, StringComparer.Ordinal);
        var gameRoots = gameTypes.Keys.Select(type => type.Split('.')[0]).ToHashSet(StringComparer.Ordinal);
        var ownTypes = mod.Types.Select(type => InteropTypeNames.Normalize(type.FullName)).ToHashSet(StringComparer.Ordinal);
        var pending = new Dictionary<string, PendingDependency>(StringComparer.Ordinal);
        var external = new HashSet<string>(StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            var patch = edge.Kind == "Patches";
            var (reason, text) = SplitReason(edge.TargetText ?? "");
            if (text.Length == 0) continue;
            var type = DeclaringType(text);
            if (IsExternal(type)) { external.Add(text); continue; }
            gameById.TryGetValue(edge.TargetSymbolId ?? "", out var target);
            if (!patch) target = interop.ResolveReference(text, ReferenceKind(edge.Kind)) ?? target;
            if (edge.TargetSymbolId is not null && target is null) continue;
            var unknownPatchTarget = patch && reason is not null && text == modById.GetValueOrDefault(edge.SourceSymbolId)?.Signature;
            if (target is null && !IsGameType(type) && !unknownPatchTarget)
            {
                external.Add(text);
                continue;
            }
            var source = new ModDependencySource(patch ? "harmony_patch" : "direct_reference",
                patch ? edge.GeneratedDetail : null, modById.GetValueOrDefault(edge.SourceSymbolId)?.Signature ?? edge.SourceSymbolId,
                patch && edge.Evidence == "RecoveredIL" ? "DERIVED" : "FACT");
            Add(target, text, target?.Kind ?? (patch ? "Method" : ReferenceKind(edge.Kind)),
                reason ?? (target is null ? ResolutionReason(text, gameSymbols) : null), source);
            AddType(type, source);
        }

        foreach (var type in mod.Types)
        {
            foreach (var referencedType in new[] { type.BaseType }.Concat(type.Interfaces))
                if (referencedType is not null) AddTypeTokens(referencedType, new("direct_reference", null, type.FullName, "FACT"));
            foreach (var member in type.Members)
            {
                var memberName = ManagedMemberIdentity.Render(type.FullName, member);
                var direct = new ModDependencySource("direct_reference", null, memberName, "FACT");
                foreach (var signatureType in member.ParameterTypesOrEmpty.Concat(new[] { member.ReturnType, member.ValueType }).OfType<string>())
                    AddTypeTokens(signatureType, direct);
                foreach (var reference in member.References) AddTypeTokens(reference.Target, direct);
                foreach (var referencedType in member.TypeReferencesOrEmpty) AddTypeTokens(referencedType, direct);
                foreach (var reflection in member.ReflectionsOrEmpty)
                {
                    var normalizedType = InteropTypeNames.Normalize(reflection.TargetType);
                    var name = reflection.Kind switch
                    {
                        ManagedReflectionKind.PropertyGetter => "get_" + reflection.MemberName,
                        ManagedReflectionKind.PropertySetter => "set_" + reflection.MemberName,
                        _ => reflection.MemberName
                    };
                    var kind = reflection.Kind switch
                    {
                        ManagedReflectionKind.Field => "Field",
                        ManagedReflectionKind.Property => "Property",
                        ManagedReflectionKind.Constructor => "Constructor",
                        _ => "Method"
                    };
                    var text = normalizedType + "::" + name;
                    if (reflection.ArgumentTypes is not null)
                        text += "(" + string.Join(",", reflection.ArgumentTypes.Select(InteropTypeNames.NormalizeSignature)) + ")";
                    if (IsExternal(normalizedType)) { external.Add(text); continue; }
                    if (!IsGameType(normalizedType))
                    {
                        if (!ownTypes.Contains(normalizedType)) external.Add(text);
                        continue;
                    }
                    var arguments = interop.ReflectionArguments(normalizedType, name, reflection.ArgumentTypes,
                        reflection.Kind is ManagedReflectionKind.PropertyGetter or ManagedReflectionKind.PropertySetter);
                    var candidates = gameSymbols.Where(symbol => symbol.Kind == kind && DeclaringType(symbol.QualifiedName) == normalizedType &&
                        SymbolNames.SimpleName(symbol.QualifiedName) == name && MatchesArguments(symbol.Signature, arguments)).ToArray();
                    var target = interop.ResolveReflection(normalizedType, name, kind, arguments)
                        ?? (candidates.Length == 1 ? candidates[0] : null);
                    var source = new ModDependencySource("reflection", null, memberName, "DERIVED");
                    Add(target, text, target?.Kind ?? kind, target is null ? candidates.Length > 1 ? HarmonyPatchReasons.AmbiguousOverload : ResolutionReason(text, gameSymbols) : null, source);
                    AddType(normalizedType, source);
                }
            }
        }

        var rows = new List<ModDependency>();
        foreach (var dependency in pending.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sources = dependency.Sources.Distinct().OrderBy(source => source.Kind, StringComparer.Ordinal)
                .ThenBy(source => source.PatchKind, StringComparer.Ordinal).ThenBy(source => source.Member, StringComparer.Ordinal).ToArray();
            var evidence = sources.All(source => source.Evidence == "DERIVED") ? "DERIVED" : "FACT";
            if (dependency.Symbol is null)
            {
                rows.Add(new(null, dependency.Text, dependency.Kind, "unresolved", null, null, null, sources, [], dependency.Reason, evidence));
                continue;
            }
            var before = dependency.Symbol;
            var after = await comparisons.GetModCheckSymbolAsync(toIndexId, before.CanonicalKey, cancellationToken);
            if (fromIndexId is null || after is not null)
            {
                bool? bodyChanged = null;
                if (fromIndexId is not null && after?.BodyFingerprint is not null)
                {
                    var old = await comparisons.GetModCheckSymbolAsync(fromIndexId, before.CanonicalKey, cancellationToken);
                    if (old?.BodyFingerprint is not null) bodyChanged = old.BodyFingerprint != after.BodyFingerprint;
                }
                rows.Add(new(before.CanonicalKey, before.QualifiedName, before.Kind, fromIndexId is null ? "resolved" : "unchanged",
                    fromIndexId is null ? null : before.Signature, after?.Symbol.Signature ?? before.Signature, bodyChanged, sources, [], null, evidence));
                continue;
            }

            var declaring = DeclaringType(before.QualifiedName);
            var name = SymbolNames.SimpleName(before.QualifiedName);
            var sameName = await comparisons.FindModCheckMembersAsync(toIndexId, declaring, name, before.Kind, 2, cancellationToken);
            var oldOverloads = gameSymbols.Count(symbol => symbol.Kind == before.Kind && DeclaringType(symbol.QualifiedName) == declaring && SymbolNames.SimpleName(symbol.QualifiedName) == name);
            if (oldOverloads == 1 && sameName.Count == 1)
            {
                rows.Add(new(before.CanonicalKey, before.QualifiedName, before.Kind, "signature_changed", before.Signature, sameName[0].Symbol.Signature,
                    null, sources, [Replacement(sameName[0])], null, evidence));
                continue;
            }
            var original = await comparisons.GetModCheckSymbolAsync(fromIndexId, before.CanonicalKey, cancellationToken);
            var moves = original?.BodyFingerprint is null ? [] : await comparisons.FindModCheckMovesAsync(
                toIndexId, declaring, name, MemberTail(before.QualifiedName), before.Kind, original.BodyFingerprint, cancellationToken);
            if (moves.Count == 1)
            {
                rows.Add(new(before.CanonicalKey, before.QualifiedName, before.Kind, "moved", before.Signature, moves[0].Symbol.Signature,
                    null, sources, [Replacement(moves[0])], null, "DERIVED"));
                continue;
            }
            var possible = await comparisons.FindModCheckMembersAsync(toIndexId, null, name, before.Kind, 5, cancellationToken);
            rows.Add(new(before.CanonicalKey, before.QualifiedName, before.Kind, "removed", before.Signature, null,
                null, sources, possible.Select(Replacement).ToArray(), null, evidence));
        }

        var sorted = rows.OrderBy(row => Array.IndexOf(StatusOrder, row.Status)).ThenBy(row => row.Name, StringComparer.Ordinal).ToArray();
        var counts = StatusOrder.ToDictionary(status => status, status => sorted.Count(row => row.Status == status), StringComparer.Ordinal);
        var patches = StatusOrder.ToDictionary(status => status, status => sorted.Count(row => row.Status == status && row.Kind != "Type" && row.Sources.Any(source => source.Kind == "harmony_patch")), StringComparer.Ordinal);
        return new(fromBuildId, toBuildId, fromIndexId, toIndexId, fromIndexId is null,
            new(counts, patches, external.Count, sorted.Count(IsBreaking), sorted.Count(row => IsBreaking(row) && row.Kind != "Type" && row.Sources.Any(source => source.Kind == "harmony_patch"))), sorted);

        bool IsGameType(string type) => gameTypes.ContainsKey(type) || gameRoots.Contains(type.Split('.')[0]);
        void Add(IndexSymbolRecord? symbol, string text, string kind, string? reason, ModDependencySource source)
        {
            var key = symbol?.CanonicalKey ?? kind + ":" + text;
            if (!pending.TryGetValue(key, out var dependency))
                pending[key] = dependency = new(symbol, text, kind, reason);
            dependency.Sources.Add(source);
        }
        void AddType(string type, ModDependencySource source)
        {
            var normalized = InteropTypeNames.Normalize(type);
            if (IsExternal(normalized) || ownTypes.Contains(normalized)) return;
            if (gameTypes.TryGetValue(normalized, out var symbol)) Add(symbol, normalized, "Type", null, source);
            else if (IsGameType(normalized)) Add(null, normalized, "Type", HarmonyPatchReasons.TargetTypeNotFound, source);
        }
        void AddTypeTokens(string signature, ModDependencySource source)
        {
            foreach (Match match in TypeToken.Matches(signature))
            {
                var type = InteropTypeNames.Normalize(match.Value);
                if (ownTypes.Contains(type)) continue;
                if (IsExternal(type)) { external.Add(type); continue; }
                if (IsGameType(type)) AddType(type, source);
                else if (type.Contains('.', StringComparison.Ordinal)) external.Add(type);
            }
        }
    }

    private static bool IsBreaking(ModDependency row) => row.Status is "removed" or "signature_changed";
    private static ModReplacement Replacement(ModCheckSymbol candidate) => new(candidate.Symbol.CanonicalKey, candidate.Symbol.QualifiedName, candidate.Symbol.Signature, "DERIVED");
    private static string DeclaringType(string signature) => InteropTypeNames.Normalize(signature.Split("::", 2, StringSplitOptions.None)[0]);
    private static string MemberTail(string signature) => signature.Contains("::", StringComparison.Ordinal) ? signature.Split("::", 2, StringSplitOptions.None)[1] : signature;
    private static bool IsExternal(string type) => ExternalRoots.Any(root => type == root || type.StartsWith(root + ".", StringComparison.Ordinal));
    private static string ReferenceKind(string kind) => kind is "ReadsField" or "WritesField" or "TakesFieldAddress" ? "Field" : kind == "Constructs" ? "Constructor" : "Method";
    private static (string? Reason, string Text) SplitReason(string text)
    {
        if (!text.StartsWith(HarmonyPatchReasons.Marker, StringComparison.Ordinal)) return (null, text);
        var rest = text[HarmonyPatchReasons.Marker.Length..];
        var separator = rest.IndexOf(':');
        return separator < 0 ? (rest, text) : (rest[..separator], rest[(separator + 1)..]);
    }
    private static string ResolutionReason(string text, IReadOnlyList<IndexSymbolRecord> game)
    {
        var type = DeclaringType(text);
        if (!game.Any(symbol => symbol.Kind == "Type" && symbol.Signature == type)) return HarmonyPatchReasons.TargetTypeNotFound;
        return game.Any(symbol => DeclaringType(symbol.QualifiedName) == type && SymbolNames.SimpleName(symbol.QualifiedName) == SymbolNames.SimpleName(text))
            ? HarmonyPatchReasons.NoMatchingOverload : HarmonyPatchReasons.TargetMemberNotFound;
    }
    private static bool MatchesArguments(string signature, IReadOnlyList<string>? arguments)
    {
        if (arguments is null) return true;
        var tail = MemberTail(signature);
        var open = tail.IndexOf('(');
        var close = tail.LastIndexOf(')');
        if (open < 0 || close < open) return false;
        // Preserve nested generic commas: compare the canonical parameter text as a whole.
        return InteropTypeNames.NormalizeSignature(tail[(open + 1)..close]) ==
            string.Join(",", arguments.Select(argument => InteropTypeNames.NormalizeSignature(CanonicalSignatureRenderer.RenderType(argument))));
    }
    private sealed record PendingDependency(IndexSymbolRecord? Symbol, string Text, string Kind, string? Reason)
    {
        public List<ModDependencySource> Sources { get; } = [];
    }
}
