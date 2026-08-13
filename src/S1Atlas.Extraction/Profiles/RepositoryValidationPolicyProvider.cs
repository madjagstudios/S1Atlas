using System.Security;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Profiles;

public sealed class RepositoryValidationPolicyProvider : IValidationPolicyProvider
{
    private readonly string _policyDirectory;
    private readonly ValidationPolicySerializer _serializer = new();

    public RepositoryValidationPolicyProvider(string policyDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyDirectory);
        _policyDirectory = Path.GetFullPath(policyDirectory);
    }

    public IReadOnlyList<ResolvedValidationPolicy> GetAll()
    {
        var paths = EnumeratePaths();
        var policies = new List<ResolvedValidationPolicy>(paths.Length);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var resolved = _serializer.Deserialize(ReadAllText(path), path);
            if (!identities.Add(resolved.Policy.PolicyId))
            {
                throw new ToolOperationException("ValidationPolicyInvalid", $"Validation policy '{path}' duplicates policy ID '{resolved.Policy.PolicyId}'.");
            }
            policies.Add(resolved);
        }
        return policies;
    }

    public ResolvedValidationPolicy GetRequired(string policyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        return GetAll().FirstOrDefault(candidate => string.Equals(candidate.Policy.PolicyId, policyId, StringComparison.Ordinal))
            ?? throw new ToolOperationException("UnknownValidationPolicy", $"No repository validation policy exists for '{policyId}'.");
    }

    private string[] EnumeratePaths()
    {
        if (!Directory.Exists(_policyDirectory)) throw new ToolOperationException("ValidationPolicyInvalid", $"Validation policy directory '{_policyDirectory}' does not exist.");
        try
        {
            return Directory.EnumerateFiles(_policyDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (IsFilesystemFailure(exception))
        {
            throw ReadFailure(_policyDirectory, exception);
        }
    }

    private static string ReadAllText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception exception) when (IsFilesystemFailure(exception)) { throw ReadFailure(path, exception); }
    }

    private static bool IsFilesystemFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException;
    private static ToolOperationException ReadFailure(string path, Exception exception) => new("ValidationPolicyInvalid", $"Validation policies could not be read from '{path}': {exception.Message}", exception);
}
