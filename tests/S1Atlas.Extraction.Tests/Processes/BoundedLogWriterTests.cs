using System.Text;
using S1Atlas.Extraction.Processes;
using Xunit;

namespace S1Atlas.Extraction.Tests.Processes;

public sealed class BoundedLogWriterTests : IDisposable
{
    private readonly string _temporaryDirectory = CreateTemporaryDirectory();

    [Fact]
    public async Task DrainAsync_WhenSourceExceedsCap_ConsumesSourceAndAppendsOneMarker()
    {
        var payload = Enumerable.Repeat((byte)'x', 4096).ToArray();
        await using var source = new MemoryStream(payload);
        var path = LogPath();

        var result = await new BoundedLogWriter().DrainAsync(
            source,
            path,
            maximumRetainedBytes: 1024,
            TestContext.Current.CancellationToken);

        Assert.True(result.Truncated);
        Assert.Equal(3072, result.DiscardedBytes);
        Assert.Equal(1024, result.RetainedBytes);
        Assert.Equal(source.Length, source.Position);
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        var marker =
            $"[S1Atlas log truncated; discarded {result.DiscardedBytes} bytes]" +
            Environment.NewLine;
        Assert.EndsWith(marker, text, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(text, marker));
    }

    [Fact]
    public async Task DrainAsync_CountsEncodedBytesRatherThanUtf16Characters()
    {
        var payload = Encoding.UTF8.GetBytes("ééé");
        await using var source = new MemoryStream(payload);

        var result = await new BoundedLogWriter().DrainAsync(
            source,
            LogPath(),
            maximumRetainedBytes: 3,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result.RetainedBytes);
        Assert.Equal(3, result.DiscardedBytes);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task DrainAsync_WhenSourceIsEmpty_CreatesEmptyLog()
    {
        await using var source = new MemoryStream();
        var path = LogPath();

        var result = await new BoundedLogWriter().DrainAsync(
            source,
            path,
            maximumRetainedBytes: 10,
            TestContext.Current.CancellationToken);

        Assert.Equal(new BoundedLogResult(path, 0, 0, Truncated: false), result);
        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task DrainAsync_WhenCapIsNotPositive_RejectsWithoutCreatingLog(long cap)
    {
        var path = LogPath();
        await using var source = new MemoryStream([1]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new BoundedLogWriter().DrainAsync(
                source,
                path,
                cap,
                TestContext.Current.CancellationToken));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DrainAsync_WhenLogExists_NeverOverwritesIt()
    {
        var path = LogPath();
        await File.WriteAllTextAsync(
            path,
            "existing",
            TestContext.Current.CancellationToken);
        await using var source = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<IOException>(() =>
            new BoundedLogWriter().DrainAsync(
                source,
                path,
                10,
                TestContext.Current.CancellationToken));

        Assert.Equal(
            "existing",
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DrainAsync_WhenOwnedLogWriteFails_RemovesPartialLog()
    {
        var path = LogPath();
        await using var source = new MemoryStream(Enumerable.Repeat((byte)1, 64 * 1024).ToArray());
        var writer = new BoundedLogWriter(
            outputPath => new FailAfterFirstWriteStream(outputPath));

        await Assert.ThrowsAsync<IOException>(() =>
            writer.DrainAsync(
                source,
                path,
                64 * 1024,
                TestContext.Current.CancellationToken));

        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private string LogPath() => Path.Combine(_temporaryDirectory, $"{Guid.NewGuid():N}.log");

    private static int CountOccurrences(string value, string substring) =>
        value.Split(substring, StringSplitOptions.None).Length - 1;

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"s1atlas-log-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FailAfterFirstWriteStream : Stream
    {
        private readonly FileStream _inner;
        private bool _wrote;

        public FailAfterFirstWriteStream(string path)
        {
            _inner = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_wrote)
            {
                throw new IOException("Injected log write failure.");
            }

            _wrote = true;
            await _inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
