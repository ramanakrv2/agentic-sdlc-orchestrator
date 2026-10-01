using System.Net;
using System.Net.Http.Json;
using App.Features.Urls;
using App.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;

namespace App.Tests.Features.Urls;

public sealed class UrlExpiryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<UrlResponse> CreateAsync(HttpClient client, CreateUrlRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/v1/urls", request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<UrlResponse>(Ct))!;
    }

    [Fact]
    public async Task Link_redirects_before_expiry_and_returns_410_after()
    {
        await using var app = new TestApp();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var link = await CreateAsync(client, new CreateUrlRequest("https://example.com/promo", ExpiresInMinutes: 60));

        (await client.GetAsync($"/{link.Code}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Found);

        app.Clock.Advance(TimeSpan.FromMinutes(61));
        var expired = await client.GetAsync($"/{link.Code}", Ct);

        expired.StatusCode.ShouldBe(HttpStatusCode.Gone); // also proves a cached entry does not outlive the link
    }

    [Fact]
    public async Task Metadata_shows_the_expiry_time()
    {
        await using var app = new TestApp();
        var client = app.CreateClient();
        var link = await CreateAsync(client, new CreateUrlRequest("https://example.com/a", "expiring", 30));

        var metadata = await client.GetFromJsonAsync<UrlResponse>($"/api/v1/urls/{link.Code}", Ct);

        metadata!.ExpiresAt.ShouldBe(app.Clock.GetUtcNow().AddMinutes(30));
    }

    [Fact]
    public async Task Links_without_expiry_never_expire()
    {
        await using var app = new TestApp();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var link = await CreateAsync(client, new CreateUrlRequest("https://example.com/forever"));

        app.Clock.Advance(TimeSpan.FromDays(400));

        link.ExpiresAt.ShouldBeNull();
        (await client.GetAsync($"/{link.Code}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Found);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(UrlValidator.MaxExpiryMinutes + 1)]
    public void Out_of_range_expiry_is_rejected(int minutes) =>
        UrlValidator.Validate(new CreateUrlRequest("https://example.com", ExpiresInMinutes: minutes)).ShouldContainKey("expiresInMinutes");

    [Fact]
    public async Task Invalid_expiry_returns_400()
    {
        await using var app = new TestApp();
        var response = await app.CreateClient().PostAsJsonAsync("/api/v1/urls", new CreateUrlRequest("https://example.com", ExpiresInMinutes: 0), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void Entity_reports_expiry_relative_to_now()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        new ShortUrl { ExpiresAt = now }.IsExpired(now).ShouldBeTrue();
        new ShortUrl { ExpiresAt = now.AddSeconds(1) }.IsExpired(now).ShouldBeFalse();
        new ShortUrl().IsExpired(now).ShouldBeFalse();
    }
}
