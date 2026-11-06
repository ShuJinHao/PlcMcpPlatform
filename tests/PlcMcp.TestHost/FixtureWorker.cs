using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.TestHost;

public sealed record FixtureProject(string ApplicationId, string ProgramId, string Declaration = "PROGRAM PLC_PRG",
    string Implementation = "counter := counter + 1;", bool CompileError = false, int DelayMilliseconds = 0, string? Fault = null, FixtureObject[]? Objects = null, JsonObject? Configuration = null);
public sealed record FixtureObject(string Id, string Name, string Kind, string Declaration, string? Implementation, string? Language = null);

/// <summary>Childless deterministic test process. It never interprets or compiles PLC code.</summary>
public static class FixtureWorker
{
    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    public static async Task<int> RunAsync(string inputPath, string outputPath)
    {
        var request = JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync(inputPath), WorkerJson.Options)!;
        if (request.ExecutionMode != "fixture" || request.ProtocolVersion != 1) return 2;
        var input = request.Payload.Deserialize<EngineeringInput>(WorkerJson.Options)!;
        var bytes = await File.ReadAllBytesAsync(input.ProjectPath);
        var fixture = JsonSerializer.Deserialize<FixtureProject>(bytes, WorkerJson.Options)!;
        var revision = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (fixture.DelayMilliseconds > 0) await Task.Delay(Math.Min(fixture.DelayMilliseconds, 30000));
        if (fixture.Fault == "exit") return 7;
        if (fixture.Fault == "malformed") { await File.WriteAllTextAsync(outputPath, "not-json"); return 0; }
        if (fixture.Fault == "oversized") { await File.WriteAllTextAsync(outputPath, new string('x', WorkerJson.MaxResponseBytes + 1)); return 0; }
        if (fixture.Fault == "noise") { Console.Write(new string('n', 100000)); Console.Error.Write(new string('e', 100000)); }
        WorkerResponse response;
        if (revision != request.ExpectedRevision) response = WorkerJson.Failed(request, "workspace_revision_conflict");
        else
        {
            var payload = new Dictionary<string, object?> { ["revision"] = revision, ["revisionScope"] = "project_file_sha256", ["simulated"] = true };
            string? error = null;
            var nodes = fixture.Objects ?? [new(fixture.ProgramId, "PLC_PRG", "pou", fixture.Declaration, fixture.Implementation, "ST")];
            try
            {
            switch (request.Operation)
            {
                case var op when ConfigurationOperations.IsRead(op):
                    var configRead = new FixtureConfiguration(fixture);
                    payload["configuration"] = new { operation = op, objectId = input.ObjectId, data = configRead.Read(op, input.ObjectId, input.Query, input.MaxObjects) }; break;
                case var op when ConfigurationOperations.IsWrite(op):
                    if (!input.Candidate || input.Edit is null) { error = "managed_candidate_required"; break; }
                    var config = new FixtureConfiguration(fixture, input.Requirements);
                    if (input.CurrentDependencies is not null && JsonSerializer.Serialize(config.Dependencies(), WorkerJson.Options) != JsonSerializer.Serialize(input.CurrentDependencies, WorkerJson.Options))
                    { error = "dependency_snapshot_conflict"; break; }
                    var mutation = config.Apply(op, input.Edit);
                    if (fixture.Fault == "save_failure") { error = "save_failed"; break; }
                    await File.WriteAllTextAsync(input.ProjectPath, JsonSerializer.Serialize(config.Save(), WorkerJson.Options));
                    if (fixture.Fault == "after_save_exit") return 9;
                    var readProject = JsonSerializer.Deserialize<FixtureProject>(await File.ReadAllTextAsync(input.ProjectPath), WorkerJson.Options)!;
                    var reopenConfig = new FixtureConfiguration(readProject, input.Requirements);
                    var actual = reopenConfig.Proof(op, mutation.Id, input.Edit, mutation.Data);
                    if (fixture.Fault == "reopen_mismatch" || !JsonNode.DeepEquals(actual, mutation.Data)) { error = "reopen_readback_mismatch"; break; }
                    payload["configuration"] = new { operation = op, objectId = mutation.Id, data = actual };
                    payload["dependencySnapshot"] = reopenConfig.Dependencies();
                    payload["baseRevision"] = revision;
                    payload["revision"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(input.ProjectPath))).ToLowerInvariant();
                    payload["dependencies"] = new { controllerMatched = true, libraryReferencesMatched = true, installedDependenciesVerified = false, simulated = true };
                    payload["saved"] = fixture.Fault != "false_save"; payload["reopened"] = true; payload["readBackVerified"] = true; payload["sessionClosedAfterOperation"] = true;
                    break;
                case EngineeringOperations.Open:
                    payload["opened"] = true; payload["sessionClosedAfterOperation"] = true; break;
                case EngineeringOperations.Structure:
                    var objects = fixture.Configuration is null
                        ? new object[] { new { id = fixture.ApplicationId, name = "Application", parentId = (string?)null, kind = "application" } }
                            .Concat(nodes.Select(n => (object)new { id = n.Id, name = n.Name, parentId = fixture.ApplicationId, kind = n.Kind })).ToArray()
                        : new FixtureConfiguration(fixture).Structure().Cast<object>().ToArray();
                    payload["objects"] = objects.Take(input.MaxObjects).ToArray(); payload["truncated"] = input.MaxObjects < objects.Length; break;
                case EngineeringOperations.ReadProgram:
                    var node = nodes.SingleOrDefault(n => n.Id == input.ObjectId);
                    if (node is null) { error = "object_not_found"; break; }
                    if (node.Implementation is null) { error = "textual_source_unavailable"; break; }
                    payload["objectId"] = node.Id; payload["language"] = node.Language;
                    payload["declaration"] = node.Declaration; payload["implementation"] = node.Implementation; break;
                case EngineeringOperations.ReadGvl:
                case EngineeringOperations.ReadDut:
                    var declarationNode = nodes.SingleOrDefault(n => n.Id == input.ObjectId);
                    if (declarationNode is null) { error = "object_not_found"; break; }
                    if (declarationNode.Kind != request.Operation.Split('.')[0]) { error = "object_kind_mismatch"; break; }
                    payload["objectId"] = declarationNode.Id; payload["kind"] = declarationNode.Kind; payload["declaration"] = declarationNode.Declaration; break;
                case EngineeringOperations.Compile:
                    if (input.ApplicationId != fixture.ApplicationId) { error = "object_not_found"; break; }
                    var errorNode = nodes.FirstOrDefault(n => n.Implementation?.Contains("FIXTURE_ERROR", StringComparison.Ordinal) == true);
                    var compileError = fixture.CompileError || errorNode is not null;
                    payload["applicationId"] = fixture.ApplicationId; payload["succeeded"] = !compileError;
                    payload["diagnostics"] = compileError
                        ? new object[] { new { severity = "error", code = "FIXTURE0001", message = "Simulated compilation error; no PLC compiler ran.", objectId = errorNode?.Id ?? fixture.ProgramId, line = (int?)null, column = (int?)null } } : [];
                    if (compileError) error = "compile_failed";
                    break;
                default:
                    if (!input.Candidate || input.Edit is null || !(EditOperations.Contains(request.Operation) || request.Operation == EditOperations.VerifyProject)) { error = "invalid_operation"; break; }
                    var edit = input.Edit;
                    FixtureObject? edited = null;
                    var list = nodes.ToList();
                    if (EditOperations.CreatesObject(request.Operation))
                    {
                        if (edit.ParentId != fixture.ApplicationId) { error = "object_not_found"; break; }
                        if (list.Any(n => string.Equals(n.Name, edit.Name, StringComparison.OrdinalIgnoreCase))) { error = "object_name_conflict"; break; }
                        var kind = request.Operation.Split('.')[0];
                        edited = new(Guid.NewGuid().ToString(), edit.Name!, kind, Normalize(edit.Declaration!),
                            kind == "pou" && edit.Language is "ST" or "IL" ? Normalize(edit.Implementation ?? "") : null, edit.Language);
                        list.Add(edited);
                    }
                    else if (EditOperations.UpdatesObject(request.Operation))
                    {
                        var index = list.FindIndex(n => n.Id == edit.ObjectId);
                        if (index < 0) { error = "object_not_found"; break; }
                        var existing = list[index];
                        if (existing.Kind != request.Operation.Split('.')[0]) { error = "object_kind_mismatch"; break; }
                        if (existing.Implementation is null && edit.Implementation is not null) { error = "textual_implementation_unavailable"; break; }
                        edited = existing with { Declaration = edit.Declaration is null ? existing.Declaration : Normalize(edit.Declaration),
                            Implementation = edit.Implementation is null ? existing.Implementation : Normalize(edit.Implementation) };
                        list[index] = edited;
                    }
                    if (fixture.Fault == "save_failure") { error = "save_failed"; break; }
                    var legacyState = new FixtureConfiguration(fixture, input.Requirements);
                    if (input.CurrentDependencies is not null && JsonSerializer.Serialize(legacyState.Dependencies(), WorkerJson.Options) != JsonSerializer.Serialize(input.CurrentDependencies, WorkerJson.Options))
                    { error = "dependency_snapshot_conflict"; break; }
                    var saved = new FixtureConfiguration(fixture with { Objects = list.ToArray() }, input.Requirements).Save();
                    await File.WriteAllTextAsync(input.ProjectPath, JsonSerializer.Serialize(saved, WorkerJson.Options));
                    if (fixture.Fault == "after_save_exit") return 9;
                    var reopened = JsonSerializer.Deserialize<FixtureProject>(await File.ReadAllTextAsync(input.ProjectPath), WorkerJson.Options)!;
                    if (fixture.Fault == "reopen_mismatch" || edited is not null && reopened.Objects!.Single(n => n.Id == edited.Id) != edited) { error = "reopen_readback_mismatch"; break; }
                    payload["baseRevision"] = revision;
                    payload["revision"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(input.ProjectPath))).ToLowerInvariant();
                    payload["dependencies"] = new { controllerMatched = true, libraryReferencesMatched = true, installedDependenciesVerified = false, simulated = true };
                    payload["dependencySnapshot"] = new FixtureConfiguration(reopened, input.Requirements).Dependencies();
                    payload["saved"] = fixture.Fault != "false_save"; payload["reopened"] = true; payload["readBackVerified"] = true; payload["sessionClosedAfterOperation"] = true;
                    if (edited is not null)
                    { payload["objectId"] = edited.Id; payload["name"] = edited.Name; payload["declaration"] = edited.Declaration; payload["implementation"] = edited.Implementation; }
                    break;
            }
            }
            catch (ProjectEditException failure) { error = failure.Code; }
            response = new(1, request.RequestId, request.JobId, error is null ? "succeeded" : "failed", error, "fixture", JsonSerializer.SerializeToElement(payload, WorkerJson.Options));
        }
        if (fixture.Fault == "wrong_id") response = response with { RequestId = Guid.NewGuid() };
        if (fixture.Fault == "wrong_mode") response = response with { ExecutionMode = "real" };
        var temporary = outputPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(response, WorkerJson.Options));
        File.Move(temporary, outputPath);
        return 0;
    }
}
