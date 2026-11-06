using PlcMcp.Execution;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.TestHost;

public sealed class FixtureExecutor(string directory, string? workerAssembly = null) : IEngineeringExecutor
{
    public string ExecutionMode => "fixture";
    public OperationError? Unavailability => null;
    public Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        var runner = new ProcessWorker(directory, (input, output) => new(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            [workerAssembly ?? typeof(FixtureExecutor).Assembly.Location, "--fixture-worker", input, output]), childlessTestWorker: true);
        return runner.RunAsync(request, cancellationToken);
    }
}
