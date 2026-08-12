using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security;

namespace S1Atlas.Extraction.Discovery;

internal sealed class DependencyVersionReader : IDependencyVersionReader
{
    private readonly Func<string, string?> _fileVersionProbe;
    private readonly Func<string, string?> _assemblyVersionProbe;

    public DependencyVersionReader()
        : this(ReadFileVersion, ReadAssemblyVersion)
    {
    }

    internal DependencyVersionReader(
        Func<string, string?> fileVersionProbe,
        Func<string, string?> assemblyVersionProbe)
    {
        _fileVersionProbe = fileVersionProbe ??
            throw new ArgumentNullException(nameof(fileVersionProbe));
        _assemblyVersionProbe = assemblyVersionProbe ??
            throw new ArgumentNullException(nameof(assemblyVersionProbe));
    }

    public string? TryReadVersion(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var fileVersion = _fileVersionProbe(path);
            if (!string.IsNullOrWhiteSpace(fileVersion))
            {
                return fileVersion;
            }
        }
        catch (Exception exception) when (IsExpectedProbeFailure(exception))
        {
            return null;
        }

        try
        {
            return _assemblyVersionProbe(path);
        }
        catch (Exception exception) when (IsExpectedProbeFailure(exception))
        {
            return null;
        }
    }

    private static string? ReadFileVersion(string path) =>
        FileVersionInfo.GetVersionInfo(path).FileVersion;

    private static string? ReadAssemblyVersion(string path) =>
        AssemblyName.GetAssemblyName(path).Version?.ToString();

    private static bool IsExpectedProbeFailure(Exception exception) =>
        exception is Win32Exception or IOException or UnauthorizedAccessException or
            SecurityException or BadImageFormatException;
}
