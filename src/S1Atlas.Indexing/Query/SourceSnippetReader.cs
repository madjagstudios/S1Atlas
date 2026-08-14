using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Query;

public sealed record SourceSnippetReadResult(
    string Text,
    int ContextBefore,
    int ContextAfter);

public sealed class SourceSnippetReader
{
    public async Task<byte[]> ReadVerifiedBytesAsync(
        string absolutePath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);

        var fullPath = Path.GetFullPath(absolutePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The indexed source file was not found.", fullPath);

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        var actualSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"The indexed source file hash does not match the recorded SHA-256 for '{fullPath}'.");

        return bytes;
    }

    public async Task<SourceSnippetReadResult> ReadAsync(
        string absolutePath,
        string expectedSha256,
        IndexSourceLocationRecord location,
        int context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (context < 0)
            throw new ArgumentOutOfRangeException(nameof(context), "Source context cannot be negative.");

        var bytes = await ReadVerifiedBytesAsync(absolutePath, expectedSha256, cancellationToken);
        var text = Encoding.UTF8.GetString(bytes)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = text.Split('\n');
        var logicalLineCount = lines.Length > 1 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        if (logicalLineCount == 0)
            throw new InvalidDataException("The indexed source file is empty.");

        var startLineIndex = location.StartLine - 1;
        var endLineIndex = (location.EndLine ?? location.StartLine) - 1;
        if (startLineIndex < 0 || startLineIndex >= logicalLineCount ||
            endLineIndex < startLineIndex || endLineIndex >= logicalLineCount)
            throw new InvalidDataException("The recorded source line span is outside the indexed source file.");

        var startColumnIndex = location.StartColumn - 1;
        var endColumnIndex = (location.EndColumn ?? location.StartColumn) - 1;
        if (startColumnIndex < 0 || startColumnIndex > lines[startLineIndex].Length ||
            endColumnIndex < 0 || endColumnIndex > lines[endLineIndex].Length ||
            (startLineIndex == endLineIndex && endColumnIndex < startColumnIndex))
            throw new InvalidDataException("The recorded source column span is outside the indexed source file.");

        var firstLineIndex = Math.Max(0, startLineIndex - context);
        var lastLineIndex = Math.Min(logicalLineCount - 1, endLineIndex + context);
        var resultLines = new List<string>(lastLineIndex - firstLineIndex + 1);

        for (var lineIndex = firstLineIndex; lineIndex <= lastLineIndex; lineIndex++)
        {
            var line = lines[lineIndex];
            if (lineIndex < startLineIndex || lineIndex > endLineIndex)
            {
                resultLines.Add(line);
                continue;
            }

            var sliceStart = lineIndex == startLineIndex ? startColumnIndex : 0;
            var sliceEnd = lineIndex == endLineIndex ? endColumnIndex : line.Length;
            resultLines.Add(line[sliceStart..sliceEnd]);
        }

        return new SourceSnippetReadResult(
            string.Join('\n', resultLines),
            startLineIndex - firstLineIndex,
            lastLineIndex - endLineIndex);
    }
}
