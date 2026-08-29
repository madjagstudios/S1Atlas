using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Query;

internal static class RuntimeVerificationClassifier
{
    private static readonly IReadOnlyDictionary<RuntimeVerificationSignal, string[]> SignalTokens =
        new Dictionary<RuntimeVerificationSignal, string[]>
        {
            [RuntimeVerificationSignal.Physics] = ["Physics", "Rigidbody", "Rigidbody2D", "Collider", "Collider2D"],
            [RuntimeVerificationSignal.NavMesh] = ["NavMesh", "NavMeshAgent", "OffMeshLink", "NavMeshPath"],
            [RuntimeVerificationSignal.TriggerState] =
                ["OnTrigger", "OnCollision", "isTrigger", "OverlapSphere", "OverlapBox", "OverlapCapsule", "ComputePenetration"]
        };

    public static RuntimeVerificationHint? Classify(string selectedSpan, string canonicalSignature)
    {
        ArgumentNullException.ThrowIfNull(selectedSpan);
        ArgumentNullException.ThrowIfNull(canonicalSignature);

        var tokens = Tokenize(selectedSpan + "\n" + canonicalSignature);
        var signals = Enum.GetValues<RuntimeVerificationSignal>()
            .Where(signal => SignalTokens[signal].Any(tokens.Contains))
            .ToArray();
        if (signals.Length == 0) return null;

        var signalNames = string.Join(", ", signals.Select(signal => signal.ToString()));
        return new RuntimeVerificationHint(
            signals,
            $"Static guidance only: the selected source suggests {signalNames} runtime behavior; verify it in-game.");
    }

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var start = -1;
        for (var index = 0; index <= text.Length; index++)
        {
            var isIdentifierCharacter = index < text.Length &&
                (char.IsLetterOrDigit(text[index]) || text[index] == '_');
            if (isIdentifierCharacter)
            {
                if (start < 0) start = index;
                continue;
            }

            if (start >= 0)
            {
                tokens.Add(text[start..index]);
                start = -1;
            }
        }

        return tokens;
    }
}
