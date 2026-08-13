using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Tools;

internal sealed class ToolInstallationDocumentStore
{
    private const string ManifestFileName = "tool-manifest.json";
    private const string InstallationFileName = "installation.json";

    private static readonly UTF8Encoding Utf8NoBom = new(
        encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly ToolDefinitionSerializer _definitionSerializer = new();

    public async Task WriteAsync(
        string installRoot,
        ResolvedToolDefinition definition,
        ManagedToolInstallation installation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(installation);

        var fullInstallRoot = Path.GetFullPath(installRoot);
        Directory.CreateDirectory(fullInstallRoot);

        var manifestPath = Path.Combine(fullInstallRoot, ManifestFileName);
        var installationPath = Path.Combine(fullInstallRoot, InstallationFileName);
        ToolPathPolicy.EnsureNoReparsePointInExistingPath(
            fullInstallRoot,
            manifestPath);
        ToolPathPolicy.EnsureNoReparsePointInExistingPath(
            fullInstallRoot,
            installationPath);

        var manifestTemporaryPath = CreateTemporaryPath(manifestPath);
        var installationTemporaryPath = CreateTemporaryPath(installationPath);

        try
        {
            var manifestJson = _definitionSerializer.Serialize(
                definition.Definition);
            var installationJson = JsonSerializer.Serialize(
                ToDocument(installation),
                JsonOptions);

            await WriteTemporaryAsync(
                manifestTemporaryPath,
                manifestJson,
                cancellationToken);
            await WriteTemporaryAsync(
                installationTemporaryPath,
                installationJson,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(manifestTemporaryPath, manifestPath, overwrite: true);
            File.Move(
                installationTemporaryPath,
                installationPath,
                overwrite: true);
        }
        finally
        {
            DeleteFileBestEffort(manifestTemporaryPath);
            DeleteFileBestEffort(installationTemporaryPath);
        }
    }

    public async Task<(
        ResolvedToolDefinition Definition,
        ManagedToolInstallation Installation)?> TryReadAsync(
        string installRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        var fullInstallRoot = Path.GetFullPath(installRoot);
        var manifestPath = Path.Combine(fullInstallRoot, ManifestFileName);
        var installationPath = Path.Combine(fullInstallRoot, InstallationFileName);

        try
        {
            if (!IsRegularFile(fullInstallRoot, manifestPath) ||
                !IsRegularFile(fullInstallRoot, installationPath))
            {
                return null;
            }

            var manifestJson = await File.ReadAllTextAsync(
                manifestPath,
                Utf8NoBom,
                cancellationToken);
            var installationJson = await File.ReadAllTextAsync(
                installationPath,
                Utf8NoBom,
                cancellationToken);

            var definition = _definitionSerializer.Deserialize(
                manifestJson,
                manifestPath);
            var document = JsonSerializer.Deserialize<ToolInstallationDocument>(
                installationJson,
                JsonOptions);
            var installation = FromDocument(document);
            return installation is null ? null : (definition, installation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            NotSupportedException or
            ToolOperationException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }

    private static ToolInstallationDocument ToDocument(
        ManagedToolInstallation installation) =>
        new()
        {
            SchemaVersion = installation.SchemaVersion,
            ToolId = installation.ToolId,
            DisplayName = installation.DisplayName,
            Version = installation.Version,
            Platform = installation.Platform,
            DefinitionDigest = installation.DefinitionDigest,
            PackageSha256 = installation.PackageSha256,
            ExecutableSha256 = installation.ExecutableSha256,
            RootPath = installation.RootPath,
            Status = installation.Status,
            InstalledAtUtc = installation.InstalledAtUtc,
            LastVerifiedAtUtc = installation.LastVerifiedAtUtc,
            ProbeResults = installation.ProbeResults
                .Select(ToDocument)
                .Select(result => (ToolProbeResultDocument?)result)
                .ToList(),
            ReplacedInstallationPath = installation.ReplacedInstallationPath
        };

    private static ToolProbeResultDocument ToDocument(ToolProbeResult result) =>
        new()
        {
            ProbeId = result.ProbeId,
            Succeeded = result.Succeeded,
            ExitCode = result.ExitCode,
            TimedOut = result.TimedOut,
            StandardOutputTruncated = result.StandardOutputTruncated,
            StandardErrorTruncated = result.StandardErrorTruncated,
            FailureCode = result.FailureCode,
            FailureMessage = result.FailureMessage
        };

    private static ManagedToolInstallation? FromDocument(
        ToolInstallationDocument? document)
    {
        if (document?.SchemaVersion != 1 ||
            !HasText(document.ToolId) ||
            !HasText(document.DisplayName) ||
            !HasText(document.Version) ||
            !HasText(document.Platform) ||
            !IsNormalizedSha256(document.DefinitionDigest) ||
            !IsNormalizedSha256(document.PackageSha256) ||
            !IsNormalizedSha256(document.ExecutableSha256) ||
            !HasText(document.RootPath) ||
            !Path.IsPathFullyQualified(document.RootPath) ||
            document.Status is null ||
            !Enum.IsDefined(document.Status.Value) ||
            document.InstalledAtUtc is null ||
            document.LastVerifiedAtUtc is null ||
            document.ProbeResults is null)
        {
            return null;
        }

        var probeResults = new List<ToolProbeResult>(
            document.ProbeResults.Count);
        foreach (var probe in document.ProbeResults)
        {
            if (probe is null ||
                !HasText(probe.ProbeId) ||
                probe.Succeeded is null ||
                probe.TimedOut is null ||
                probe.StandardOutputTruncated is null ||
                probe.StandardErrorTruncated is null)
            {
                return null;
            }

            probeResults.Add(new ToolProbeResult(
                probe.ProbeId,
                probe.Succeeded.Value,
                probe.ExitCode,
                probe.TimedOut.Value,
                probe.StandardOutputTruncated.Value,
                probe.StandardErrorTruncated.Value,
                probe.FailureCode,
                probe.FailureMessage));
        }

        return new ManagedToolInstallation(
            document.SchemaVersion.Value,
            document.ToolId,
            document.DisplayName,
            document.Version,
            document.Platform,
            document.DefinitionDigest,
            document.PackageSha256,
            document.ExecutableSha256,
            Path.GetFullPath(document.RootPath),
            document.Status.Value,
            document.InstalledAtUtc.Value,
            document.LastVerifiedAtUtc.Value,
            probeResults,
            document.ReplacedInstallationPath);
    }

    private static bool IsRegularFile(string root, string path)
    {
        ToolPathPolicy.EnsureNoReparsePointInExistingPath(root, path);
        if (!File.Exists(path))
        {
            return false;
        }

        var attributes = File.GetAttributes(path);
        return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    }

    private static bool HasText([NotNullWhen(true)] string? value) =>
        !string.IsNullOrWhiteSpace(value);

    private static bool IsNormalizedSha256(
        [NotNullWhen(true)] string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string CreateTemporaryPath(string destinationPath) =>
        $"{destinationPath}.{Guid.NewGuid():N}.tmp";

    private static async Task WriteTemporaryAsync(
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        var bytes = Utf8NoBom.GetBytes(contents);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
