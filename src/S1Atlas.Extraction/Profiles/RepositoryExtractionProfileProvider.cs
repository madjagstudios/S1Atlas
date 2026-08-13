using System.Security;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Profiles;

public sealed class RepositoryExtractionProfileProvider : IExtractionProfileProvider
{
    private readonly string _profileDirectory;
    private readonly ExtractionProfileSerializer _serializer = new();

    public RepositoryExtractionProfileProvider(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        _profileDirectory = Path.GetFullPath(profileDirectory);
    }

    public IReadOnlyList<ResolvedExtractionProfile> GetAll()
    {
        var paths = EnumeratePaths();
        var profiles = new List<ResolvedExtractionProfile>(paths.Length);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var resolved = _serializer.Deserialize(ReadAllText(path), path);
            if (!identities.Add(resolved.Profile.ProfileId))
            {
                throw new ToolOperationException("ExtractionProfileInvalid", $"Extraction profile '{path}' duplicates profile ID '{resolved.Profile.ProfileId}'.");
            }
            profiles.Add(resolved);
        }
        return profiles;
    }

    public ResolvedExtractionProfile GetRequired(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return GetAll().FirstOrDefault(candidate => string.Equals(candidate.Profile.ProfileId, profileId, StringComparison.Ordinal))
            ?? throw new ToolOperationException("UnknownExtractionProfile", $"No repository extraction profile exists for '{profileId}'.");
    }

    private string[] EnumeratePaths()
    {
        if (!Directory.Exists(_profileDirectory)) throw new ToolOperationException("ExtractionProfileInvalid", $"Extraction profile directory '{_profileDirectory}' does not exist.");
        try
        {
            return Directory.EnumerateFiles(_profileDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (IsFilesystemFailure(exception))
        {
            throw ReadFailure(_profileDirectory, exception);
        }
    }

    private static string ReadAllText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception exception) when (IsFilesystemFailure(exception)) { throw ReadFailure(path, exception); }
    }

    private static bool IsFilesystemFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException;
    private static ToolOperationException ReadFailure(string path, Exception exception) => new("ExtractionProfileInvalid", $"Extraction profiles could not be read from '{path}': {exception.Message}", exception);
}
