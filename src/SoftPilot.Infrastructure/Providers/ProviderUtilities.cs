using System.Text.Json;

namespace SoftPilot.Infrastructure.Providers;

internal static class ProviderUtilities
{
    public static string NormalizeVersion(string version) =>
        version.Trim().TrimStart('v');

    public static string FindChecksum(string checksumList, string fileName)
    {
        foreach (var line in checksumList.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOfAny([' ', '\t']);
            if (separator <= 0)
            {
                continue;
            }

            var candidate = line[(separator + 1)..].TrimStart(' ', '\t', '*');
            if (string.Equals(candidate, fileName, StringComparison.Ordinal))
            {
                return line[..separator].Trim();
            }
        }

        throw new IntegrityException($"官方校验清单中没有 {fileName}。");
    }

    public static async Task<string> GetRequiredStringAsync(
        HttpClient client,
        Uri uri,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new IntegrityException($"拒绝从非 HTTPS 地址读取官方元数据：{uri}");
        }

        using var response = await client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public static string? ReadFlexibleString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }
}
