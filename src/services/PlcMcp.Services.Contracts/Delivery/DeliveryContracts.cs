using System.Text.Json;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;

namespace PlcMcp.Services.Contracts.Delivery;

public static class DeliveryOperations { public const string Create = "project.package"; }
public sealed record DeliveryRequest(string WorkspaceName, string Revision, string ApplicationId);
public sealed record DeliveryArtifact(string ArtifactId, string Sha256, long ByteLength, string WorkspaceName,
    string Revision, string ProjectSha256, string ExecutionMode, string EvidenceKind, bool EngineeringReady, int FileCount);
public sealed record DeliveryChunk(DeliveryArtifact Artifact, long Offset, long NextOffset, bool Complete, string Base64, string ChunkSha256);
public sealed record DeliveryManifest(string ContractVersion, string WorkspaceName, string Revision, string ProjectFile,
    string ProjectSha256, FileStamp[] Files, TemplateDefinition Template, PlcMcp.Services.Contracts.Configuration.DependencySnapshot? Dependencies,
    string ExecutionMode, string EvidenceKind, bool EngineeringReady, ValidationEvidence? Validation, RuntimeReadiness? Acceptance = null);
public interface IDeliveryArtifacts
{
    Task<DeliveryArtifact> CreateAsync(ProjectSnapshot source, DeliveryManifest manifest, JsonElement compilation, JsonElement tasks, CancellationToken token);
    Task<DeliveryChunk> ReadAsync(string id, long offset, int maxBytes, CancellationToken token);
}
