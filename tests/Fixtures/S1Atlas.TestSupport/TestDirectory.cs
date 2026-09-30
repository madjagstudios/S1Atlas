using Microsoft.Data.Sqlite;

namespace S1Atlas.TestSupport;

/// <summary>
/// Deletes per-test temp trees. Clears pooled SQLite connections first (a pooled
/// connection otherwise holds the database file until garbage collection), then
/// retries past the transient locks Windows raises while antivirus, search
/// indexing, or a just-exited process still has a file open. After the bounded
/// retry window the last error propagates: transient locks are absorbed, but a
/// persistently held directory still fails loudly.
/// </summary>
public static class TestDirectory
{
    private const int MaxAttempts = 10;
    private const int RetryDelayMilliseconds = 100;

    public static async Task DeleteTreeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return;
        }

        SqliteConnection.ClearAllPools();

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (
                (exception is IOException or UnauthorizedAccessException) &&
                attempt < MaxAttempts - 1)
            {
                await Task.Delay(RetryDelayMilliseconds, cancellationToken);
            }
        }
    }

    public static void DeleteTree(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return;
        }

        SqliteConnection.ClearAllPools();

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (
                (exception is IOException or UnauthorizedAccessException) &&
                attempt < MaxAttempts - 1)
            {
                Thread.Sleep(RetryDelayMilliseconds);
            }
        }
    }
}
