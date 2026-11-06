using System.Security.Cryptography;
using System.Text.Json;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.TestHost;

public sealed record FixtureProject(string ApplicationId, string ProgramId, string Declaration = "PROGRAM PLC_PRG",
    string Implementation = "counter := counter + 1;", bool CompileError = false, int DelayMilliseconds = 0, string? Fault = null);

/// <summary>Childless deterministic test process. It never interprets or compiles PLC code.</summary>
public static class FixtureWorker
{
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
            switch (request.Operation)
            {
                case EngineeringOperations.Open:
                    payload["opened"] = true; payload["sessionClosedAfterOperation"] = true; break;
                case EngineeringOperations.Structure:
                    object[] objects = [new { id = fixture.ApplicationId, name = "Application", parentId = (string?)null, kind = "application" },
                        new { id = fixture.ProgramId, name = "PLC_PRG", parentId = fixture.ApplicationId, kind = "object" }];
                    payload["objects"] = objects.Take(input.MaxObjects).ToArray(); payload["truncated"] = input.MaxObjects < objects.Length; break;
                case EngineeringOperations.ReadProgram:
                    if (input.ObjectId != fixture.ProgramId) { error = "object_not_found"; break; }
                    payload["objectId"] = fixture.ProgramId; payload["language"] = "ST";
                    payload["declaration"] = fixture.Declaration; payload["implementation"] = fixture.Implementation; break;
                case EngineeringOperations.Compile:
                    if (input.ApplicationId != fixture.ApplicationId) { error = "object_not_found"; break; }
                    payload["applicationId"] = fixture.ApplicationId; payload["succeeded"] = !fixture.CompileError;
                    payload["diagnostics"] = fixture.CompileError
                        ? new object[] { new { severity = "error", code = "FIXTURE0001", message = "Simulated compilation error; no PLC compiler ran.", objectId = fixture.ProgramId, line = (int?)null, column = (int?)null } } : [];
                    if (fixture.CompileError) error = "compile_failed";
                    break;
                default: error = "invalid_operation"; break;
            }
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
