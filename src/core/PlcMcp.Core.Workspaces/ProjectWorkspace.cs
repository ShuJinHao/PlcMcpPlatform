using PlcMcp.SharedKernel.Domain;

namespace PlcMcp.Core.Workspaces;

/// <summary>平台管理的工作区元数据。PLC 工程内容始终由厂商工程文件持有。</summary>
public sealed class ProjectWorkspace : IAggregateRoot
{
    private ProjectWorkspace() { }
    public Guid Id { get; private set; }
    public string VendorId { get; private set; } = string.Empty;
    public string ProjectPath { get; private set; } = string.Empty;
    public string Revision { get; private set; } = string.Empty;
    public long Version { get; private set; }

    public static ProjectWorkspace Register(string vendorId, string absoluteProjectPath, string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vendorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteProjectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        if (!Path.IsPathFullyQualified(absoluteProjectPath))
            throw new ArgumentException("Project path must be absolute.", nameof(absoluteProjectPath));
        return new() { Id = Guid.NewGuid(), VendorId = vendorId, ProjectPath = absoluteProjectPath, Revision = revision };
    }

    public void RecordVerifiedRevision(string expectedRevision, string verifiedRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedRevision);
        if (!string.Equals(Revision, expectedRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("workspace_revision_conflict");
        Revision = verifiedRevision;
        Version++;
    }
}
