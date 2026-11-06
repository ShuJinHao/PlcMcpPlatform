namespace PlcMcp.Execution;

/// <summary>Single local owner; the OS releases sharing locks on process termination.</summary>
public sealed class InstanceOwnership : IDisposable
{
    private readonly FileStream handle;
    public InstanceOwnership(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        handle = new FileStream(Path.Combine(dataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public void Dispose() => handle.Dispose();
}
