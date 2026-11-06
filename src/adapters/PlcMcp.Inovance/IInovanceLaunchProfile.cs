using PlcMcp.Execution;

namespace PlcMcp.Inovance;

public interface IInovanceLaunchProfile
{
    string StrategyId { get; }
    ProcessInvocation CreateInvocation(string requestPath, string resultPath);
}

/// <summary>Official ScriptEngine CLI baseline, not an InoProShop compatibility declaration.</summary>
public sealed class ScriptEngine35LaunchProfile(InovanceOptions options) : IInovanceLaunchProfile
{
    public string StrategyId => "scriptengine-3.5-cli-unverified";
    public ProcessInvocation CreateInvocation(string requestPath, string resultPath) => new(options.ExecutablePath,
        ["--profile=" + options.Profile, "--runscript=" + Path.GetFullPath(options.ScriptPath), "--noUI"],
        new Dictionary<string, string> { ["PLCMCP_REQUEST_PATH"] = requestPath, ["PLCMCP_RESULT_PATH"] = resultPath });
}
