namespace S1Atlas.Extraction.Discovery;

internal interface IDependencyVersionReader
{
    string? TryReadVersion(string path);
}
