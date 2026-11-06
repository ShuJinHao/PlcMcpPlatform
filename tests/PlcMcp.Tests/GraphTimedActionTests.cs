using PlcMcp.Core.Workspaces;
using PlcMcp.EngineeringService.Exchange;
using Xunit;

namespace PlcMcp.Tests;

public sealed class GraphTimedActionTests
{
    private static GraphicalProgram Timed(string qualifier, string? duration)
    {
        var sample = ExchangeTests.Sample("sfc-v2-branches");
        return sample with { Nodes = sample.Nodes.Select(n => n.Kind == "actionBlock" ? n with { Qualifier = qualifier, Duration = duration } : n).ToArray() };
    }
    [Theory]
    [InlineData("L")][InlineData("D")][InlineData("SD")][InlineData("DS")][InlineData("SL")]
    public void Timed_actions_roundtrip_and_affect_semantic_hash(string qualifier)
    {
        var sample = Timed(qualifier, "T#250ms");
        Assert.Null(GraphRules.Validate(sample));
        var xml = PlcOpenGraphCodec.Encode(sample);
        Assert.Contains("duration=\"T#250ms\"", xml);
        var decoded = PlcOpenGraphCodec.Decode(xml);
        Assert.All(decoded.Program.Nodes.Where(n => n.Kind == "actionBlock"), n => Assert.Equal("T#250ms", n.Duration));
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(sample), decoded.SemanticSha256);
        Assert.NotEqual(decoded.SemanticSha256, PlcOpenGraphCodec.SemanticHash(Timed(qualifier, "T#251ms")));
        Assert.Equal("graph_profile_mismatch", GraphRules.Validate(sample with { Profile = "graph-v1" }));
        Assert.Equal("graph-v2", PlcOpenGraphCodec.Decode(xml.Replace("graph-v2", "vendor")).Profile);
    }
    [Theory]
    [InlineData(null)][InlineData("T#0ms")][InlineData("T#-1s")][InlineData("T#50d")][InlineData("DelayVariable")][InlineData("T#1s500ms")][InlineData("LTIME#1s")][InlineData("T#1s\n")]
    public void Invalid_or_unrepresented_durations_are_rejected(string? duration) => Assert.Equal("invalid_sfc_duration", GraphRules.Validate(Timed("D", duration)));
    [Fact] public void Duration_cannot_be_attached_to_an_untimed_action_or_other_node()
    {
        Assert.Equal("invalid_sfc_duration", GraphRules.Validate(Timed("N", "T#1s")));
        var sample = Timed("D", "T#1s");
        Assert.Equal("graph_field_not_applicable", GraphRules.Validate(sample with { Nodes = sample.Nodes.Select(n => n.Kind == "step" ? n with { Duration = "T#1s" } : n).ToArray() }));
        Assert.True(GraphV2Rules.DurationValid("TIME#4294967295ms"));
        Assert.False(GraphV2Rules.DurationValid("T#4294967296ms"));
    }
}
