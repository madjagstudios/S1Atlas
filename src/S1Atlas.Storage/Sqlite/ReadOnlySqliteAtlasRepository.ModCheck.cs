using Microsoft.Data.Sqlite;
using S1Atlas.Core.Storage;

namespace S1Atlas.Storage.Sqlite;

public sealed partial class ReadOnlySqliteAtlasRepository : IModCheckRepository
{
    private const string ModCheckSelect = """
        SELECT symbol.symbol_id, symbol.snapshot_id, symbol.canonical_key, symbol.kind,
               symbol.qualified_name, symbol.signature, symbol.is_best_effort,
               symbol.body_recovery_status, symbol.is_public, symbol.is_generated, fp.fingerprint
        FROM index_runs AS run
        INNER JOIN symbols AS symbol ON symbol.snapshot_id = run.snapshot_id
        LEFT JOIN symbol_fingerprints AS fp ON fp.symbol_id = symbol.symbol_id AND fp.fingerprint_kind = 'method-body'
        WHERE run.index_id = $indexId AND run.status = 'Completed'
        """;

    public async Task<ModCheckSymbol?> GetModCheckSymbolAsync(string indexId, string canonicalKey, CancellationToken cancellationToken)
    {
        var rows = await ReadModCheckAsync(indexId,
            "AND symbol.canonical_key = $key COLLATE BINARY LIMIT 2;",
            [new("$key", canonicalKey)], cancellationToken);
        return rows.Count == 1 ? rows[0] : null;
    }

    public Task<IReadOnlyList<ModCheckSymbol>> FindModCheckMembersAsync(
        string indexId, string? declaringType, string name, string kind, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return ReadModCheckAsync(indexId,
            """
            AND symbol.simple_name = $name COLLATE NOCASE AND symbol.simple_name = $name COLLATE BINARY
            AND symbol.kind = $kind
            AND ($type IS NULL OR substr(symbol.qualified_name, 1, instr(symbol.qualified_name, '::') - 1) = $type COLLATE BINARY)
            ORDER BY symbol.canonical_key COLLATE BINARY LIMIT $limit;
            """,
            [new("$name", name), new("$kind", kind), new("$type", (object?)declaringType ?? DBNull.Value), new("$limit", limit)],
            cancellationToken);
    }

    public Task<IReadOnlyList<ModCheckSymbol>> FindModCheckMovesAsync(
        string indexId, string declaringType, string name, string signatureTail, string kind,
        string bodyFingerprint, CancellationToken cancellationToken) =>
        ReadModCheckAsync(indexId,
            """
            AND symbol.kind = $kind AND fp.fingerprint = $body
            AND (
                (symbol.canonical_key >= $prefix COLLATE BINARY AND symbol.canonical_key < $prefixEnd COLLATE BINARY
                 AND symbol.simple_name <> $name COLLATE BINARY)
                OR
                (symbol.simple_name = $name COLLATE NOCASE AND symbol.simple_name = $name COLLATE BINARY
                 AND substr(symbol.qualified_name, instr(symbol.qualified_name, '::') + 2) = $tail COLLATE BINARY
                 AND substr(symbol.qualified_name, 1, instr(symbol.qualified_name, '::') - 1) <> $type COLLATE BINARY)
            )
            ORDER BY symbol.canonical_key COLLATE BINARY LIMIT 2;
            """,
            [new("$kind", kind), new("$body", bodyFingerprint), new("$name", name), new("$tail", signatureTail),
             new("$type", declaringType), new("$prefix", $"ScheduleI:Installed:{kind}:{declaringType}::"),
             new("$prefixEnd", $"ScheduleI:Installed:{kind}:{declaringType}::\uffff")], cancellationToken);

    private Task<IReadOnlyList<ModCheckSymbol>> ReadModCheckAsync(
        string indexId, string filter, IReadOnlyList<SqliteParameter> parameters, CancellationToken cancellationToken) =>
        WithConnectionAsync<IReadOnlyList<ModCheckSymbol>>(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ModCheckSelect + "\n" + filter;
            command.Parameters.AddWithValue("$indexId", indexId);
            foreach (var parameter in parameters) command.Parameters.Add(parameter);
            var rows = new List<ModCheckSymbol>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(new ModCheckSymbol(ReadSymbol(reader), reader.IsDBNull(10) ? null : reader.GetString(10)));
            return rows;
        }, cancellationToken);
}
