using System.Text;

namespace S1Atlas.Extraction.Processes;

internal sealed class BoundedLogWriter
{
    private const int BufferSize = 16 * 1024;
    private static readonly UTF8Encoding MarkerEncoding = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    private readonly Func<string, Stream> _openOutput;

    public BoundedLogWriter()
        : this(OpenOwnedLog)
    {
    }

    internal BoundedLogWriter(Func<string, Stream> openOutput)
    {
        ArgumentNullException.ThrowIfNull(openOutput);
        _openOutput = openOutput;
    }

    public async Task<BoundedLogResult> DrainAsync(
        Stream source,
        string logPath,
        long maximumRetainedBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        if (maximumRetainedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRetainedBytes),
                maximumRetainedBytes,
                "The retained byte cap must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        Stream? output = null;
        var ownsLog = false;
        try
        {
            output = _openOutput(logPath);
            ownsLog = true;
            var buffer = new byte[BufferSize];
            long retainedBytes = 0;
            long discardedBytes = 0;

            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                var remaining = maximumRetainedBytes - retainedBytes;
                var bytesToRetain = (int)Math.Min(remaining, bytesRead);
                if (bytesToRetain > 0)
                {
                    await output.WriteAsync(
                        buffer.AsMemory(0, bytesToRetain),
                        cancellationToken);
                    retainedBytes += bytesToRetain;
                }

                discardedBytes = checked(
                    discardedBytes + bytesRead - bytesToRetain);
            }

            if (discardedBytes > 0)
            {
                var marker = MarkerEncoding.GetBytes(
                    $"[S1Atlas log truncated; discarded {discardedBytes} bytes]" +
                    Environment.NewLine);
                await output.WriteAsync(marker, cancellationToken);
            }

            await output.FlushAsync(cancellationToken);
            await output.DisposeAsync();
            output = null;

            return new BoundedLogResult(
                logPath,
                retainedBytes,
                discardedBytes,
                Truncated: discardedBytes > 0);
        }
        catch
        {
            if (output is not null)
            {
                try
                {
                    await output.DisposeAsync();
                }
                catch
                {
                }
            }

            if (ownsLog)
            {
                try
                {
                    File.Delete(logPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                }
            }

            throw;
        }
    }

    private static Stream OpenOwnedLog(string path) =>
        new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
}
