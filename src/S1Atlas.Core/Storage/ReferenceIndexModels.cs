namespace S1Atlas.Core.Storage;

public sealed record ReferenceIndexContextRecord(
    string ReferenceIndexId,
    string GameIndexId,
    string BuildId);

public sealed record IndexReferenceModRecord(
    string ModId,
    string DisplayName,
    string Version,
    string? License,
    string RootPath,
    string ContentSha256,
    IReadOnlyList<string> SymbolIds);

public sealed record IndexReferenceDocumentRecord(
    string ModId,
    string RelativePath,
    string Kind,
    string Sha256,
    long ByteCount,
    string Content);
