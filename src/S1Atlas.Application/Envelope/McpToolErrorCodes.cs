using System.Text;

namespace S1Atlas.Application.Envelope;

// The unified MCP error vocabulary. Internal PascalCase codes keep their
// detail for direct callers and the CLI; the MCP wire maps them to ten
// snake_case codes at serialization so clients switch on a closed set.
public static class McpToolErrorCodes
{
    public const string InvalidArguments = "invalid_arguments";
    public const string SymbolNotFound = "symbol_not_found";
    public const string NoCompletedIndex = "no_completed_index";
    public const string SourceUnavailable = "source_unavailable";
    public const string SourceIntegrityFailure = "source_integrity_failure";
    public const string SnapshotNotFound = "snapshot_not_found";
    public const string NoCurrentBuild = "no_current_build";
    public const string AtlasUnavailable = "atlas_unavailable";
    public const string UnexpectedToolFailure = "unexpected_tool_failure";
    public const string InvalidCursor = "invalid_cursor";

    public static readonly IReadOnlyList<string> UnifiedCodes =
    [
        InvalidArguments,
        SymbolNotFound,
        NoCompletedIndex,
        SourceUnavailable,
        SourceIntegrityFailure,
        SnapshotNotFound,
        NoCurrentBuild,
        AtlasUnavailable,
        UnexpectedToolFailure,
        InvalidCursor
    ];

    private static readonly IReadOnlyDictionary<string, string> WireCodes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["InvalidArguments"] = InvalidArguments,
            ["InvalidLimit"] = InvalidArguments,
            ["InvalidDepth"] = InvalidArguments,
            ["InvalidOffset"] = InvalidArguments,
            ["InvalidContext"] = InvalidArguments,
            ["InvalidRelatedLimit"] = InvalidArguments,
            ["InvalidRelationshipLimit"] = InvalidArguments,
            ["InvalidOwnerLimit"] = InvalidArguments,
            ["InvalidFieldFilter"] = InvalidArguments,
            ["InvalidOptionCombination"] = InvalidArguments,
            ["InvalidRelationshipKind"] = InvalidArguments,
            ["InvalidKind"] = InvalidArguments,
            ["InvalidScope"] = InvalidArguments,
            ["InvalidCollection"] = InvalidArguments,
            ["CollectionRequired"] = InvalidArguments,
            ["InvalidChannel"] = InvalidArguments,
            ["InvalidQuestion"] = InvalidArguments,
            ["InvalidSelector"] = InvalidArguments,
            ["InvalidNativeTraversalBudget"] = InvalidArguments,
            ["InvalidRuntimeProofRequest"] = InvalidArguments,
            ["InvalidQuery"] = InvalidArguments,
            ["InvalidPage"] = InvalidArguments,
            ["SameBuild"] = InvalidArguments,
            ["SameIndex"] = InvalidArguments,
            ["UnsupportedCodebase"] = InvalidArguments,
            ["UnsupportedContainer"] = InvalidArguments,
            ["UnknownEndpoint"] = InvalidArguments,
            ["SymbolKindMismatch"] = InvalidArguments,
            ["AmbiguousBuildPrefix"] = InvalidArguments,
            ["ReferenceCollectionBuildMismatch"] = InvalidArguments,
            ["ReferenceCollectionBaseIndexMismatch"] = InvalidArguments,
            ["AmbiguousScene"] = InvalidArguments,
            ["AmbiguousGameObject"] = InvalidArguments,
            ["AmbiguousComponent"] = InvalidArguments,
            ["AmbiguousScriptableAsset"] = InvalidArguments,
            ["SymbolNotFound"] = SymbolNotFound,
            ["SceneNotFound"] = SymbolNotFound,
            ["GameObjectNotFound"] = SymbolNotFound,
            ["ComponentNotFound"] = SymbolNotFound,
            ["ScriptableAssetNotFound"] = SymbolNotFound,
            ["UnresolvedCodeSymbol"] = SymbolNotFound,
            ["UnresolvedSceneReference"] = SymbolNotFound,
            ["CallableSurfaceUnavailable"] = SymbolNotFound,
            ["NoCompletedIndex"] = NoCompletedIndex,
            ["NoCompletedSceneIndex"] = NoCompletedIndex,
            ["NoCompletedScheduleOneCodeIndex"] = NoCompletedIndex,
            ["StaleApiIndex"] = NoCompletedIndex,
            ["ApiIndexUnavailable"] = NoCompletedIndex,
            ["NoVerifiedSceneContainers"] = NoCompletedIndex,
            ["SceneTypeTreeUnavailable"] = NoCompletedIndex,
            ["SceneIndexInProgress"] = NoCompletedIndex,
            ["NoRecoverableSceneObjects"] = NoCompletedIndex,
            ["PartialRecovery"] = NoCompletedIndex,
            ["NoPreferredVerifiedExtraction"] = NoCompletedIndex,
            ["NoReplayVerifiedExtractionInput"] = NoCompletedIndex,
            ["SourceUnavailable"] = SourceUnavailable,
            ["SourceIntegrityFailure"] = SourceIntegrityFailure,
            ["SceneInputIntegrityFailure"] = SourceIntegrityFailure,
            ["ExtractionIntegrityFailure"] = SourceIntegrityFailure,
            ["SceneSnapshotNotFound"] = SnapshotNotFound,
            ["BuildNotFound"] = SnapshotNotFound,
            ["NoMatchingEnvironmentSnapshot"] = SnapshotNotFound,
            ["CrossBuildCodeIndex"] = SnapshotNotFound,
            ["PreferredExtractionChanged"] = SnapshotNotFound,
            ["ReplayVerifiedInputChanged"] = SnapshotNotFound,
            ["CodeIndexChanged"] = SnapshotNotFound,
            ["NoReplayVerifiedInputChanged"] = SnapshotNotFound,
            ["IndexBuildMismatch"] = SnapshotNotFound,
            ["NoCurrentBuild"] = NoCurrentBuild,
            ["AtlasUnavailable"] = AtlasUnavailable,
            ["UnexpectedToolFailure"] = UnexpectedToolFailure,
            ["IncompleteSeamResult"] = UnexpectedToolFailure,
            ["InvalidCursor"] = InvalidCursor
        };

    public static string MapToWireCode(string code)
    {
        if (WireCodes.TryGetValue(code, out var mapped))
            return mapped;

        return ToSnakeCase(code);
    }

    private static string ToSnakeCase(string code)
    {
        if (string.IsNullOrEmpty(code))
            return code;

        var builder = new StringBuilder(code.Length + 8);
        for (var i = 0; i < code.Length; i++)
        {
            var current = code[i];
            if (char.IsUpper(current))
            {
                if (i > 0 && builder.Length > 0 && builder[^1] != '_')
                    builder.Append('_');
                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }
}
