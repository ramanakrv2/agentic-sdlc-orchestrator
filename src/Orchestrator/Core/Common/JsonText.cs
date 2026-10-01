using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestrator.Core.Common;

public static class JsonText
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Empty JSON for {typeof(T).Name}");

    /// <summary>
    /// Extracts the outermost JSON object from LLM output that may be wrapped in prose or code fences,
    /// then deserializes it. Throws <see cref="JsonException"/> with a message suitable as retry feedback.
    /// </summary>
    public static T ParseLlmJson<T>(string llmOutput)
    {
        var start = llmOutput.IndexOf('{');
        var end = llmOutput.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new JsonException("Response did not contain a JSON object. Reply with a single JSON object only.");
        var json = llmOutput[start..(end + 1)];
        try
        {
            return Deserialize<T>(json);
        }
        catch (JsonException ex)
        {
            throw new JsonException($"Response JSON was invalid for {typeof(T).Name}: {ex.Message}");
        }
    }
}
