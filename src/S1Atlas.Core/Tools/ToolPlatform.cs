using System.Runtime.InteropServices;

namespace S1Atlas.Core.Tools;

public static class ToolPlatform
{
    public const string WindowsX64 = "win-x64";

    public static string GetCurrent()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return WindowsX64;
        }

        throw new ToolOperationException(
            "ToolPlatformUnsupported",
            "The managed Cpp2IL tool is supported only on Windows x64.");
    }
}
