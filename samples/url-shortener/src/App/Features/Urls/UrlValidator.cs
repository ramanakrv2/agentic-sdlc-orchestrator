using System.Text.RegularExpressions;

namespace App.Features.Urls;

public static partial class UrlValidator
{
    public const int MaxUrlLength = 2048;
    public const int MaxExpiryMinutes = 525_600; // one year

    private static readonly HashSet<string> ReservedAliases = new(StringComparer.OrdinalIgnoreCase) { "api", "health", "ops", "swagger" };

    [GeneratedRegex("^[A-Za-z0-9_-]{4,32}$")]
    private static partial Regex AliasPattern();

    public static Dictionary<string, string[]> Validate(CreateUrlRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Url))
            errors["url"] = ["URL is required."];
        else if (request.Url.Length > MaxUrlLength)
            errors["url"] = [$"URL must be at most {MaxUrlLength} characters."];
        else if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            errors["url"] = ["URL must be an absolute http or https URL."];
        else if (!string.IsNullOrEmpty(uri.UserInfo))
            errors["url"] = ["URLs with embedded credentials are not allowed."];

        if (request.CustomAlias is { } alias)
        {
            if (!AliasPattern().IsMatch(alias))
                errors["customAlias"] = ["Alias must be 4-32 characters: letters, digits, '-' or '_'."];
            else if (ReservedAliases.Contains(alias))
                errors["customAlias"] = ["Alias is reserved."];
        }

        if (request.ExpiresInMinutes is < 1 or > MaxExpiryMinutes)
            errors["expiresInMinutes"] = [$"Expiry must be between 1 and {MaxExpiryMinutes} minutes."];

        return errors;
    }
}
