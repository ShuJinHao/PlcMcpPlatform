using System.Security.Cryptography;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.Execution;

public sealed class WorkspaceFiles(string allowedRoot) : IWorkspaceFiles
{
    private readonly string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot));
    public Result<string> NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || !Path.IsPathFullyQualified(path))
            return Result<string>.Failure("invalid_request", "An absolute project path is required.");
        try
        {
            var full = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                return Result<string>.Failure("path_not_allowed", "Path is outside the configured workspace root.");
            return Result<string>.Success(OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        { return Result<string>.Failure("invalid_request", "Invalid project path."); }
    }
    public async Task<Result<ResolvedProject>> ResolveAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = NormalizePath(path);
        if (!normalized.IsSuccess) return Result<ResolvedProject>.Failure(normalized.Error!.Code, normalized.Error.Message);
        try
        {
            var full = normalized.Value!;
            var relative = Path.GetRelativePath(root, full);
            var current = root;
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return DeniedLink();
            foreach (var part in relative.Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, part);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return DeniedLink();
            }
            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            var revision = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            return Result<ResolvedProject>.Success(new(full, revision));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Result<ResolvedProject>.Failure("project_unavailable", "Project file is absent, unreadable or busy."); }
    }
    private static Result<ResolvedProject> DeniedLink() => Result<ResolvedProject>.Failure("path_not_allowed", "Linked paths are not accepted.");
}
