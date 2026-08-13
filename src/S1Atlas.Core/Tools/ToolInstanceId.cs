using S1Atlas.Core.Identity;

namespace S1Atlas.Core.Tools;

public static class ToolInstanceId
{
    public static string Create(
        string toolName,
        string executableSha256,
        string platform,
        ToolTrustLevel trustLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        using var writer = new CanonicalHashWriter("tool-instance", 1);
        writer.AppendString(toolName);
        writer.AppendString(executableSha256);
        writer.AppendString(platform);
        writer.AppendString(trustLevel.ToString());
        return writer.Complete();
    }
}
