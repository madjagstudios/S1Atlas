using System.Runtime.InteropServices;

namespace S1Atlas.Application.Readiness;

public sealed class DotNetRuntimeProbe : IDotNetRuntimeProbe
{
    public DotNetRuntimeInfo GetCurrent()
    {
        var version = Environment.Version;
        return new DotNetRuntimeInfo(
            version.Major >= 8,
            version.ToString(),
            RuntimeInformation.FrameworkDescription);
    }
}
