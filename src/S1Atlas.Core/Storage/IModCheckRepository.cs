namespace S1Atlas.Core.Storage;

public sealed record ModCheckSymbol(IndexSymbolRecord Symbol, string? BodyFingerprint);

/// <summary>Dependency-scoped comparison reads; never loads a build's relationships or fingerprint set.</summary>
public interface IModCheckRepository
{
    Task<ModCheckSymbol?> GetModCheckSymbolAsync(string indexId, string canonicalKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<ModCheckSymbol>> FindModCheckMembersAsync(
        string indexId, string? declaringType, string name, string kind, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<ModCheckSymbol>> FindModCheckMovesAsync(
        string indexId, string declaringType, string name, string signatureTail, string kind,
        string bodyFingerprint, CancellationToken cancellationToken);
}
