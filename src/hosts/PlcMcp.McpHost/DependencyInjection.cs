using PlcMcp.EngineeringService.Capabilities;
using PlcMcp.Inovance;
using PlcMcp.Services.Contracts.Capabilities;
using PlcMcp.Services.Contracts.Messaging;

namespace PlcMcp.McpHost;

public static class DependencyInjection
{
    public static IServiceCollection AddPlcMcpApplication(this IServiceCollection services)
    {
        services.AddSingleton<IPlcCapabilityProvider, InovanceCapabilityProvider>();
        services.AddScoped<IQueryHandler<ListCapabilitiesQuery, CapabilityCatalog>, ListCapabilitiesHandler>();
        return services;
    }
}
