namespace PlcMcp.Execution;

/// <summary>Single local owner; the OS releases sharing locks on process termination.</summary>
public sealed class InstanceOwnership : IDisposable
{
    private readonly FileStream handle;
    private readonly FileStream? workspaceHandle;
    private static FileStream Acquire(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "instance.lock");
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
            || File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("instance_path_not_allowed");
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public InstanceOwnership(string dataDirectory, string? workspaceRoot = null)
    {
        handle = Acquire(dataDirectory);
        try
        {
            if (workspaceRoot is not null && !Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                workspaceHandle = Acquire(workspaceRoot);
        }
        catch { handle.Dispose(); throw; }
    }
    public void Dispose() { workspaceHandle?.Dispose(); handle.Dispose(); }
}
