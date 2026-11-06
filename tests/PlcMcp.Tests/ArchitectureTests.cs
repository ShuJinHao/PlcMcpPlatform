using PlcMcp.Core.Automation;
using PlcMcp.Core.Workspaces;
using PlcMcp.EngineeringService.Capabilities;
using PlcMcp.Services.Contracts.Capabilities;
using Xunit;

namespace PlcMcp.Tests;

public sealed class ArchitectureTests
{
    [Fact] public void Inner_layers_do_not_reference_external_implementations()
    {
        var assemblies = new[] { typeof(AutomationJob).Assembly, typeof(ProjectWorkspace).Assembly,
            typeof(ListCapabilitiesHandler).Assembly, typeof(IPlcCapabilityProvider).Assembly };
        foreach (var assembly in assemblies)
            foreach (var dependency in assembly.GetReferencedAssemblies())
                Assert.False(new[] { "ModelContextProtocol", "Microsoft.EntityFrameworkCore", "PlcMcp.Inovance",
                    "PlcMcp.EntityFrameworkCore", "PlcMcp.McpHost", "Serilog" }
                    .Any(prefix => dependency.Name!.StartsWith(prefix, StringComparison.Ordinal)),
                    $"{assembly.GetName().Name} must not reference {dependency.Name}");
    }
}
