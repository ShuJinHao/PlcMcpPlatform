using PlcMcp.Execution;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.Inovance;

public sealed record InovanceOptions(string ExecutablePath, string Profile, string ScriptPath, string WorkDirectory);

public sealed class InovanceExecutor(InovanceOptions options, IInovanceLaunchProfile? launchProfile = null) : IEngineeringExecutor
{
    public string ExecutionMode => "real";
    public OperationError? Unavailability => !OperatingSystem.IsWindows() || !File.Exists(options.ExecutablePath)
        ? new("ide_unavailable", "A configured Windows InoProShop installation is required for real execution.")
        : new("capability_unverified", "This IDE version has not passed integration validation; production tools remain disabled.");

    // Invoked only by an explicit integration harness until per-version evidence is implemented.
    public Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(options.ExecutablePath) || !File.Exists(options.ScriptPath)
            || string.IsNullOrWhiteSpace(options.Profile) || request.ExecutionMode != ExecutionMode)
            return Task.FromResult(WorkerJson.Failed(request, "ide_unavailable"));
        var profile = launchProfile ?? new ScriptEngine35LaunchProfile(options);
        var worker = new ProcessWorker(options.WorkDirectory, profile.CreateInvocation);
        return worker.RunAsync(request, cancellationToken);
    }
}
