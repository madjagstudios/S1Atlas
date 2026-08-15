namespace S1Atlas.Indexing.Scene;

public sealed class SceneIndexFailureException : Exception
{
    public SceneIndexFailureException(SceneQueryStatus status, string message, Exception? innerException = null)
        : base(message, innerException) => Status = status;

    public SceneQueryStatus Status { get; }
}
