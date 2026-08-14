using System.Net.Http.Json;
using System.Security.Cryptography;
using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Upstream;

public sealed class GitHubUpstreamClient : IUpstreamClient
{
    private readonly HttpClient _httpClient;

    public GitHubUpstreamClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("S1Atlas/0.1");
    }

    public async Task<UpstreamSnapshot> FetchAsync(UpstreamRepositoryConfiguration repository, string commitSha, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ValidateCommit(commitSha);
        var treeUri = $"https://api.github.com/repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/git/trees/{commitSha}?recursive=1";
        using var response = await _httpClient.GetAsync(treeUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var tree = await response.Content.ReadFromJsonAsync<GitTreeResponse>(cancellationToken: cancellationToken) ?? throw new InvalidDataException("GitHub returned no tree.");
        if (tree.Truncated)
            throw new InvalidDataException("GitHub returned a truncated repository tree; the upstream snapshot was not cached.");
        var files = new List<UpstreamFile>();
        foreach (var entry in tree.Tree.Where(entry => entry.Type == "blob" && entry.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            ValidateRelativePath(entry.Path);
            var escapedPath = string.Join('/', entry.Path.Split('/').Select(Uri.EscapeDataString));
            var rawUri = $"https://raw.githubusercontent.com/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/{commitSha}/{escapedPath}";
            var content = await _httpClient.GetByteArrayAsync(rawUri, cancellationToken);
            files.Add(new UpstreamFile(entry.Path, content, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()));
        }
        return new UpstreamSnapshot(repository, commitSha, files);
    }

    private static void ValidateCommit(string commitSha)
    {
        if (commitSha is not { Length: 40 } || commitSha.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("The upstream commit must be a 40-character lower-case SHA.", nameof(commitSha));
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal) || path.Contains(':') ||
            path.Split('/').Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
            throw new InvalidDataException("GitHub returned an unsafe upstream file path.");
    }

    private sealed record GitTreeResponse(IReadOnlyList<GitTreeEntry> Tree, bool Truncated);
    private sealed record GitTreeEntry(string Path, string Type);
}
