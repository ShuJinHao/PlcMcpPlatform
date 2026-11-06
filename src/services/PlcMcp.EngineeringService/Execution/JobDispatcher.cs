using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.EngineeringService.Execution;

/// <summary>One execution slot. The store is authoritative; signals are optional wake-ups.</summary>
public sealed class JobDispatcher(IJobStore store, IEngineeringExecutor executor)
{
    public async Task<bool> RunNextAsync(CancellationToken stoppingToken)
    {
        var claimed = await store.ClaimAsync(stoppingToken);
        if (claimed is null) return false;
        var request = claimed.Request;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task<WorkerResponse> execution;
        try { execution = executor.ExecuteAsync(request, cancel.Token); }
        catch { await store.FinishAsync(WorkerJson.Unknown(request), CancellationToken.None); return true; }
        try
        {
            while (!execution.IsCompleted)
            {
                await Task.WhenAny(execution, Task.Delay(100, CancellationToken.None));
                if (!execution.IsCompleted && (await store.GetAsync(request.JobId, CancellationToken.None))?.Status == "CancelRequested")
                    await cancel.CancelAsync();
            }
            var response = await execution;
            await store.FinishAsync(WorkerJson.Matches(request, response) ? response : WorkerJson.Unknown(request, "worker_protocol_error"), CancellationToken.None);
        }
        catch
        {
            // A cancellation exception alone is not evidence that the IDE stopped.
            await cancel.CancelAsync();
            try { await execution.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* The slot is quarantined below even if termination cannot be confirmed. */ }
            await store.FinishAsync(WorkerJson.Unknown(request), CancellationToken.None);
        }
        return true;
    }
}
