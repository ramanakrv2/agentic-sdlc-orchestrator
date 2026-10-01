using App.Features.Urls;

namespace App.Tests.Features.Urls;

public sealed class UrlUnitTests
{
    [Fact]
    public void Generated_codes_are_seven_base62_characters_and_unique()
    {
        var generator = new Base62CodeGenerator();

        var codes = Enumerable.Range(0, 10_000).Select(_ => generator.Generate()).ToList();

        codes.ShouldAllBe(c => c.Length == Base62CodeGenerator.Length && c.All(char.IsLetterOrDigit));
        codes.Distinct().Count().ShouldBe(codes.Count);
    }

    [Theory]
    [InlineData("https://example.com/a?b=c")]
    [InlineData("http://example.com")]
    public void Valid_urls_pass(string url) =>
        UrlValidator.Validate(new CreateUrlRequest(url)).ShouldBeEmpty();

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/file")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@example.com")]
    public void Invalid_urls_are_rejected(string url) =>
        UrlValidator.Validate(new CreateUrlRequest(url)).ShouldContainKey("url");

    [Fact]
    public void Overlong_urls_are_rejected() =>
        UrlValidator.Validate(new CreateUrlRequest("https://example.com/" + new string('a', UrlValidator.MaxUrlLength))).ShouldContainKey("url");

    [Theory]
    [InlineData("abc")]
    [InlineData("has space")]
    [InlineData("api")]
    [InlineData("health")]
    public void Bad_or_reserved_aliases_are_rejected(string alias) =>
        UrlValidator.Validate(new CreateUrlRequest("https://example.com", alias)).ShouldContainKey("customAlias");

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/120.0 Safari/537.36", "Chrome")]
    [InlineData("Mozilla/5.0 Firefox/121.0", "Firefox")]
    [InlineData("Googlebot/2.1", "Bot")]
    [InlineData("curl/8.4.0", "curl")]
    [InlineData(null, "Unknown")]
    public void User_agent_family_is_classified(string? userAgent, string expected) =>
        ClickRecorder.UserAgentFamily(userAgent).ShouldBe(expected);

    [Fact]
    public void Referrer_host_is_extracted_and_garbage_ignored()
    {
        ClickRecorder.ReferrerHost("https://news.example.org/post/1").ShouldBe("news.example.org");
        ClickRecorder.ReferrerHost("not-a-url").ShouldBeNull();
    }
}
