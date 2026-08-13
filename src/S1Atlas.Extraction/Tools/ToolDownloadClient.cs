using System.Net;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Tools;

internal sealed class ToolDownloadClient
{
    private const int BufferSize = 64 * 1024;

    private readonly HttpClient _httpClient;

    public ToolDownloadClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task DownloadAsync(
        Uri sourceUri,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ValidateHttpsUri(sourceUri, "source");

        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var destinationCreated = false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is null)
            {
                throw new ToolOperationException(
                    "ToolDownloadFailed",
                    "The tool download response did not identify its final request URI.");
            }

            ValidateHttpsUri(finalUri, "final");

            if (!response.IsSuccessStatusCode)
            {
                throw new ToolOperationException(
                    "ToolDownloadFailed",
                    $"The tool download failed with HTTP status " +
                    $"{(int)response.StatusCode} ({response.ReasonPhrase ?? "unknown"}).");
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 0 && contentLength.Value > maximumBytes)
            {
                throw new ToolOperationException(
                    "ToolSizeMismatch",
                    $"The tool download declared {contentLength.Value} bytes, " +
                    $"which exceeds the {maximumBytes}-byte safety limit.");
            }

            var directory = Path.GetDirectoryName(fullDestinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var source = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            await using var destination = new FileStream(
                fullDestinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            destinationCreated = true;

            var buffer = new byte[BufferSize];
            long totalBytes = 0;
            while (true)
            {
                var bytesRead = await source.ReadAsync(
                    buffer.AsMemory(),
                    cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                if (bytesRead > maximumBytes - totalBytes)
                {
                    throw new ToolOperationException(
                        "ToolSizeMismatch",
                        $"The tool download exceeded the {maximumBytes}-byte safety limit.");
                }

                await destination.WriteAsync(
                    buffer.AsMemory(0, bytesRead),
                    cancellationToken);
                totalBytes += bytesRead;
            }

            await destination.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (destinationCreated)
            {
                TryDelete(fullDestinationPath);
            }

            throw;
        }
        catch (ToolOperationException)
        {
            if (destinationCreated)
            {
                TryDelete(fullDestinationPath);
            }

            throw;
        }
        catch (Exception exception) when (IsExpectedDownloadFailure(exception))
        {
            if (destinationCreated)
            {
                TryDelete(fullDestinationPath);
            }

            throw new ToolOperationException(
                "ToolDownloadFailed",
                $"The tool package could not be downloaded: {exception.Message}",
                exception);
        }
    }

    private static void ValidateHttpsUri(Uri uri, string description)
    {
        if (!uri.IsAbsoluteUri ||
            !string.Equals(
                uri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ToolOperationException(
                "ToolDownloadFailed",
                $"The {description} tool download URI must be absolute, " +
                "credential-free HTTPS.");
        }
    }

    private static bool IsExpectedDownloadFailure(Exception exception) =>
        exception is HttpRequestException or
            IOException or
            UnauthorizedAccessException or
            WebException or
            NotSupportedException;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
