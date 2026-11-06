using PlcMcp.Core.Workspaces;
using PlcMcp.EngineeringService.Editing;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.EngineeringService.Exchange;

public sealed class ExchangeService(IExchangeArtifacts artifacts, ManagedEditingJobs edits, IPlcGraphPolicy? targetPolicy = null)
{
    public async Task<ExchangeArtifact> StageAsync(string format, string xml, CancellationToken token)
    { _ = ExchangeXml.Parse(xml, ExchangeOperations.MaxInlineXmlBytes); _ = ExchangeXml.Inspect(format, xml); return await artifacts.PutAsync(format, xml, token); }
    public async Task<ExchangeInspection> InspectAsync(string id, CancellationToken token)
    { var d = await artifacts.GetAsync(id, token); return ExchangeXml.Inspect(d.Artifact.Format, d.Xml); }
    public async Task<ExchangeChunk> ReadAsync(string id, int offset, int maxChars, CancellationToken token)
    {
        var d = await artifacts.GetAsync(id, token);
        if (offset < 0 || offset > d.Xml.Length || maxChars is < 1 or > 8192) throw new ProjectEditException("invalid_artifact_range");
        // Offsets count UTF-16 code units; never cut a surrogate pair between chunks.
        if (offset > 0 && offset < d.Xml.Length && char.IsLowSurrogate(d.Xml[offset])) throw new ProjectEditException("invalid_artifact_range");
        var count = Math.Min(maxChars, d.Xml.Length - offset);
        if (count > 0 && offset + count < d.Xml.Length && char.IsHighSurrogate(d.Xml[offset + count - 1])) count--;
        if (count == 0 && offset < d.Xml.Length) throw new ProjectEditException("artifact_chunk_too_small");
        return new(d.Artifact, offset, offset + count, offset + count == d.Xml.Length, d.Xml.Substring(offset, count));
    }
    public async Task<GeneratedGraph> GenerateAsync(GraphicalProgram program, CancellationToken token)
    {
        var xml = PlcOpenGraphCodec.Encode(program);
        return new(await artifacts.PutAsync("plcopen", xml, token), PlcOpenGraphCodec.Decode(xml));
    }
    public async Task<GraphDocument> DecodeAsync(string id, CancellationToken token)
    {
        var document = await artifacts.GetAsync(id, token);
        if (document.Artifact.Format != "plcopen") throw new ProjectEditException("graph_language_outside_profile");
        return PlcOpenGraphCodec.Decode(document.Xml);
    }
    public async Task<Result<JobView>> ImportAsync(string workspace, string revision, string parentId, string id,
        string policy, bool warnings, string key, int timeout, CancellationToken token, bool graph = false, string? objectId = null, string? expectedGraphSha256 = null)
    {
        try
        {
            var document = await artifacts.GetAsync(id, token);
            var definition = graph ? PlcOpenGraphCodec.Decode(document.Xml) : null;
            if (definition is not null && targetPolicy?.Validate(definition.Program) is { } invalid) throw new ProjectEditException(invalid);
            var change = new ExchangeChange(id, document.Artifact.Format, null, policy, warnings, expectedGraphSha256);
            return await edits.SubmitAsync(graph ? ExchangeOperations.WriteGraph : ExchangeOperations.Import,
                new(workspace, ParentId: parentId, ObjectId: objectId, Name: definition?.Program.Name, Exchange: change), key, revision, timeout, token);
        }
        catch (ProjectEditException error) { return Result<JobView>.Failure(error.Code, "Exchange input is unavailable or outside the supported profile."); }
        catch (IOException) { return Result<JobView>.Failure("artifact_unavailable", "Cannot read the exchange artifact."); }
    }
}
