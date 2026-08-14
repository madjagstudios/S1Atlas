using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class SourceSnippetReaderTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-source-snippet-" + Guid.NewGuid().ToString("N"));

    public SourceSnippetReaderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task ReadAsync_returns_the_exact_recorded_span_when_context_is_zero()
    {
        const string text = "before\n    public void Run() { } trailing\nafter\n";
        var path = await WriteAsync("exact.cs", text);
        var location = new IndexSourceLocationRecord("symbol", "file", 2, 5, 2, 26);
        var reader = new SourceSnippetReader();

        var result = await reader.ReadAsync(
            path,
            Sha256(text),
            location,
            0,
            TestContext.Current.CancellationToken);

        Assert.Equal("public void Run() { }", result.Text);
        Assert.Equal(0, result.ContextBefore);
        Assert.Equal(0, result.ContextAfter);
    }

    [Fact]
    public async Task ReadAsync_returns_five_lines_of_context_around_a_multiline_span()
    {
        var lines = Enumerable.Range(1, 14).Select(index => "line-" + index).ToArray();
        var text = string.Join("\n", lines) + "\n";
        var path = await WriteAsync("context.cs", text);
        var location = new IndexSourceLocationRecord("symbol", "file", 7, 1, 8, 7);
        var reader = new SourceSnippetReader();

        var result = await reader.ReadAsync(
            path,
            Sha256(text),
            location,
            5,
            TestContext.Current.CancellationToken);

        Assert.Equal(string.Join("\n", lines.Skip(1).Take(12)), result.Text);
        Assert.Equal(5, result.ContextBefore);
        Assert.Equal(5, result.ContextAfter);
    }

    [Theory]
    [InlineData(1, 2, 0, 2)]
    [InlineData(5, 5, 2, 0)]
    public async Task ReadAsync_clips_context_at_file_boundaries(
        int startLine,
        int endLine,
        int expectedBefore,
        int expectedAfter)
    {
        const string text = "one\ntwo\nthree\nfour\nfive";
        var path = await WriteAsync("clip-" + startLine + ".cs", text);
        var location = new IndexSourceLocationRecord("symbol", "file", startLine, 1, endLine, 5);
        var reader = new SourceSnippetReader();

        var result = await reader.ReadAsync(
            path,
            Sha256(text),
            location,
            2,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedBefore, result.ContextBefore);
        Assert.Equal(expectedAfter, result.ContextAfter);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task ReadAsync_handles_lf_and_crlf_without_leaking_carriage_returns(string newline)
    {
        var text = "alpha" + newline + "beta" + newline + "gamma" + newline;
        var path = await WriteAsync("newline-" + newline.Length + ".cs", text);
        var location = new IndexSourceLocationRecord("symbol", "file", 2, 1, 2, 5);
        var reader = new SourceSnippetReader();

        var result = await reader.ReadAsync(
            path,
            Sha256(text),
            location,
            1,
            TestContext.Current.CancellationToken);

        Assert.Equal("alpha\nbeta\ngamma", result.Text);
        Assert.DoesNotContain('\r', result.Text);
    }

    [Fact]
    public async Task ReadAsync_rejects_negative_context()
    {
        const string text = "alpha";
        var path = await WriteAsync("negative.cs", text);
        var reader = new SourceSnippetReader();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadAsync(
            path,
            Sha256(text),
            new IndexSourceLocationRecord("symbol", "file", 1, 1, 1, 6),
            -1,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_reports_a_missing_source_file()
    {
        var reader = new SourceSnippetReader();

        await Assert.ThrowsAsync<FileNotFoundException>(() => reader.ReadAsync(
            Path.Combine(_root, "missing.cs"),
            new string('0', 64),
            new IndexSourceLocationRecord("symbol", "file", 1, 1, 1, 1),
            0,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_rejects_content_when_the_recorded_hash_does_not_match()
    {
        const string text = "alpha";
        var path = await WriteAsync("mismatch.cs", text);
        var reader = new SourceSnippetReader();

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(
            path,
            new string('0', 64),
            new IndexSourceLocationRecord("symbol", "file", 1, 1, 1, 6),
            0,
            TestContext.Current.CancellationToken));
    }

    private async Task<string> WriteAsync(string name, string text)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), TestContext.Current.CancellationToken);
        return path;
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
