using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using S1Atlas.Core.Tools;

namespace S1Atlas.Storage.Sqlite;

public sealed partial class SqliteAtlasRepository
{
    private static readonly JsonSerializerOptions ProbeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task SaveVerifiedManagedToolAsync(
        ManagedToolInstallation installation,
        ToolInstance toolInstance,
        CancellationToken cancellationToken)
    {
        ValidateVerifiedToolProvenance(installation, toolInstance);
        var probeSummary = JsonSerializer.Serialize(
            installation.ProbeResults,
            ProbeJsonOptions);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await UpsertManagedToolInstallationAsync(
                connection,
                transaction,
                installation,
                probeSummary,
                cancellationToken);
            await UpsertToolInstanceAsync(
                connection,
                transaction,
                toolInstance,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new InvalidOperationException(
                $"Managed tool provenance for '{installation.ToolId}' " +
                "could not be saved atomically.",
                exception);
        }
    }

    public async Task<ManagedToolInstallation?> GetManagedToolAsync(
        string toolId,
        string version,
        string platform,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                tool_id,
                version,
                platform,
                definition_digest,
                package_sha256,
                executable_sha256,
                root_path,
                status,
                installed_at_utc,
                last_verified_at_utc,
                probe_summary
            FROM managed_tool_installations
            WHERE tool_id = $toolId
              AND version = $version
              AND platform = $platform;
            """;
        command.Parameters.AddWithValue("$toolId", toolId);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$platform", platform);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var storedToolId = reader.GetString(0);
        return new ManagedToolInstallation(
            SchemaVersion: 1,
            ToolId: storedToolId,
            DisplayName: storedToolId,
            Version: reader.GetString(1),
            Platform: reader.GetString(2),
            DefinitionDigest: reader.GetString(3),
            PackageSha256: reader.GetString(4),
            ExecutableSha256: reader.GetString(5),
            RootPath: reader.GetString(6),
            Status: ParseStoredEnum<ToolInstallationStatus>(
                reader.GetString(7),
                "managed_tool_installations.status"),
            InstalledAtUtc: ParseStoredTimestamp(
                reader.GetString(8),
                "managed_tool_installations.installed_at_utc"),
            LastVerifiedAtUtc: ParseStoredTimestamp(
                reader.GetString(9),
                "managed_tool_installations.last_verified_at_utc"),
            ProbeResults: ParseProbeSummary(reader.GetString(10)),
            ReplacedInstallationPath: null);
    }

    public async Task<ToolInstance?> GetToolInstanceAsync(
        string toolInstanceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolInstanceId);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                tool_instance_id,
                tool_name,
                version_label,
                platform,
                trust_level,
                definition_digest,
                package_sha256,
                executable_sha256,
                observed_path,
                first_observed_at_utc,
                last_verified_at_utc,
                status
            FROM tool_instances
            WHERE tool_instance_id = $toolInstanceId;
            """;
        command.Parameters.AddWithValue("$toolInstanceId", toolInstanceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ToolInstance(
            ToolInstanceId: reader.GetString(0),
            ToolName: reader.GetString(1),
            VersionLabel: reader.IsDBNull(2) ? null : reader.GetString(2),
            Platform: reader.GetString(3),
            TrustLevel: ParseStoredEnum<ToolTrustLevel>(
                reader.GetString(4),
                "tool_instances.trust_level"),
            DefinitionDigest: reader.IsDBNull(5) ? null : reader.GetString(5),
            PackageSha256: reader.IsDBNull(6) ? null : reader.GetString(6),
            ExecutableSha256: reader.GetString(7),
            ObservedPath: reader.GetString(8),
            FirstObservedAtUtc: ParseStoredTimestamp(
                reader.GetString(9),
                "tool_instances.first_observed_at_utc"),
            LastVerifiedAtUtc: ParseStoredTimestamp(
                reader.GetString(10),
                "tool_instances.last_verified_at_utc"),
            Status: ParseStoredEnum<ToolInstallationStatus>(
                reader.GetString(11),
                "tool_instances.status"));
    }

    private static void ValidateVerifiedToolProvenance(
        ManagedToolInstallation installation,
        ToolInstance toolInstance)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(toolInstance);

        if (installation.Status != ToolInstallationStatus.Verified)
        {
            throw new InvalidOperationException(
                "Only verified managed tool installations can be persisted.");
        }

        if (toolInstance.Status != ToolInstallationStatus.Verified)
        {
            throw new InvalidOperationException(
                "Only verified tool instances can be persisted.");
        }

        if (toolInstance.TrustLevel != ToolTrustLevel.ManagedPinned)
        {
            throw new InvalidOperationException(
                "Managed tool persistence requires ManagedPinned trust.");
        }

        if (!string.Equals(
                installation.ToolId,
                toolInstance.ToolName,
                StringComparison.Ordinal) ||
            !string.Equals(
                installation.Version,
                toolInstance.VersionLabel,
                StringComparison.Ordinal) ||
            !string.Equals(
                installation.Platform,
                toolInstance.Platform,
                StringComparison.Ordinal) ||
            !string.Equals(
                installation.DefinitionDigest,
                toolInstance.DefinitionDigest,
                StringComparison.Ordinal) ||
            !string.Equals(
                installation.PackageSha256,
                toolInstance.PackageSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                installation.ExecutableSha256,
                toolInstance.ExecutableSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The managed installation and tool instance provenance disagree.");
        }

        var expectedId = ToolInstanceId.Create(
            installation.ToolId,
            installation.ExecutableSha256,
            installation.Platform,
            ToolTrustLevel.ManagedPinned);
        if (!string.Equals(
                expectedId,
                toolInstance.ToolInstanceId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The stored tool instance ID does not match its production identity inputs.");
        }
    }

    private static async Task UpsertManagedToolInstallationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ManagedToolInstallation installation,
        string probeSummary,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO managed_tool_installations (
                tool_id,
                version,
                platform,
                definition_digest,
                package_sha256,
                executable_sha256,
                root_path,
                status,
                installed_at_utc,
                last_verified_at_utc,
                probe_summary)
            VALUES (
                $toolId,
                $version,
                $platform,
                $definitionDigest,
                $packageSha256,
                $executableSha256,
                $rootPath,
                $status,
                $installedAtUtc,
                $lastVerifiedAtUtc,
                $probeSummary)
            ON CONFLICT(tool_id, version, platform) DO UPDATE SET
                definition_digest = excluded.definition_digest,
                package_sha256 = excluded.package_sha256,
                executable_sha256 = excluded.executable_sha256,
                root_path = excluded.root_path,
                status = excluded.status,
                installed_at_utc = excluded.installed_at_utc,
                last_verified_at_utc = excluded.last_verified_at_utc,
                probe_summary = excluded.probe_summary;
            """;
        command.Parameters.AddWithValue("$toolId", installation.ToolId);
        command.Parameters.AddWithValue("$version", installation.Version);
        command.Parameters.AddWithValue("$platform", installation.Platform);
        command.Parameters.AddWithValue(
            "$definitionDigest",
            installation.DefinitionDigest);
        command.Parameters.AddWithValue(
            "$packageSha256",
            installation.PackageSha256);
        command.Parameters.AddWithValue(
            "$executableSha256",
            installation.ExecutableSha256);
        command.Parameters.AddWithValue("$rootPath", installation.RootPath);
        command.Parameters.AddWithValue("$status", installation.Status.ToString());
        command.Parameters.AddWithValue(
            "$installedAtUtc",
            FormatTimestamp(installation.InstalledAtUtc));
        command.Parameters.AddWithValue(
            "$lastVerifiedAtUtc",
            FormatTimestamp(installation.LastVerifiedAtUtc));
        command.Parameters.AddWithValue("$probeSummary", probeSummary);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertToolInstanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ToolInstance toolInstance,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tool_instances (
                tool_instance_id,
                tool_name,
                version_label,
                platform,
                trust_level,
                definition_digest,
                package_sha256,
                executable_sha256,
                observed_path,
                first_observed_at_utc,
                last_verified_at_utc,
                status)
            VALUES (
                $toolInstanceId,
                $toolName,
                $versionLabel,
                $platform,
                $trustLevel,
                $definitionDigest,
                $packageSha256,
                $executableSha256,
                $observedPath,
                $firstObservedAtUtc,
                $lastVerifiedAtUtc,
                $status)
            ON CONFLICT(tool_instance_id) DO UPDATE SET
                tool_name = excluded.tool_name,
                version_label = excluded.version_label,
                platform = excluded.platform,
                trust_level = excluded.trust_level,
                definition_digest = excluded.definition_digest,
                package_sha256 = excluded.package_sha256,
                executable_sha256 = excluded.executable_sha256,
                observed_path = excluded.observed_path,
                first_observed_at_utc = CASE
                    WHEN julianday(excluded.first_observed_at_utc) <
                         julianday(tool_instances.first_observed_at_utc)
                    THEN excluded.first_observed_at_utc
                    ELSE tool_instances.first_observed_at_utc
                END,
                last_verified_at_utc = excluded.last_verified_at_utc,
                status = excluded.status;
            """;
        command.Parameters.AddWithValue(
            "$toolInstanceId",
            toolInstance.ToolInstanceId);
        command.Parameters.AddWithValue("$toolName", toolInstance.ToolName);
        command.Parameters.AddWithValue(
            "$versionLabel",
            (object?)toolInstance.VersionLabel ?? DBNull.Value);
        command.Parameters.AddWithValue("$platform", toolInstance.Platform);
        command.Parameters.AddWithValue(
            "$trustLevel",
            toolInstance.TrustLevel.ToString());
        command.Parameters.AddWithValue(
            "$definitionDigest",
            (object?)toolInstance.DefinitionDigest ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$packageSha256",
            (object?)toolInstance.PackageSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$executableSha256",
            toolInstance.ExecutableSha256);
        command.Parameters.AddWithValue("$observedPath", toolInstance.ObservedPath);
        command.Parameters.AddWithValue(
            "$firstObservedAtUtc",
            FormatTimestamp(toolInstance.FirstObservedAtUtc));
        command.Parameters.AddWithValue(
            "$lastVerifiedAtUtc",
            FormatTimestamp(toolInstance.LastVerifiedAtUtc));
        command.Parameters.AddWithValue("$status", toolInstance.Status.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyList<ToolProbeResult> ParseProbeSummary(string json)
    {
        try
        {
            var probes = JsonSerializer.Deserialize<ToolProbeResult?[]>(
                json,
                ProbeJsonOptions);
            if (probes is null || probes.Any(probe => probe is null))
            {
                throw new JsonException(
                    "The stored probe summary is null or contains a null entry.");
            }

            return probes.Select(probe => probe!).ToArray();
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException)
        {
            throw new InvalidOperationException(
                "Stored managed tool probe JSON failed integrity validation.",
                exception);
        }
    }

    private static TEnum ParseStoredEnum<TEnum>(
        string value,
        string fieldName)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(parsed))
        {
            throw new InvalidOperationException(
                $"Stored field '{fieldName}' contains unknown enum value '{value}'.");
        }

        return parsed;
    }

    private static DateTimeOffset ParseStoredTimestamp(
        string value,
        string fieldName)
    {
        try
        {
            return ParseTimestamp(value);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"Stored field '{fieldName}' contains an invalid timestamp.",
                exception);
        }
    }
}
