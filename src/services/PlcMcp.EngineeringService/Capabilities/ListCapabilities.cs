using PlcMcp.Services.Contracts.Capabilities;
using PlcMcp.Services.Contracts.Messaging;

namespace PlcMcp.EngineeringService.Capabilities;

public sealed record ListCapabilitiesQuery;
public sealed class ListCapabilitiesHandler(IEnumerable<IPlcCapabilityProvider> providers)
    : IQueryHandler<ListCapabilitiesQuery, CapabilityCatalog>
{
    public Task<CapabilityCatalog> Handle(ListCapabilitiesQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capabilities = providers.SelectMany(p => p.GetCapabilities()).OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
        if (capabilities.GroupBy(c => (c.VendorId, c.IdeVersion, c.Id)).Any(group => group.Count() > 1))
            throw new InvalidOperationException("duplicate_capability_registration");
        return Task.FromResult(new CapabilityCatalog("1.0", "P6-incremental-engineering", capabilities));
    }
}
