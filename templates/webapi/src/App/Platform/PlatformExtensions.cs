using System.Threading.RateLimiting;
using App.Platform.Caching;
using App.Platform.Data;
using App.Platform.Ops;
using App.Platform.Queue;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Timeouts;
using Serilog;

namespace App.Platform;

// PLATFORM-OWNED (template-locked). Cross-cutting concerns every generated service gets for free.

public static class PlatformExtensions
{
    public static WebApplicationBuilder AddPlatform(this WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var services = builder.Services;
        var instanceId = string.IsNullOrWhiteSpace(config["InstanceId"]) ? $"{Environment.MachineName}-{Environment.ProcessId}" : config["InstanceId"]!;

        services.AddSerilog((_, lc) => lc
            .ReadFrom.Configuration(config)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("InstanceId", instanceId)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {InstanceId} {SourceContext}: {Message:lj}{NewLine}{Exception}"));

        services.Configure<DatabaseOptions>(config.GetSection(DatabaseOptions.Section));
        services.Configure<CacheOptions>(config.GetSection(CacheOptions.Section));
        services.Configure<RateLimitingOptions>(config.GetSection(RateLimitingOptions.Section));
        services.Configure<ChaosOptions>(config.GetSection(ChaosOptions.Section));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<OpsCounters>();
        services.AddSingleton(new InstanceInfo(instanceId));
        services.AddProblemDetails();

        services.AddPlatformDatabase(config);
        services.AddPlatformCache(config);

        // Rate limiting: fixed window per client (protects capacity; health/ops endpoints are exempt).
        var rl = config.GetSection(RateLimitingOptions.Section).Get<RateLimitingOptions>() ?? new RateLimitingOptions();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                if (!rl.Enabled || ctx.Request.Path.StartsWithSegments("/health") || ctx.Request.Path.StartsWithSegments("/ops"))
                    return RateLimitPartition.GetNoLimiter("exempt");
                return RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = rl.PermitLimit,
                    Window = TimeSpan.FromSeconds(rl.WindowSeconds),
                    QueueLimit = 0,
                });
            });
            o.OnRejected = (ctx, _) =>
            {
                ctx.HttpContext.RequestServices.GetRequiredService<OpsCounters>().RateLimited();
                ctx.HttpContext.Response.Headers.RetryAfter = rl.WindowSeconds.ToString();
                return ValueTask.CompletedTask;
            };
        });

        // Request timeouts: fail fast instead of piling up when a dependency is slow.
        var timeoutMs = config.GetValue<int?>($"{RequestOptions.Section}:TimeoutMs") ?? 5000;
        services.AddRequestTimeouts(o => o.DefaultPolicy = new RequestTimeoutPolicy
        {
            Timeout = TimeSpan.FromMilliseconds(timeoutMs),
            TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
        });

        services.AddHealthChecks().AddCheck<DbHealthCheck>("database", tags: ["ready"]);
        return builder;
    }

    public static WebApplication UsePlatform(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        var instance = app.Services.GetRequiredService<InstanceInfo>();
        var ops = app.Services.GetRequiredService<OpsCounters>();
        app.Use(async (ctx, next) =>
        {
            ops.Request();
            ctx.Response.Headers["X-Instance-Id"] = instance.Id;
            await next();
        });

        app.UseRateLimiter();
        app.UseRequestTimeouts();

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
        app.MapGet("/ops/stats", (OpsCounters counters, IEnumerable<IQueueProbe> queues) => Results.Ok(counters.Snapshot(instance.Id, queues)))
            .ExcludeFromDescription();
        return app;
    }

    public static Task InitializePlatformAsync(this WebApplication app) => app.Services.InitializeDatabaseAsync();

    /// <summary>
    /// Client identity for rate limiting. X-Forwarded-For is honoured because the local cluster runs behind the YARP gateway;
    /// in production only trust it from known proxies (ForwardedHeadersOptions.KnownProxies).
    /// </summary>
    private static string ClientKey(HttpContext ctx)
    {
        var forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrEmpty(forwarded)) return forwarded.Split(',')[0].Trim();
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}

public sealed record InstanceInfo(string Id);
