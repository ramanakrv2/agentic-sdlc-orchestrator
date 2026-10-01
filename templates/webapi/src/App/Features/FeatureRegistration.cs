namespace App.Features;

/// <summary>
/// Feature entry point. Agents register feature services in <see cref="AddFeatures"/> and map endpoints in <see cref="MapFeatures"/>.
/// </summary>
public static class FeatureRegistration
{
    public static IServiceCollection AddFeatures(this IServiceCollection services, IConfiguration configuration)
    {
        return services;
    }

    public static IEndpointRouteBuilder MapFeatures(this IEndpointRouteBuilder endpoints)
    {
        return endpoints;
    }
}
