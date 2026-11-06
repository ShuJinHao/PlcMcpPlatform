using PlcMcp.EngineeringService.Execution;
using PlcMcp.Execution;

namespace PlcMcp.McpHost;

public sealed class ExecutionService(JobDispatcher dispatcher, ExecutionSignal signal) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
                if (!await dispatcher.RunNextAsync(stoppingToken)) await signal.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
