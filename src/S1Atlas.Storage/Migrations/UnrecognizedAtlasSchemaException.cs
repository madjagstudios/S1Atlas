namespace S1Atlas.Storage.Migrations;

public sealed class UnrecognizedAtlasSchemaException : InvalidOperationException
{
    public UnrecognizedAtlasSchemaException(string message)
        : base(message)
    {
    }
}
