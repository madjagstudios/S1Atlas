namespace S1Atlas.Core.Indexing;

public sealed record UpstreamRepositoryConfiguration(
    CodebaseKind Codebase,
    string Owner,
    string Name,
    string DefaultBranch = "main");

public sealed record UpstreamFile(
    string RelativePath,
    byte[] Content,
    string Sha256);

public sealed record UpstreamSnapshot(
    UpstreamRepositoryConfiguration Repository,
    string CommitSha,
    IReadOnlyList<UpstreamFile> Files);

public sealed record UpstreamSyncResult(
    CodebaseKind Codebase,
    string CommitSha,
    bool ReusedCache,
    bool Stale,
    int FileCount,
    string? Warning);
