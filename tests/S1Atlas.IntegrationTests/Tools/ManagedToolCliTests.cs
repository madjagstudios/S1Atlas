using System.Text.Json;
using Xunit;

namespace S1Atlas.IntegrationTests.Tools;

public sealed class ManagedToolCliTests
{
    [Fact]
    public async Task ToolsStatus_WhenNotInstalled_ReportsNotInstalledWithoutHttp()
    {
        await using var fixture = new ManagedToolCliFixture();

        var result = fixture.Invoke("tools", "status", "cpp2il");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Cpp2IL", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "Installation status:  NotInstalled",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "s1atlas tools install cpp2il",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal(0, fixture.RequestCount);
    }

    [Fact]
    public async Task ToolsStatusJson_WhenNotInstalled_ReturnsOneStableEnvelope()
    {
        await using var fixture = new ManagedToolCliFixture();

        var result = fixture.Invoke("tools", "status", "cpp2il", "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        AssertSuccessEnvelope(root, "tools status");
        var tool = Assert.Single(
            root.GetProperty("data").GetProperty("tools").EnumerateArray());
        Assert.Equal("cpp2il", tool.GetProperty("toolId").GetString());
        Assert.Equal("NotInstalled", tool.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, tool.GetProperty("executableSha256").ValueKind);
        Assert.Equal(0, fixture.RequestCount);
    }

    [Fact]
    public async Task ToolsInstall_DownloadsOnceAndReportsVerified()
    {
        await using var fixture = new ManagedToolCliFixture();

        var result = fixture.Invoke("tools", "install", "cpp2il");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "Cpp2IL test-version installed and verified.",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal(1, fixture.RequestCount);
        Assert.True(File.Exists(fixture.ExecutablePath));
    }

    [Fact]
    public async Task ToolsInstallJson_ReturnsVerifiedManagedPinFacts()
    {
        await using var fixture = new ManagedToolCliFixture();

        var result = fixture.Invoke(
            "tools",
            "install",
            "cpp2il",
            "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        AssertSuccessEnvelope(root, "tools install");
        var data = root.GetProperty("data");
        Assert.False(data.GetProperty("wasAlreadyVerified").GetBoolean());
        Assert.False(data.GetProperty("repaired").GetBoolean());
        var tool = data.GetProperty("tool");
        Assert.Equal("Verified", tool.GetProperty("status").GetString());
        Assert.Equal("ManagedPinned", tool.GetProperty("trustLevel").GetString());
        Assert.Equal(
            fixture.PackageSha256,
            tool.GetProperty("packageSha256").GetString());
        Assert.Equal(
            fixture.PackageSha256,
            tool.GetProperty("executableSha256").GetString());
        Assert.Equal(fixture.InstallRoot, tool.GetProperty("installRoot").GetString());
    }

    [Fact]
    public async Task ToolsInstall_WhenAlreadyVerified_IsNoOpWithoutSecondRequest()
    {
        await using var fixture = new ManagedToolCliFixture();
        Assert.Equal(
            0,
            fixture.Invoke("tools", "install", "cpp2il").ExitCode);

        var result = fixture.Invoke("tools", "install", "cpp2il");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "is already installed and verified.",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains("No work required.", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(1, fixture.RequestCount);
    }

    [Fact]
    public async Task ToolsInstall_WhenCorrupt_RequiresRepairWithoutRequest()
    {
        await using var fixture = new ManagedToolCliFixture();
        Assert.Equal(
            0,
            fixture.Invoke("tools", "install", "cpp2il").ExitCode);
        await File.WriteAllBytesAsync(
            fixture.ExecutablePath,
            [9, 9, 9, 9],
            TestContext.Current.CancellationToken);

        var result = fixture.Invoke("tools", "install", "cpp2il", "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        var root = document.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("data").ValueKind);
        Assert.Equal(
            "ToolRepairRequired",
            root.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, fixture.RequestCount);
    }

    [Fact]
    public async Task ToolsInstallRepair_QuarantinesCorruptRootAndReturnsVerified()
    {
        await using var fixture = new ManagedToolCliFixture();
        Assert.Equal(
            0,
            fixture.Invoke("tools", "install", "cpp2il").ExitCode);
        await File.WriteAllBytesAsync(
            fixture.ExecutablePath,
            [9, 9, 9, 9],
            TestContext.Current.CancellationToken);

        var result = fixture.Invoke(
            "tools",
            "install",
            "cpp2il",
            "--repair",
            "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(0, result.ExitCode);
        AssertSuccessEnvelope(document.RootElement, "tools install");
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("repaired").GetBoolean());
        var quarantinePath = data.GetProperty("quarantinePath").GetString();
        Assert.NotNull(quarantinePath);
        Assert.True(Directory.Exists(quarantinePath));
        Assert.True(File.Exists(fixture.ExecutablePath));
        Assert.Equal(2, fixture.RequestCount);
    }

    [Fact]
    public async Task ToolsStatus_WhenCorrupt_ReturnsSuccessWithCorruptState()
    {
        await using var fixture = new ManagedToolCliFixture();
        Assert.Equal(
            0,
            fixture.Invoke("tools", "install", "cpp2il").ExitCode);
        await File.WriteAllBytesAsync(
            fixture.ExecutablePath,
            [9, 9, 9, 9],
            TestContext.Current.CancellationToken);

        var result = fixture.Invoke("tools", "status", "cpp2il", "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(0, result.ExitCode);
        AssertSuccessEnvelope(document.RootElement, "tools status");
        var tool = Assert.Single(document.RootElement
            .GetProperty("data")
            .GetProperty("tools")
            .EnumerateArray());
        Assert.Equal("Corrupt", tool.GetProperty("status").GetString());
        Assert.Equal(
            "ToolExecutableChecksumMismatch",
            tool.GetProperty("diagnosticCode").GetString());
        Assert.Equal(1, fixture.RequestCount);
    }

    [Fact]
    public async Task ToolsInstall_WhenChecksumDiffers_ReturnsStructuredFailureAndNoFinalRoot()
    {
        await using var fixture = new ManagedToolCliFixture();
        fixture.WriteDefinition(new string('0', 64));

        var result = fixture.Invoke("tools", "install", "cpp2il", "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal(
            "ToolChecksumMismatch",
            document.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("data").ValueKind);
        Assert.False(Directory.Exists(fixture.InstallRoot));
        Assert.Equal(1, fixture.RequestCount);
    }

    [Fact]
    public async Task ToolsStatus_UnknownTool_ReturnsUnknownToolWithoutHttp()
    {
        await using var fixture = new ManagedToolCliFixture();

        var result = fixture.Invoke("tools", "status", "unknown", "--json");

        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal(
            "UnknownTool",
            document.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        Assert.Equal(0, fixture.RequestCount);
    }

    private static void AssertSuccessEnvelope(JsonElement root, string command)
    {
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(command, root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, root.GetProperty("data").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
    }
}
