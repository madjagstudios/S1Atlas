using System.Text.Json.Nodes;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Tools;
using Xunit;

namespace S1Atlas.Extraction.Tests.Tools;

public sealed class ToolDefinitionValidatorTests
{
    [Theory]
    [InlineData("http://example.test/tool.exe")]
    [InlineData("https://user:password@example.test/tool.exe")]
    public void Deserialize_WhenSourceUrlIsNotApprovedHttpsShape_Rejects(
        string sourceUrl)
    {
        AssertInvalid(root => Package(root)["sourceUrl"] = sourceUrl);
    }

    [Theory]
    [InlineData("../unsafe")]
    [InlineData("unsafe/version")]
    [InlineData("unsafe:version")]
    public void Deserialize_WhenVersionIsNotOneSafePathSegment_Rejects(
        string version)
    {
        AssertInvalid(root => root["version"] = version);
    }

    [Fact]
    public void Deserialize_WhenExpectedSizeExceedsDownloadLimit_Rejects()
    {
        AssertInvalid(root => Package(root)["expectedSize"] = 5);
    }

    [Fact]
    public void Deserialize_WhenExecutablePathEscapesRoot_Rejects()
    {
        AssertInvalid(root =>
            Package(root)["executableRelativePath"] = "../Cpp2IL.exe");
    }

    [Fact]
    public void Deserialize_WhenProbeIdsRepeat_Rejects()
    {
        AssertInvalid(root =>
        {
            var probes = root["probes"]!.AsArray();
            probes[1]!.AsObject()["probeId"] = "help";
        });
    }

    [Fact]
    public void Deserialize_WhenArchiveFormatConflictsWithPackageKind_Rejects()
    {
        AssertInvalid(root => Package(root)["archiveFormat"] = "zip");
    }

    [Fact]
    public void Deserialize_WhenJsonContainsCommentsOrTrailingComma_Rejects()
    {
        var serializer = new ToolDefinitionSerializer();
        var openingBrace = ToolTestFixture.ValidDefinitionJson.IndexOf('{');
        var withComment = ToolTestFixture.ValidDefinitionJson.Insert(
            openingBrace + 1,
            " // comment");
        var finalBrace = ToolTestFixture.ValidDefinitionJson.LastIndexOf('}');
        var withTrailingComma = ToolTestFixture.ValidDefinitionJson.Insert(
            finalBrace,
            ",");

        var commentException = Assert.Throws<ToolOperationException>(() =>
            serializer.Deserialize(withComment, "comment.json"));
        var commaException = Assert.Throws<ToolOperationException>(() =>
            serializer.Deserialize(withTrailingComma, "trailing-comma.json"));

        Assert.Equal("ToolDefinitionInvalid", commentException.Code);
        Assert.Equal("ToolDefinitionInvalid", commaException.Code);
    }

    private static JsonObject Package(JsonObject root) =>
        root["package"]!.AsObject();

    private static void AssertInvalid(Action<JsonObject> mutate)
    {
        var root = JsonNode
            .Parse(ToolTestFixture.ValidDefinitionJson)!
            .AsObject();
        mutate(root);
        var serializer = new ToolDefinitionSerializer();

        var exception = Assert.Throws<ToolOperationException>(() =>
            serializer.Deserialize(root.ToJsonString(), "fixture.json"));

        Assert.Equal("ToolDefinitionInvalid", exception.Code);
    }
}
