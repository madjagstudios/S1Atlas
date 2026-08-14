using S1Atlas.Indexing.Source;
using Xunit;

namespace S1Atlas.Indexing.Tests.Source;

public sealed class GeneratedSourceWriterTests
{
    [Fact]
    public async Task Allows_literal_double_dot_names_but_rejects_parent_segments()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-source-writer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new GeneratedSourceWriter();
            await writer.WriteAsync(root, "notes..cs", "class Demo {}", "snapshot-1", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(root, "../escape.cs", "", "snapshot-1", TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
