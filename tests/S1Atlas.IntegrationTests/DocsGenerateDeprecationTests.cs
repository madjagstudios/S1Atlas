using S1Atlas.Cli;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.IntegrationTests;

public sealed class DocsGenerateDeprecationTests
{
    [Fact]
    public void Generate_WritesDeprecationNoticeToStderr()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "s1atlas-docs-deprecated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var application = new CliApplication(root, "0.1.0-test");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = application.Invoke(
                ["docs", "generate"],
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Contains("deprecated", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("s1atlas serve", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            TestDirectory.DeleteTree(root);
        }
    }

    [Fact]
    public void Generate_HelpSaysDeprecated()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "s1atlas-docs-deprecated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var application = new CliApplication(root, "0.1.0-test");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = application.Invoke(
                ["docs", "generate", "--help"],
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains("eprecated", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            TestDirectory.DeleteTree(root);
        }
    }
}
