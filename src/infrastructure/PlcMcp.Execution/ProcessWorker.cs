using System.Diagnostics;
using PlcMcp.Services.Contracts.Configuration;
using System.Text.Json;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.Execution;

public sealed record ProcessInvocation(string FileName, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>One request per owned process. Private JSON files separate protocol from IDE output.</summary>
public sealed class ProcessWorker(string workRoot, Func<string, string, ProcessInvocation> createInvocation,
    bool childlessTestWorker = false, int maxExchangeBytes = PlcMcp.Services.Contracts.Exchange.ExchangeOperations.DefaultMaxXmlBytes)
{
    public async Task<WorkerResponse> RunAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        if (request.ProtocolVersion != 1 || request.JobId == Guid.Empty || request.RequestId == Guid.Empty
            || request.WorkspaceId == Guid.Empty || !(EngineeringOperations.Contains(request.Operation) || request.Operation == EditOperations.VerifyProject
                || PlcMcp.Services.Contracts.Exchange.ExchangeOperations.IsWrite(request.Operation) || ConfigurationOperations.IsWrite(request.Operation) || EditOperations.CreatesObject(request.Operation) || EditOperations.UpdatesObject(request.Operation))
            || request.ExecutionMode is not ("real" or "fixture") || request.Payload.ValueKind != JsonValueKind.Object)
            return WorkerJson.Failed(request, "invalid_worker_request");
        if (!EngineeringOperations.Contains(request.Operation) && (!request.Payload.TryGetProperty("candidate", out var candidate) || candidate.ValueKind != JsonValueKind.True))
            return WorkerJson.Failed(request, "managed_candidate_required");
        var remaining = request.DeadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return WorkerJson.Failed(request, "deadline_expired");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : remaining);
        var directory = Path.Combine(Path.GetFullPath(workRoot), request.JobId.ToString("N"), Guid.NewGuid().ToString("N"));
        var requestPath = Path.Combine(directory, "request.json");
        var responsePath = Path.Combine(directory, "result.json");
        using var process = new Process();
        var started = false;
        Task? stdout = null, stderr = null;
        try
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var transmitted = await WorkerExchangeFiles.PrepareRequestAsync(request, directory, maxExchangeBytes, deadline.Token);
            var json = JsonSerializer.SerializeToUtf8Bytes(transmitted, WorkerJson.Options);
            if (json.Length > WorkerJson.MaxRequestBytes) return WorkerJson.Failed(request, "request_too_large");
            await File.WriteAllBytesAsync(requestPath, json, deadline.Token);
            var invocation = createInvocation(requestPath, responsePath);
            process.StartInfo = new ProcessStartInfo(invocation.FileName)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in invocation.Arguments) process.StartInfo.ArgumentList.Add(arg);
            if (invocation.Environment is not null)
                foreach (var pair in invocation.Environment) process.StartInfo.Environment[pair.Key] = pair.Value;
            process.StartInfo.Environment["PLCMCP_EXCHANGE_MAX_BYTES"] = maxExchangeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            cancellationToken.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started) return WorkerJson.Failed(request, "worker_start_failed");
            // Drain with fixed memory; raw IDE output is not returned to models or logged.
            stdout = DrainAsync(process.StandardOutput);
            stderr = DrainAsync(process.StandardError);
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2), deadline.Token);
            if (process.ExitCode != 0) return WorkerJson.Unknown(request, "worker_exited_without_result");
            return await ReadResponseAsync(request, responsePath, maxExchangeBytes);
        }
        catch (OperationCanceledException)
        {
            if (!started) return new(1, request.RequestId, request.JobId, cancellationToken.IsCancellationRequested ? "cancelled" : "failed",
                cancellationToken.IsCancellationRequested ? "cancelled_before_execution" : "deadline_expired", request.ExecutionMode, null);
            var stopped = await StopOwnedProcessAsync(process);
            if (stopped && childlessTestWorker && request.ExecutionMode == "fixture" && cancellationToken.IsCancellationRequested)
                return new(1, request.RequestId, request.JobId, "cancelled", "executor_confirmed_cancellation", request.ExecutionMode, null);
            // Killing a parent does not prove that a vendor IDE's descendant or delegated session stopped.
            return WorkerJson.Unknown(request, cancellationToken.IsCancellationRequested ? "cancellation_unconfirmed" : "execution_timeout");
        }
        catch (ProjectEditException error)
        { return started ? WorkerJson.Unknown(request, error.Code) : WorkerJson.Failed(request, error.Code); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or JsonException or InvalidOperationException or TimeoutException or ArgumentException)
        {
            if (started) await StopOwnedProcessAsync(process);
            return started ? WorkerJson.Unknown(request, "worker_protocol_error") : WorkerJson.Failed(request, "worker_start_failed");
        }
        finally
        {
            if (started && !process.HasExited) await StopOwnedProcessAsync(process);
            if (stdout is not null && stderr is not null)
            {
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception error) when (error is IOException or ObjectDisposedException or TimeoutException) { }
            }
        }
    }

    private static async Task<WorkerResponse> ReadResponseAsync(WorkerRequest request, string path, int maxExchangeBytes)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return WorkerJson.Unknown(request, "worker_result_missing");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > WorkerJson.MaxResponseBytes) return WorkerJson.Unknown(request, "worker_result_size_invalid");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes);
        if (stream.ReadByte() != -1) return WorkerJson.Unknown(request, "worker_result_size_invalid");
        var response = JsonSerializer.Deserialize<WorkerResponse>(bytes, WorkerJson.Options);
        if (response is not null) response = await WorkerExchangeFiles.ReadResponseAsync(response, Path.GetDirectoryName(path)!, maxExchangeBytes, CancellationToken.None);
        return response is not null && WorkerJson.Matches(request, response) && WorkerResultValidation.IsValid(request, response)
            ? response : WorkerJson.Unknown(request, "worker_protocol_error");
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer) != 0) { }
    }
    private static async Task<bool> StopOwnedProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            return process.HasExited;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { return false; }
    }
}
