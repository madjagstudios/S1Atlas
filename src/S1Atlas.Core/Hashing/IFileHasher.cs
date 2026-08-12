namespace S1Atlas.Core.Hashing;

public interface IFileHasher
{
    Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken);
}
