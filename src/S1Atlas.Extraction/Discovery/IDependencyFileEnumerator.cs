namespace S1Atlas.Extraction.Discovery;

internal interface IDependencyFileEnumerator
{
    IReadOnlyList<string> EnumerateDlls(string rootPath);
}
