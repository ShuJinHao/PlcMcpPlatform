using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using PlcMcp.EngineeringService.Exchange;
using PlcMcp.McpHost;
using PlcMcp.Services.Contracts.Workers;
using Xunit;

namespace PlcMcp.Tests;

public sealed class McpTextPresentationTests
{
    [Fact]
    public void Model_text_preserves_exact_Unicode_paths_and_structured_semantics_without_changing_worker_bytes_or_hashes()
    {
        const string path = "/Users/example/产线系统架构升级/项目/project.fixture.json";
        var graph = GraphDeclarationTests.Sample(); var semantic = PlcOpenGraphCodec.SemanticHash(graph);
        var payload = new { projectPath = path, html = "<b>文字 & 'quote'</b>", revision = "m2:unchanged", sha256 = semantic };
        var workerBytes = JsonSerializer.SerializeToUtf8Bytes(payload, WorkerJson.Options);
        var structured = JsonSerializer.SerializeToElement(payload, WorkerJson.Options);
        var raw = structured.GetRawText(); Assert.Contains("\\u4EA7", raw);
        var reply = new CallToolResult { IsError = false, StructuredContent = structured, Content = [new TextContentBlock { Text = raw }] };

        Assert.Same(reply, McpTextPresentation.Present(reply));
        var text = Assert.IsType<TextContentBlock>(Assert.Single(reply.Content)).Text;
        Assert.Contains(path, text); Assert.DoesNotContain("\\u4EA7", text);
        Assert.Contains("\\u003C", text); Assert.Contains("\\u003E", text); Assert.Contains("\\u0026", text); Assert.Contains("\\u0027", text);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(raw)));
        Assert.Equal(raw, reply.StructuredContent!.Value.GetRawText());
        Assert.Equal(workerBytes, JsonSerializer.SerializeToUtf8Bytes(payload, WorkerJson.Options));
        Assert.Equal(semantic, PlcOpenGraphCodec.SemanticHash(graph)); Assert.False(reply.IsError);
    }

    [Fact]
    public void Text_only_and_multipart_results_are_not_discarded_and_error_status_is_preserved()
    {
        var original = new TextContentBlock { Text = "opaque error" };
        var textOnly = new CallToolResult { IsError = true, Content = [original] };
        McpTextPresentation.Present(textOnly); Assert.Same(original, textOnly.Content[0]); Assert.True(textOnly.IsError);
        var multipart = new CallToolResult { StructuredContent = JsonSerializer.SerializeToElement(new { message = "中文" }), Content = [original, new TextContentBlock { Text = "additional evidence" }] };
        McpTextPresentation.Present(multipart); Assert.Equal(2, multipart.Content.Count); Assert.Same(original, multipart.Content[0]);
    }
}
