namespace S1Atlas.Core.Environment;

public sealed record DependencyVersion(
    DependencyKind Kind,
    string? Version,
    string? Path,
    bool IsInstalled);
