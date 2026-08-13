namespace S1Atlas.Core.Tools;

public sealed class ToolOperationException : InvalidOperationException
{
    public ToolOperationException(
        string code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
    }

    public string Code { get; }
}
