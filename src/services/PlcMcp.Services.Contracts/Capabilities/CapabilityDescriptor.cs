using System.Text.Json.Serialization;

namespace PlcMcp.Services.Contracts.Capabilities;

[JsonConverter(typeof(JsonStringEnumConverter<CapabilityStatus>))]
public enum CapabilityStatus { Unverified, Supported, Unsupported, Unavailable }
[JsonConverter(typeof(JsonStringEnumConverter<OperationRisk>))]
public enum OperationRisk { Read, ModifyProject, OnlineControl }
public sealed record CapabilityDescriptor(string Id, string VendorId, string IdeVersion,
    CapabilityStatus Status, OperationRisk Risk, string Reason, string? EvidenceReference = null, PlcMcp.Services.Contracts.Execution.OperationAcceptance[]? AcceptedScopes = null);
public interface IPlcCapabilityProvider
{
    string VendorId { get; }
    IReadOnlyList<CapabilityDescriptor> GetCapabilities();
}
public sealed record CapabilityCatalog(string ContractVersion, string Stage,
    IReadOnlyList<CapabilityDescriptor> Capabilities);
