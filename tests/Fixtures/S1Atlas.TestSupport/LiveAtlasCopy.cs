using Microsoft.Data.Sqlite;

namespace S1Atlas.TestSupport;

/// <summary>
/// Copies a developer's live atlas directory into a per-test temp directory so
/// that <c>LocalGameRequired</c> tests never open the live database for writing.
/// The live database is never opened at all: existence is checked with
/// <see cref="File.Exists(string)"/>, then the whole directory is copied and every
/// repository, migration, and CLI invocation runs against the copy.
/// </summary>
public sealed class LiveAtlasCopy : IAsyncDisposable
{
    /// <summary>
    /// Set to <c>1</c> to run <c>LocalGameRequired</c> tests explicitly. Default
    /// test runs skip them without touching the live atlas.
    /// </summary>
    public const string EnableVariable = "S1ATLAS_RUN_LOCAL_GAME_TESTS";

    private const string HomeVariable = "S1ATLAS_HOME";

    private bool _disposed;

    private LiveAtlasCopy(string dataRoot)
    {
        DataRoot = dataRoot;
    }

    /// <summary>
    /// The copied atlas root. Always inside the test temp area.
    /// </summary>
    public string DataRoot { get; }

    public string DatabasePath => Path.Combine(DataRoot, "atlas.db");

    public static bool IsExplicitlyEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable(EnableVariable),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Resolves the live atlas root exactly like the product: the
    /// <c>S1ATLAS_HOME</c> override, else the per-user data directory.
    /// </summary>
    public static string ResolveLiveDataRoot()
    {
        var overridePath = Environment.GetEnvironmentVariable(HomeVariable);
        var root = !string.IsNullOrWhiteSpace(overridePath)
            ? overridePath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "S1Atlas");
        return Path.GetFullPath(root);
    }

    /// <summary>
    /// Throws unless <paramref name="dataRoot"/> is strictly inside the test temp
    /// area. Call this on every data root a <c>LocalGameRequired</c> test opens so
    /// a wiring mistake can never aim repository or CLI calls at the live atlas.
    /// </summary>
    public static void RequireTestTempRoot(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var fullRoot = Path.GetFullPath(dataRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var temp = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullRoot.StartsWith(temp + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException(
                "Refusing to open an atlas outside the test temp area. " +
                "LocalGameRequired tests must run against a LiveAtlasCopy.");
        }
    }

    /// <summary>
    /// Copies <paramref name="liveDataRoot"/> into a fresh per-test temp directory.
    /// Returns <c>null</c> when the temp drive cannot hold the copy, so callers can
    /// skip; throws when the live database is missing (callers pre-check with a skip).
    /// </summary>
    public static async Task<LiveAtlasCopy?> CreateAsync(
        string liveDataRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveDataRoot);
        var liveRoot = Path.GetFullPath(liveDataRoot);
        var liveDatabase = Path.Combine(liveRoot, "atlas.db");
        if (!File.Exists(liveDatabase))
        {
            throw new FileNotFoundException(
                "No live atlas database to copy.", liveDatabase);
        }

        var copyRoot = Path.Combine(
            Path.GetTempPath(),
            "s1atlas-live-atlas-copy-" + Guid.NewGuid().ToString("N"));
        if (!HasSpaceFor(liveRoot, copyRoot))
        {
            return null;
        }

        Directory.CreateDirectory(copyRoot);
        try
        {
            await CopyDirectoryAsync(liveRoot, copyRoot, cancellationToken);
        }
        catch
        {
            await TestDirectory.DeleteTreeAsync(copyRoot);
            throw;
        }

        RequireTestTempRoot(copyRoot);
        return new LiveAtlasCopy(copyRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await TestDirectory.DeleteTreeAsync(DataRoot);
        GC.SuppressFinalize(this);
    }

    private static bool HasSpaceFor(string liveRoot, string copyRoot)
    {
        long required = 0;
        foreach (var file in Directory.EnumerateFiles(liveRoot, "*", SearchOption.AllDirectories))
        {
            required += new FileInfo(file).Length;
        }

        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(copyRoot))!);
        try
        {
            return drive.AvailableFreeSpace > required;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static async Task CopyDirectoryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(directory))
            {
                continue;
            }

            Directory.CreateDirectory(
                Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(file))
            {
                continue;
            }

            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await CopyFileAsync(file, target, cancellationToken);
        }
    }

    private static async Task CopyFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        await using var from = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var to = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await from.CopyToAsync(to, cancellationToken);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }
}
