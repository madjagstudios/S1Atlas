namespace S1Atlas.Web;

public sealed class AtlasStoreMissingException : Exception
{
    public AtlasStoreMissingException()
        : base("The Atlas data store was not found.")
    {
    }
}
