using App.Platform.Queue;

namespace App.Features.Urls;

public static class UrlEndpoints
{
    public static IEndpointRouteBuilder MapUrlEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/urls").WithTags("Urls");

        api.MapPost("/", async (CreateUrlRequest request, IUrlService service, HttpContext http, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            return result.Status switch
            {
                CreateStatus.Invalid => Results.ValidationProblem(result.Errors!),
                CreateStatus.AliasTaken => Results.Problem("The requested alias is already in use.", statusCode: StatusCodes.Status409Conflict),
                _ => Results.Created($"/api/v1/urls/{result.Url!.Code}", ToResponse(result.Url, http)),
            };
        });

        api.MapGet("/{code}", async (string code, IUrlService service, HttpContext http, CancellationToken ct) =>
            await service.GetAsync(code, ct) is { } url ? Results.Ok(ToResponse(url, http)) : Results.NotFound());

        api.MapGet("/{code}/stats", async (string code, int? days, IUrlService service, CancellationToken ct) =>
            await service.GetStatsAsync(code, days ?? 30, ct) is { } stats ? Results.Ok(stats) : Results.NotFound());

        // Redirect hot path: cache lookup, enqueue the click (never blocks), 302 so every click reaches us for analytics.
        app.MapGet("/{code:regex(^[A-Za-z0-9_-]{{4,32}}$)}", async (string code, IUrlService service, IBackgroundQueue<ClickEvent> clicks,
            TimeProvider time, HttpContext http, CancellationToken ct) =>
        {
            var resolved = await service.ResolveAsync(code, ct);
            switch (resolved.Status)
            {
                case ResolveStatus.NotFound:
                    return Results.NotFound();
                case ResolveStatus.Expired:
                    return Results.Problem("This short link has expired.", statusCode: StatusCodes.Status410Gone);
            }

            clicks.TryEnqueue(new ClickEvent
            {
                Code = code,
                OccurredAt = time.GetUtcNow(),
                ReferrerHost = ClickRecorder.ReferrerHost(http.Request.Headers.Referer.ToString()),
                UserAgentFamily = ClickRecorder.UserAgentFamily(http.Request.Headers.UserAgent.ToString()),
            });
            return Results.Redirect(resolved.TargetUrl!, permanent: false);
        });

        return app;
    }

    private static UrlResponse ToResponse(ShortUrl url, HttpContext http) =>
        new(url.Code, $"{http.Request.Scheme}://{http.Request.Host}/{url.Code}", url.TargetUrl, url.CreatedAt, url.ClickCount, url.ExpiresAt);
}
