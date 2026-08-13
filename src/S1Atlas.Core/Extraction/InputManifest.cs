namespace S1Atlas.Core.Extraction;

public sealed record InputManifest(IReadOnlyList<InputManifestEntry> Entries);

public sealed record InputManifestEntry(
    string RelativePath,
    string Role,
    long Size,
    string Sha256,
    DateTimeOffset LastWriteUtc);
