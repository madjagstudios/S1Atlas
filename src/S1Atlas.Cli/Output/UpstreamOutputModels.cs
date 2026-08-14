namespace S1Atlas.Cli.Output;

internal sealed record UpstreamStatusOutput(
    string Codebase,
    bool AutoCheck,
    string CheckInterval,
    bool Cached,
    string? CommitSha,
    string MatchKind,
    string Explanation);

internal sealed record UpstreamSyncOutput(
    string Codebase,
    string CommitSha,
    bool ReusedCache,
    bool Stale,
    int FileCount,
    string? Warning);
