using App.Features.Urls;
using App.Platform.Queue;

namespace App.Features;

/// <summary>
/// Feature entry point. Agents register feature services in <see cref="AddFeatures"/> and map endpoints in <see cref="MapFeatures"/>.
/// </summary>
public static class FeatureRegistration
{
    public static IServiceCollection AddFeatures(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IUrlRepository, EfUrlRepository>();
        services.AddScoped<IClickAnalyticsRepository, EfClickAnalyticsRepository>();
        services.AddSingleton<IShortCodeGenerator, Base62CodeGenerator>();
        services.AddScoped<IUrlService, UrlService>();
        services.AddBackgroundQueue<ClickEvent, ClickRecorder>(o =>
        {
            o.Capacity = configuration.GetValue("Analytics:QueueCapacity", 10_000);
            o.BatchSize = 200;
            o.FlushInterval = TimeSpan.FromMilliseconds(500);
        });
        return services;
    }

    public static IEndpointRouteBuilder MapFeatures(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapUrlEndpoints();
        return endpoints;
    }
}
