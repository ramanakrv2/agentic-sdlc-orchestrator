using System.Net;
using System.Net.Http.Json;
using App.Features.Urls;
using App.Tests.Support;

namespace App.Tests.Features.Urls;

public sealed class UrlApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpClient NoRedirectClient(TestApp app) =>
        app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Create_returns_201_with_location_and_short_url()
    {
        await using var app = new TestApp();
        var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/urls", new CreateUrlRequest("https://example.com/docs"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<UrlResponse>(Ct);
        body!.Code.Length.ShouldBe(Base62CodeGenerator.Length);
        body.TargetUrl.ShouldBe("https://example.com/docs");
        body.ShortUrl.ShouldEndWith("/" + body.Code);
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/urls/{body.Code}");
    }

    [Fact]
    public async Task Invalid_url_returns_400_problem_details()
    {
        await using var app = new TestApp();
        var response = await app.CreateClient().PostAsJsonAsync("/api/v1/urls", new CreateUrlRequest("javascript:alert(1)"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("url");
    }

    [Fact]
    public async Task Custom_alias_is_used_and_duplicate_alias_returns_409()
    {
        await using var app = new TestApp();
        var client = app.CreateClient();

        var first = await client.PostAsJsonAsync("/api/v1/urls", new CreateUrlRequest("https://example.com/a", "my-link"), Ct);
        var second = await client.PostAsJsonAsync("/api/v1/urls", new CreateUrlRequest("https://example.com/b", "my-link"), Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await first.Content.ReadFromJsonAsync<UrlResponse>(Ct))!.Code.ShouldBe("my-link");
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Redirect_returns_302_to_target_and_unknown_code_returns_404()
    {
        await using var app = new TestApp();
        var client = NoRedirectClient(app);
        var created = await (await client.PostAsJsonAsync("/api/v1/urls", new CreateUrlRequest("https://example.com/target"), Ct))
            .Content.ReadFromJsonAsync<UrlResponse>(Ct);

        var redirect = await client.GetAsync($"/{created!.Code}", Ct);
        var missing = await client.GetAsync("/zzzzzzz", Ct);

        redirect.StatusCode.ShouldBe(HttpStatusCode.Found);
        redirect.Headers.Location!.ToString().ShouldBe("https://example.com/target");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_metadata_returns_link_or_404()
    {
        await using var app = new TestApp();
        var client = app.CreateClient();
        var created = await (await client.PostAsJsonAsync("/api/v1/urls", new CreateUrlRequest("https://example.com/meta"), Ct))
            .Content.ReadFromJsonAsync<UrlResponse>(Ct);

        (await client.GetFromJsonAsync<UrlResponse>($"/api/v1/urls/{created!.Code}", Ct))!.TargetUrl.ShouldBe("https://example.com/meta");
        (await client.GetAsync("/api/v1/urls/nope1234", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Clicks_are_recorded_asynchronously_and_reported_in_stats()
    {
        await using var app = new TestApp();
        var client = NoRedirectClient(app);
        var created = await (await client.PostAsJsonAsync("/api/v1/urls", new CreateUrlRequest("https://example.com/stats"), Ct))
            .Content.ReadFromJsonAsync<UrlResponse>(Ct);

        for (var i = 0; i < 3; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/{created!.Code}");
            request.Headers.Referrer = new Uri("https://news.example.org/post");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 Firefox/121.0");
            await client.SendAsync(request, Ct);
        }

        // The analytics worker flushes in the background (batch or 500 ms interval); poll with a deadline.
        UrlStatsResponse? stats = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            stats = await client.GetFromJsonAsync<UrlStatsResponse>($"/api/v1/urls/{created!.Code}/stats", Ct);
            if (stats!.TotalClicks == 3) break;
            await Task.Delay(100, Ct);
        }

        stats!.TotalClicks.ShouldBe(3);
        stats.Daily.Sum(d => d.Clicks).ShouldBe(3);
        stats.TopReferrers.ShouldContain(r => r.Referrer == "news.example.org" && r.Clicks == 3);
        stats.UserAgents["Firefox"].ShouldBe(3);
    }

    [Fact]
    public async Task Stats_for_unknown_code_returns_404()
    {
        await using var app = new TestApp();
        (await app.CreateClient().GetAsync("/api/v1/urls/unknown1/stats", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Parallel_creates_never_produce_duplicate_codes()
    {
        await using var app = new TestApp();
        var client = app.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 200).Select(i =>
            client.PostAsJsonAsync("/api/v1/urls", new CreateUrlRequest($"https://example.com/{i}"), Ct)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created);
        var codes = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<UrlResponse>(Ct))!.Code));
        codes.Distinct().Count().ShouldBe(200);
    }
}
