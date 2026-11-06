namespace PlcMcp.Execution;

/// <summary>Single local owner; the OS releases sharing locks on process termination.</summary>
public sealed class InstanceOwnership : IDisposable
{
    private readonly FileStream handle;
    public InstanceOwnership(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "instance.lock");
        if ((File.GetAttributes(dataDirectory) & FileAttributes.ReparsePoint) != 0
            || File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("instance_path_not_allowed");
        handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public void Dispose() => handle.Dispose();
}
