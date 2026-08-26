using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Cpp2Il;
using S1Atlas.Extraction.Inputs;
using S1Atlas.Extraction.Tools;

namespace S1Atlas.Extraction;

public sealed partial class ExtractionOrchestrator
{
    private static OutputFacts InspectOwnedOutput(OwnedAttemptPaths paths)
    {
        if (!Directory.Exists(paths.OutputRoot))
        {
            throw FilesystemFailure(
                paths.AttemptId,
                "The owned Cpp2IL output directory is missing.");
        }

        EnsureSafeStaging(paths);
        if (!PathSafety.IsNormalDirectory(paths.OutputRoot) ||
            !OwnedAttemptPaths.IsSameOrDescendant(
                paths.StagingRoot,
                paths.OutputRoot))
        {
            throw FilesystemFailure(
                paths.AttemptId,
                "The owned Cpp2IL output directory is unsafe.");
        }

        var fileCount = 0;
        long byteCount = 0;
        var pending = new Stack<string>();
        pending.Push(paths.OutputRoot);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw FilesystemFailure(
                        paths.AttemptId,
                        "Cpp2IL output contains a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    fileCount = checked(fileCount + 1);
                    byteCount = checked(byteCount + new FileInfo(entry).Length);
                }
            }
        }

        return new OutputFacts(fileCount, byteCount);
    }

    private static void EnsureSafeStaging(OwnedAttemptPaths paths)
    {
        if (!Directory.Exists(paths.StagingRoot) ||
            !PathSafety.IsNormalDirectory(paths.StagingRoot) ||
            !OwnedAttemptPaths.PathsEqualParent(paths.StagingRoot, paths.AttemptId))
        {
            throw FilesystemFailure(
                paths.AttemptId,
                "The extraction staging directory is missing or unsafe.");
        }
    }

    private static void DeleteOnlyEmptyOwnedStaging(OwnedAttemptPaths paths)
    {
        if (!Directory.Exists(paths.StagingRoot))
        {
            return;
        }

        try
        {
            EnsureSafeStaging(paths);
            DeleteDirectoryIfEmpty(paths.WorkingRoot);
            DeleteDirectoryIfEmpty(paths.StagingLogsRoot);
            DeleteDirectoryIfEmpty(paths.OutputRoot);
            DeleteDirectoryIfEmpty(paths.StagingRoot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or InvalidOperationException or
                ExtractionOperationException)
        {
        }
    }

    private static void DeleteDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path, recursive: false);
        }
    }
}
