using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlcMcp.Execution;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.Inovance;

public sealed record IdentityFile(string Name, string Sha256, string FileVersion, string ProductVersion);
public sealed record InovanceIdentity(string Profile, string StrategyId, IdentityFile Executable, IdentityFile Engine,
    IdentityFile[] Dependencies, string ScriptSha256, string ExecutorSha256)
{
    public string Fingerprint() => Hash(JsonSerializer.SerializeToUtf8Bytes(this, WorkerJson.Options));
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static IdentityFile ObserveFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new InvalidOperationException("identity_file_unavailable");
        var info = FileVersionInfo.GetVersionInfo(path);
        using var stream = File.OpenRead(path);
        return new(Path.GetFileName(path), Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), info.FileVersion ?? "", info.ProductVersion ?? "");
    }
    public static string Scripts(string scriptPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(scriptPath))!;
        return Hash(Encoding.UTF8.GetBytes(string.Join("\n", new[] { Path.GetFileName(scriptPath), "configuration_worker.py", "exchange_worker.py", "runtime_identity.py" }
            .Select(name => name + ":" + Hash(File.ReadAllBytes(Path.Combine(folder, name)))))));
    }
    public static InovanceIdentity Observe(InovanceOptions options, string strategy)
    {
        // ValidationHost and McpHost share these binaries. Test/validation entry points are not execution implementations.
        var code = Directory.EnumerateFiles(AppContext.BaseDirectory, "PlcMcp.*.dll")
            .Where(path => Path.GetFileName(path) is not ("PlcMcp.TestHost.dll" or "PlcMcp.ValidationHost.dll" or "PlcMcp.Tests.dll"))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal).Select(path => Path.GetFileName(path) + ":" + ObserveFile(path).Sha256).ToArray();
        if (code.Length == 0) throw new InvalidOperationException("identity_code_unavailable");
        return new(options.Profile, strategy, ObserveFile(options.ExecutablePath), ObserveFile(options.EngineAssemblyPath),
            (options.IdentityFiles ?? []).Select(ObserveFile).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray(), Scripts(options.ScriptPath), Hash(Encoding.UTF8.GetBytes(string.Join("\n", code))));
    }
}
