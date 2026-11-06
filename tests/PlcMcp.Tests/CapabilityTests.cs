using PlcMcp.EngineeringService.Capabilities;
using PlcMcp.Inovance;
using PlcMcp.Services.Contracts.Capabilities;
using Xunit;

namespace PlcMcp.Tests;

public sealed class CapabilityTests
{
    [Fact] public async Task Catalog_never_advertises_unverified_IDE_operations_as_supported()
    {
        var catalog = await new ListCapabilitiesHandler([new InovanceCapabilityProvider()]).Handle(new(), default);
        Assert.NotEmpty(catalog.Capabilities);
        Assert.All(catalog.Capabilities, c => Assert.Equal(CapabilityStatus.Unverified, c.Status));
        Assert.Contains(catalog.Capabilities, c => c.Id == "language.ld.edit");
        Assert.Contains(catalog.Capabilities, c => c.Id == "task.pou_binding");
    }
    [Fact] public async Task Duplicate_capability_identity_is_rejected()
    {
        var handler = new ListCapabilitiesHandler([new InovanceCapabilityProvider(), new InovanceCapabilityProvider()]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(new(), default));
    }
}
