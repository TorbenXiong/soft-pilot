using System.Text.Encodings.Web;
using System.Text.Json;

namespace SoftPilot.Application;

public static class JsonTextFormatter
{
    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static string Beautify(string text) => Transform(text, IndentedOptions);

    public static string Minify(string text) => Transform(text, CompactOptions);

    private static string Transform(string text, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException("JSON text cannot be empty.");
        }

        using var document = JsonDocument.Parse(text);
        return JsonSerializer.Serialize(document.RootElement, options);
    }
}
