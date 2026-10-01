namespace S1Atlas.Web;

public sealed record ServeOptions(string DataRoot, int Port)
{
    public const int DefaultPort = 5217;
}
