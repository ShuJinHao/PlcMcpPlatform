using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Execution;

namespace PlcMcp.EngineeringService.Exchange;

public static class ExchangeValidation
{
    public static bool Identity(string? id) => Guid.TryParse(id, out var value) && value != Guid.Empty;
    public static string? Read(string operation, EngineeringInput input)
    {
        if (operation == ExchangeOperations.ReadGraph) return Identity(input.ObjectId) ? null : "invalid_object_identity";
        var query = input.Exchange;
        if (query is null || !ExchangeOperations.Format(query.Format) || query.ObjectIds is not { Length: > 0 and <= 128 }
            || query.ObjectIds.Any(x => x != "project" && !Identity(x)) || query.ObjectIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != query.ObjectIds.Length
            || query.ObjectIds.Contains("project") && query.ObjectIds.Length != 1) return "invalid_exchange_selection";
        return null;
    }
    public static string? Write(string operation, EditInput edit)
    {
        var change = edit.Exchange;
        if (change is null || !ExchangeOperations.Format(change.Format) || edit.ParentId != "project" && !Identity(edit.ParentId)
            || change.ConflictPolicy is not ("reject" or "replace" or "skip") || change.ArtifactId is not { Length: 64 }
            || !change.ArtifactId.All(Uri.IsHexDigit) || change.File is not null) return "invalid_exchange_request";
        try
        {
            if (change.Xml is not null)
            {
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(change.Format + "\0" + change.Xml))).ToLowerInvariant();
            if (digest != change.ArtifactId) return "artifact_integrity_mismatch";
            _ = ExchangeXml.Inspect(change.Format, change.Xml);
            }
            if (operation == ExchangeOperations.WriteGraph)
            {
                if (change.Format != "plcopen" || edit.ParentId == "project") return "graph_parent_required";
                if (!PlcMcp.Core.Workspaces.GraphRules.Identifier(edit.Name)) return "graph_name_mismatch";
                if (change.Xml is not null && edit.Name != PlcOpenGraphCodec.Decode(change.Xml).Program.Name) return "graph_name_mismatch";
                if (edit.ObjectId is not null && (!Identity(edit.ObjectId) || change.ExpectedGraphSha256 is not { Length: 64 } || !change.ExpectedGraphSha256.All(Uri.IsHexDigit))) return "expected_graph_revision_required";
                if (edit.ObjectId is null && change.ExpectedGraphSha256 is not null) return "unexpected_graph_revision";
                if (change.ConflictPolicy != (edit.ObjectId is null ? "reject" : "replace") || change.AllowWarnings) return "invalid_graph_policy";
            }
        }
        catch (ProjectEditException error) { return error.Code; }
        return null;
    }
}
