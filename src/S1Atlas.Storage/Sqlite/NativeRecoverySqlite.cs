using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using S1Atlas.Core.Storage;

namespace S1Atlas.Storage.Sqlite;

internal static class NativeRecoverySqlite
{
    private const int MaximumSummaryLength = 512;

    public static NativeRecoveryRequest CanonicalizeRequest(NativeRecoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BuildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IndexId);
        RequireSha256(request.GameAssemblySha256, nameof(request.GameAssemblySha256));
        ArgumentNullException.ThrowIfNull(request.SymbolIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxTraversalEdges, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.MaxTraversalEdges, 500);
        if (request.SymbolIds.Count == 0)
            throw new ArgumentException("At least one native symbol ID must be selected.", nameof(request));

        var symbolIds = request.SymbolIds
            .Select(symbolId => RequireText(symbolId, nameof(request.SymbolIds)))
            .OrderBy(symbolId => symbolId, StringComparer.Ordinal)
            .ToArray();
        if (symbolIds.Distinct(StringComparer.Ordinal).Count() != symbolIds.Length)
            throw new ArgumentException("Native symbol IDs must be unique.", nameof(request));

        return request with { SymbolIds = symbolIds };
    }

    public static NativeRecoveryRecord NormalizeRecord(NativeRecoveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = record with
        {
            Request = CanonicalizeRequest(record.Request),
            ToolName = NormalizeSummary(record.ToolName, nameof(record.ToolName)),
            ToolVersion = NormalizeSummary(record.ToolVersion, nameof(record.ToolVersion)),
            MappingEvidence = NormalizeTextCollection(record.MappingEvidence, nameof(record.MappingEvidence)),
            Edges = record.Edges
                .Select(NormalizeEdge)
                .OrderBy(edge => edge.SourceMethodPointer, StringComparer.Ordinal)
                .ThenBy(edge => edge.TargetMethodPointer, StringComparer.Ordinal)
                .ThenBy(edge => edge.TargetText, StringComparer.Ordinal)
                .ThenBy(edge => edge.Kind, StringComparer.Ordinal)
                .ThenBy(edge => edge.Evidence, StringComparer.Ordinal)
                .ThenBy(edge => edge.IsComplete)
                .ThenBy(edge => edge.EdgeId, StringComparer.Ordinal)
                .ToArray(),
            FieldAccesses = NormalizeTextCollection(record.FieldAccesses, nameof(record.FieldAccesses)),
            FailureMessage = record.FailureMessage is null
                ? null
                : NormalizeSummary(record.FailureMessage, nameof(record.FailureMessage))
        };
        normalized = normalized with
        {
            OutputSha256 = NativeRecoveryIntegrity.ComputeOutputSha256(
                normalized.Status,
                normalized.MappingEvidence,
                normalized.Edges,
                normalized.FieldAccesses,
                normalized.IsComplete,
                normalized.FailureMessage)
        };
        normalized = normalized with
        {
            RecoveryId = NativeRecoveryIntegrity.ComputeRecoveryId(
                normalized.Request,
                normalized.ToolName,
                normalized.ToolVersion,
                normalized.ToolSha256,
                normalized.OutputSha256)
        };
        ValidateRecord(normalized);
        return normalized;
    }

    public static void ValidateRecord(NativeRecoveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        RequireSha256(record.RecoveryId, nameof(record.RecoveryId));
        var canonicalRequest = CanonicalizeRequest(record.Request);
        if (!record.Request.SymbolIds.SequenceEqual(canonicalRequest.SymbolIds, StringComparer.Ordinal))
            throw new InvalidDataException("Native recovery symbol IDs must be in canonical ordinal order.");

        ValidateSummary(record.ToolName, nameof(record.ToolName));
        ValidateSummary(record.ToolVersion, nameof(record.ToolVersion));
        RequireSha256(record.ToolSha256, nameof(record.ToolSha256));
        RequireSha256(record.OutputSha256, nameof(record.OutputSha256));
        ArgumentNullException.ThrowIfNull(record.MappingEvidence);
        ArgumentNullException.ThrowIfNull(record.Edges);
        ArgumentNullException.ThrowIfNull(record.FieldAccesses);
        if (record.Edges.Count > record.Request.MaxTraversalEdges)
            throw new InvalidDataException("Native evidence edges exceed the recorded traversal budget.");

        if (!Enum.IsDefined(record.Status))
            throw new InvalidDataException("Native recovery status is not recognized.");

        var canonicalMapping = record.MappingEvidence
            .OrderBy(value => value, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (!record.MappingEvidence.SequenceEqual(canonicalMapping, StringComparer.Ordinal))
            throw new InvalidDataException("Native mapping evidence must be canonical and unique.");

        var canonicalFields = record.FieldAccesses
            .OrderBy(value => value, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (!record.FieldAccesses.SequenceEqual(canonicalFields, StringComparer.Ordinal))
            throw new InvalidDataException("Native field evidence must be canonical and unique.");

        if (record.Status == NativeRecoveryStatus.Recovered)
        {
            if (record.MappingEvidence.Count == 0 ||
                (record.Edges.Count == 0 && record.FieldAccesses.Count == 0))
            {
                throw new InvalidDataException(
                    "Recovered native evidence requires mapping and edge or field evidence.");
            }

            if (record.FailureMessage is not null)
                throw new InvalidDataException("Recovered native evidence cannot carry a failure message.");
        }
        else
        {
            if (record.IsComplete || record.Edges.Count != 0 || record.FieldAccesses.Count != 0)
                throw new InvalidDataException(
                    "Non-recovered native evidence must be incomplete and contain no edges or fields.");

            if (record.Status is not (NativeRecoveryStatus.NoBody or NativeRecoveryStatus.AmbiguousMapping) &&
                record.MappingEvidence.Count != 0)
            {
                throw new InvalidDataException(
                    "Only no-body and ambiguous-mapping results may retain mapping evidence.");
            }
        }

        var canonicalEdges = record.Edges
            .OrderBy(edge => edge.SourceMethodPointer, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetMethodPointer, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetText, StringComparer.Ordinal)
            .ThenBy(edge => edge.Kind, StringComparer.Ordinal)
            .ThenBy(edge => edge.Evidence, StringComparer.Ordinal)
            .ThenBy(edge => edge.IsComplete)
            .ThenBy(edge => edge.EdgeId, StringComparer.Ordinal)
            .ToArray();
        if (!record.Edges.SequenceEqual(canonicalEdges))
            throw new InvalidDataException("Native evidence edges must be in canonical order.");

        foreach (var mapping in record.MappingEvidence)
            ValidateSummary(mapping, nameof(record.MappingEvidence));
        foreach (var field in record.FieldAccesses)
            ValidateSummary(field, nameof(record.FieldAccesses));
        foreach (var edge in record.Edges)
        {
            ArgumentNullException.ThrowIfNull(edge);
            RequireSha256(edge.EdgeId, nameof(edge.EdgeId));
            ValidateSummary(edge.SourceMethodPointer, nameof(edge.SourceMethodPointer));
            ValidateOptionalSummary(edge.TargetMethodPointer, nameof(edge.TargetMethodPointer));
            ValidateOptionalSummary(edge.TargetText, nameof(edge.TargetText));
            ValidateSummary(edge.Kind, nameof(edge.Kind));
            ValidateSummary(edge.Evidence, nameof(edge.Evidence));
            if (edge.Kind == "DirectCall" && string.IsNullOrWhiteSpace(edge.TargetMethodPointer))
                throw new InvalidDataException("DirectCall native evidence requires a target method pointer.");
            if (edge.Kind == "UNKNOWN" && (edge.IsComplete || edge.TargetMethodPointer is not null))
                throw new InvalidDataException(
                    "UNKNOWN native evidence must be incomplete and have no target method pointer.");
            if (edge.Kind is not ("DirectCall" or "UNKNOWN"))
                throw new InvalidDataException("Native evidence edge kind is not recognized.");
        }

        ValidateOptionalSummary(record.FailureMessage, nameof(record.FailureMessage));

        var expectedOutputSha256 = NativeRecoveryIntegrity.ComputeOutputSha256(
            record.Status,
            record.MappingEvidence,
            record.Edges,
            record.FieldAccesses,
            record.IsComplete,
            record.FailureMessage);
        if (!string.Equals(record.OutputSha256, expectedOutputSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Native recovery output hash does not match the materialized evidence.");

        var expectedRecoveryId = NativeRecoveryIntegrity.ComputeRecoveryId(
            record.Request,
            record.ToolName,
            record.ToolVersion,
            record.ToolSha256,
            record.OutputSha256);
        if (!string.Equals(record.RecoveryId, expectedRecoveryId, StringComparison.Ordinal))
            throw new InvalidDataException("Native recovery ID does not match the materialized provenance and evidence.");
    }

    private static IReadOnlyList<string> NormalizeTextCollection(
        IReadOnlyList<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        return values
            .Select(value => NormalizeSummary(value, parameterName))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static NativeEvidenceEdge NormalizeEdge(NativeEvidenceEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return edge with
        {
            EdgeId = NormalizeSha256(edge.EdgeId, nameof(edge.EdgeId)),
            SourceMethodPointer = NormalizeSummary(edge.SourceMethodPointer, nameof(edge.SourceMethodPointer)),
            TargetMethodPointer = edge.TargetMethodPointer is null
                ? null
                : NormalizeSummary(edge.TargetMethodPointer, nameof(edge.TargetMethodPointer)),
            TargetText = edge.TargetText is null
                ? null
                : NormalizeSummary(edge.TargetText, nameof(edge.TargetText)),
            Kind = NormalizeSummary(edge.Kind, nameof(edge.Kind)),
            Evidence = NormalizeSummary(edge.Evidence, nameof(edge.Evidence))
        };
    }

    public static string SerializeStrings(IReadOnlyList<string> values) =>
        JsonSerializer.Serialize(values);

    public static async Task<NativeRecoveryRecord?> GetByIdAsync(
        SqliteConnection connection,
        string recoveryId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryId);
        StoredRun? run;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = RunSelectSql + " WHERE recovery_id = $recoveryId;";
            command.Parameters.AddWithValue("$recoveryId", recoveryId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            run = await reader.ReadAsync(cancellationToken) ? ReadRun(reader) : null;
        }

        if (run is null)
            return null;

        var record = await MaterializeAsync(connection, run, cancellationToken);
        ValidateRecord(record);
        return record;
    }

    public static async Task<IReadOnlyList<NativeRecoveryRecord>> GetMatchingAsync(
        SqliteConnection connection,
        NativeRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var canonicalRequest = CanonicalizeRequest(request);
        var recoveryIds = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT recovery_id
                FROM native_recovery_runs
                WHERE build_id = $buildId
                  AND index_id = $indexId
                  AND game_assembly_sha256 = $gameAssemblySha256
                  AND symbol_ids_json = $symbolIdsJson
                  AND max_traversal_edges = $maxTraversalEdges
                ORDER BY created_at_utc DESC, recovery_id COLLATE BINARY ASC;
                """;
            command.Parameters.AddWithValue("$buildId", canonicalRequest.BuildId);
            command.Parameters.AddWithValue("$indexId", canonicalRequest.IndexId);
            command.Parameters.AddWithValue("$gameAssemblySha256", canonicalRequest.GameAssemblySha256);
            command.Parameters.AddWithValue("$symbolIdsJson", SerializeStrings(canonicalRequest.SymbolIds));
            command.Parameters.AddWithValue("$maxTraversalEdges", canonicalRequest.MaxTraversalEdges);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                recoveryIds.Add(reader.GetString(0));
        }

        var records = new List<NativeRecoveryRecord>(recoveryIds.Count);
        foreach (var recoveryId in recoveryIds)
        {
            records.Add(await GetByIdAsync(connection, recoveryId, cancellationToken)
                ?? throw new InvalidDataException($"Native recovery '{recoveryId}' disappeared while being read."));
        }

        return records;
    }

    private static async Task<NativeRecoveryRecord> MaterializeAsync(
        SqliteConnection connection,
        StoredRun run,
        CancellationToken cancellationToken)
    {
        var edges = new List<NativeEvidenceEdge>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT edge_id, source_method_pointer, target_method_pointer,
                       target_text, kind, evidence, is_complete
                FROM native_recovery_edges
                WHERE recovery_id = $recoveryId
                ORDER BY ordinal;
                """;
            command.Parameters.AddWithValue("$recoveryId", run.RecoveryId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                edges.Add(new NativeEvidenceEdge(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt64(6) != 0));
            }
        }

        var fields = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT field_access
                FROM native_recovery_fields
                WHERE recovery_id = $recoveryId
                ORDER BY ordinal;
                """;
            command.Parameters.AddWithValue("$recoveryId", run.RecoveryId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                fields.Add(reader.GetString(0));
        }

        return new NativeRecoveryRecord(
            run.RecoveryId,
            new NativeRecoveryRequest(
                run.BuildId,
                run.IndexId,
                run.GameAssemblySha256,
                DeserializeStrings(run.SymbolIdsJson, "symbol_ids_json"),
                run.MaxTraversalEdges),
            run.ToolName,
            run.ToolVersion,
            run.ToolSha256,
            run.Status,
            DeserializeStrings(run.MappingEvidenceJson, "mapping_evidence_json"),
            edges,
            fields,
            run.IsComplete,
            run.OutputSha256,
            run.CreatedAtUtc,
            run.FailureMessage);
    }

    private static StoredRun ReadRun(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            Enum.Parse<NativeRecoveryStatus>(reader.GetString(9), ignoreCase: false),
            reader.GetString(10),
            reader.GetInt64(11) != 0,
            reader.GetString(12),
            DateTimeOffset.ParseExact(reader.GetString(13), "O", CultureInfo.InvariantCulture),
            reader.IsDBNull(14) ? null : reader.GetString(14));

    private static IReadOnlyList<string> DeserializeStrings(string json, string columnName)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json)
                ?? throw new InvalidDataException($"Native recovery column '{columnName}' is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Native recovery column '{columnName}' is not a string array.",
                exception);
        }
    }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    private static string NormalizeSha256(string value, string parameterName)
    {
        RequireSha256(value, parameterName);
        return value;
    }

    private static void RequireSha256(string value, string parameterName)
    {
        RequireText(value, parameterName);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("The value must be a 64-character lower-case SHA-256 digest.", parameterName);
    }

    private static void ValidateOptionalSummary(string? value, string parameterName)
    {
        if (value is not null)
            ValidateSummary(value, parameterName);
    }

    private static void ValidateSummary(string value, string parameterName)
    {
        RequireText(value, parameterName);
        var trimmed = value.Trim();
        if (trimmed.Length > MaximumSummaryLength ||
            trimmed.Any(char.IsControl) ||
            trimmed.Contains("://", StringComparison.Ordinal) ||
            trimmed.Contains('\\') ||
            trimmed.Contains('/') ||
            trimmed.Contains(".bin", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains(".dll", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains(".exe", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("disassembly", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("opcode", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("bytecode", StringComparison.OrdinalIgnoreCase) ||
            LooksLikeRawDump(trimmed))
        {
            throw new InvalidDataException($"{parameterName} must be a bounded evidence summary.");
        }
    }

    private static string NormalizeSummary(string value, string parameterName)
    {
        RequireText(value, parameterName);
        var trimmed = value.Trim();
        ValidateSummary(trimmed, parameterName);
        return trimmed;
    }

    private static bool LooksLikeRawDump(string value)
    {
        var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 4 && tokens.Take(8).All(IsByteToken))
            return true;

        if (tokens.Length >= 2 && IsInstructionMnemonic(tokens[0]))
        {
            return true;
        }

        if (tokens.Length >= 2 && IsAddressToken(tokens[0]))
        {
            var mnemonicIndex = 1;
            while (mnemonicIndex < tokens.Length && IsByteToken(tokens[mnemonicIndex]))
                mnemonicIndex++;
            if (mnemonicIndex < tokens.Length && IsInstructionMnemonic(tokens[mnemonicIndex]))
                return true;
        }

        return false;
    }

    private static bool IsInstructionMnemonic(string token) =>
        token.TrimEnd(':').ToLowerInvariant() is
            "mov" or "push" or "pop" or "call" or "jmp" or "lea" or "ret" or "cmp" or
            "test" or "add" or "sub" or "xor" or "ldr" or "str" or "br" or "bl";

    private static bool IsAddressToken(string token)
    {
        var normalized = token.TrimEnd(':');
        return normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? normalized.Length > 2 && normalized[2..].All(IsHexCharacter)
            : normalized.Length >= 6 && normalized.All(IsHexCharacter);
    }

    private static bool IsHexCharacter(char character) => character is
        >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static bool IsByteToken(string token) =>
        token.Length == 2 && token.All(character => character is
            >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private const string RunSelectSql = """
        SELECT recovery_id, build_id, index_id, game_assembly_sha256,
               symbol_ids_json, max_traversal_edges, tool_name, tool_version,
               tool_sha256, status, mapping_evidence_json, is_complete,
               output_sha256, created_at_utc, failure_message
        FROM native_recovery_runs
        """;

    private sealed record StoredRun(
        string RecoveryId,
        string BuildId,
        string IndexId,
        string GameAssemblySha256,
        string SymbolIdsJson,
        int MaxTraversalEdges,
        string ToolName,
        string ToolVersion,
        string ToolSha256,
        NativeRecoveryStatus Status,
        string MappingEvidenceJson,
        bool IsComplete,
        string OutputSha256,
        DateTimeOffset CreatedAtUtc,
        string? FailureMessage);
}
