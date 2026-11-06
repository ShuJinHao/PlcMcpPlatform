using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.Inovance;

/// <summary>Explicit local test scope. Declared versions are evidence labels, not compatibility certification.</summary>
public sealed record InovanceValidationProfile(string ValidationId, string IdeVersion, string EngineVersion, string Profile, string[] AllowedOperations)
{
    public static InovanceValidationProfile Load(string path)
    {
        if (!Path.IsPathFullyQualified(path) || new FileInfo(path).Length > 16384) throw new InvalidOperationException("Use an absolute path to a bounded validation profile.");
        var value = JsonSerializer.Deserialize<InovanceValidationProfile>(File.ReadAllText(path), WorkerJson.Options)
            ?? throw new InvalidOperationException("Invalid validation profile.");
        value.Validate(); return value;
    }
    public void Validate()
    {
        if (new[] { ValidationId, IdeVersion, EngineVersion, Profile }.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 256 || x.Contains('\0'))
            || AllowedOperations is not { Length: > 0 and <= 96 } || AllowedOperations.Distinct(StringComparer.Ordinal).Count() != AllowedOperations.Length
            || AllowedOperations.Any(x => !EngineeringOperations.Contains(x) && !EditOperations.Contains(x)))
            throw new InvalidOperationException("Validation requires explicit identity, versions, IDE profile and known operation allowlist.");
    }
    public bool Allows(string operation) => operation == EditOperations.VerifyProject
        ? AllowedOperations.Contains(EditOperations.CreateProject, StringComparer.Ordinal) || AllowedOperations.Contains(EditOperations.SaveProject, StringComparer.Ordinal)
            || AllowedOperations.Contains(PlcMcp.Services.Contracts.Batch.BatchOperations.Apply, StringComparer.Ordinal)
            || AllowedOperations.Contains(PlcMcp.Services.Contracts.Incremental.IncrementalOperations.Adopt, StringComparer.Ordinal)
            || AllowedOperations.Contains(PlcMcp.Services.Contracts.Incremental.IncrementalOperations.Restore, StringComparer.Ordinal)
        : AllowedOperations.Contains(operation, StringComparer.Ordinal);
    public ValidationEvidence Evidence(string scriptPath)
    {
        Validate();
        var directory = Path.GetDirectoryName(Path.GetFullPath(scriptPath))!;
        var hashes = new[] { Path.GetFileName(scriptPath), "configuration_worker.py", "exchange_worker.py" }.Select(name =>
            name + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name)))).ToLowerInvariant());
        var bundle = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", hashes)))).ToLowerInvariant();
        return new(ValidationId, IdeVersion, EngineVersion, Profile, bundle, AllowedOperations.ToArray());
    }
}
