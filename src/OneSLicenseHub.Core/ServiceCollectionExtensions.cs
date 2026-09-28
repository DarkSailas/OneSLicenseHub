using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OneSLicenseHub.Core.Configuration;
using OneSLicenseHub.Core.Services;

namespace OneSLicenseHub.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLicenseHubCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HubOptions>(configuration.GetSection("HubOptions"));

        // Register custom HttpClient with SSL certificate bypass for Digi self-signed certificates
        services.AddHttpClient<IDigi24PlusService, Digi24PlusService>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            });

        services.AddHttpClient<IDigi14ClassicService, Digi14ClassicService>();
        services.AddHttpClient<ISlkInspectorService, SlkInspectorService>();
        services.AddHttpClient<IGuardantInspectorService, GuardantInspectorService>();
        services.AddHttpClient<ISentinelInspectorService, SentinelInspectorService>();

        services.AddSingleton<LicenseHubAggregator>();
        services.AddSingleton<ILicenseHubAggregator>(sp => sp.GetRequiredService<LicenseHubAggregator>());
        services.AddHostedService(sp => sp.GetRequiredService<LicenseHubAggregator>());

        return services;
    }
}
