using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Decompilation;

public sealed record CallableSurfaceMatch(
    string GameTypeName,
    ManagedMemberFacts GameMember,
    ManagedMemberFacts? InteropMember,
    CallableSurfaceKind Kind,
    CallableSurfaceStatus Status,
    bool RequiresReflection,
    string? InteropSignature,
    string Evidence);

public sealed class InteropCallableSurfaceMatcher
{
    public IReadOnlyList<CallableSurfaceMatch> Match(
        ManagedDecompilation gameAssembly,
        ManagedDecompilation? interopAssembly)
    {
        ArgumentNullException.ThrowIfNull(gameAssembly);

        var interopMembers = interopAssembly is null
            ? new Dictionary<MemberLookupKey, List<ManagedMemberFacts>>()
            : BuildInteropLookup(interopAssembly);
        var matches = new List<CallableSurfaceMatch>();

        foreach (var gameType in gameAssembly.Types)
        {
            foreach (var gameMember in gameType.Members.Where(IsCallableGameMember))
            {
                if (gameMember.IsPublic)
                {
                    matches.Add(new CallableSurfaceMatch(
                        gameType.FullName,
                        gameMember,
                        null,
                        CallableSurfaceKind.DirectGameMember,
                        CallableSurfaceStatus.Resolved,
                        false,
                        null,
                        "public game member is directly callable without reflection"));
                    continue;
                }

                var candidates = FindCandidates(gameType, gameMember, interopMembers);
                if (candidates.Count == 1)
                {
                    var interopMember = candidates[0];
                    var kind = GetWrapperKind(interopMember.Kind, interopMember.IsPublic);
                    matches.Add(new CallableSurfaceMatch(
                        gameType.FullName,
                        gameMember,
                        interopMember,
                        kind,
                        CallableSurfaceStatus.Resolved,
                        !interopMember.IsPublic,
                        interopMember.Signature,
                        GetInteropEvidence(interopMember)));
                }
                else
                {
                    matches.Add(new CallableSurfaceMatch(
                        gameType.FullName,
                        gameMember,
                        null,
                        GetWrapperKind(gameMember.Kind, false),
                        candidates.Count == 0
                            ? CallableSurfaceStatus.Unavailable
                            : CallableSurfaceStatus.Ambiguous,
                        false,
                        null,
                        candidates.Count == 0
                            ? "no usable interop wrapper or accessor was found"
                            : "multiple interop wrappers or accessors matched the game member"));
                }
            }
        }

        return matches;
    }

    private static Dictionary<MemberLookupKey, List<ManagedMemberFacts>> BuildInteropLookup(
        ManagedDecompilation assembly)
    {
        var lookup = new Dictionary<MemberLookupKey, List<ManagedMemberFacts>>();
        foreach (var type in assembly.Types)
        {
            foreach (var member in type.Members.Where(IsCallableInteropMember))
            {
                var key = new MemberLookupKey(InteropMemberProjection.NormalizeType(type.FullName), member.Kind, member.Name, Arity(member));
                if (!lookup.TryGetValue(key, out var candidates))
                {
                    candidates = [];
                    lookup.Add(key, candidates);
                }

                candidates.Add(member);
            }
        }

        return lookup;
    }

    private static List<ManagedMemberFacts> FindCandidates(
        ManagedTypeFacts gameType,
        ManagedMemberFacts gameMember,
        IReadOnlyDictionary<MemberLookupKey, List<ManagedMemberFacts>> interopMembers)
    {
        var typeName = InteropMemberProjection.NormalizeType(gameType.FullName);
        var hasBackingProperty = InteropMemberProjection.IsBackingField(gameMember) && gameType.Members.Any(member =>
            member.Kind == ManagedMemberKind.Property && member.Name == InteropMemberProjection.GetBackingPropertyName(gameMember.Name));
        var candidates = new List<ManagedMemberFacts>();
        foreach (var candidateKind in InteropMemberProjection.CandidateKinds(gameMember))
            foreach (var name in InteropMemberProjection.CandidateNames(gameMember, hasBackingProperty))
                if (interopMembers.TryGetValue(new MemberLookupKey(typeName, candidateKind, name, Arity(gameMember)), out var matches))
                    candidates.AddRange(matches);

        var exact = candidates.Where(candidate => string.Equals(candidate.Signature, gameMember.Signature, StringComparison.Ordinal))
            .Distinct().ToList();
        return exact.Count > 0
            ? exact
            : candidates.Where(candidate => InteropMemberProjection.IsSignatureCompatible(gameMember, candidate)).Distinct().ToList();
    }

    private static CallableSurfaceKind GetWrapperKind(
        ManagedMemberKind interopKind,
        bool interopIsPublic)
    {
        if (!interopIsPublic)
            return CallableSurfaceKind.NonPublicWrapper;

        return interopKind switch
        {
            ManagedMemberKind.Method => CallableSurfaceKind.PublicMethodWrapper,
            ManagedMemberKind.Field => CallableSurfaceKind.PublicFieldAccessor,
            ManagedMemberKind.Property => CallableSurfaceKind.PublicPropertyAccessor,
            _ => CallableSurfaceKind.NonPublicWrapper
        };
    }

    private static string GetInteropEvidence(ManagedMemberFacts member) =>
        member.BodyFacts?.MatchesInteropWrapperPattern == true
            ? "public interop wrapper forwards through il2cpp_runtime_invoke; body is not game behavioral evidence"
            : "matched interop wrapper or accessor";

    private static bool IsCallableGameMember(ManagedMemberFacts member) =>
        member.Kind is ManagedMemberKind.Method or ManagedMemberKind.Field or ManagedMemberKind.Property;

    private static bool IsCallableInteropMember(ManagedMemberFacts member) =>
        member.Kind is ManagedMemberKind.Method or ManagedMemberKind.Field or ManagedMemberKind.Property;

    private static int Arity(ManagedMemberFacts member) => member.ParameterTypesOrEmpty.Count;

    private readonly record struct MemberLookupKey(
        string TypeName,
        ManagedMemberKind Kind,
        string Name,
        int Arity);
}
