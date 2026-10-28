using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace LiteShot;

internal enum UploadKind { Link, Search, Share }

internal sealed record UploadResult(string Url, string Id, string DeleteToken, DateTime Expires);

/// <summary>
/// Uploads to fortefile.com, Devgm's own host on Cloudflare R2. Screenshots are encrypted here with AES-256-GCM before
/// they leave the PC, and the key travels only in the link's #fragment, which browsers never send to a server, so
/// fortefile stores data it can't read. Files delete themselves after 1, 12 or 24 hours; the delete token kept in the
/// upload history removes one sooner. Reverse image search is the one exception: Google has to see the picture, so
/// those uploads aren't encrypted and last an hour.
/// </summary>
internal static class Uploader
{
    public const string DefaultServer = "https://fortefile.com";

    public static readonly (string Key, string Label)[] Lifetimes = { ("1h", "1 hour"), ("12h", "12 hours"), ("24h", "24 hours") };

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"LiteShot/{Updater.CurrentVersion}");
        return client;
    }

    /// <summary>settings.json can point uploads at another server (for testing a local one).</summary>
    public static string Server =>
        string.IsNullOrWhiteSpace(AppSettings.Current.FortefileServer) ? DefaultServer : AppSettings.Current.FortefileServer.Trim().TrimEnd('/');

    /// <summary>Older settings may hold "72h" or "never"; fortefile keeps files 24 hours at most.</summary>
    public static string Normalize(string key) => Lifetimes.Any(l => l.Key == key) ? key : "24h";

    public static TimeSpan Lifetime(string key) => Normalize(key) switch
    {
        "1h" => TimeSpan.FromHours(1),
        "12h" => TimeSpan.FromHours(12),
        _ => TimeSpan.FromHours(24),
    };

    public static string Describe(string key) => Lifetimes.First(l => l.Key == Normalize(key)).Label;

    /// <summary>Uploads an image and returns its link (with the key after the #, when encrypted).</summary>
    public static async Task<UploadResult> UploadAsync(byte[] png, string lifetime, bool encrypt)
    {
        byte[]? key = encrypt ? RandomNumberGenerator.GetBytes(32) : null;
        using var content = new ByteArrayContent(key != null ? Encrypt(png, key) : png);
        content.Headers.ContentType = new MediaTypeHeaderValue(key != null ? "application/octet-stream" : "image/png");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Server}/api/files") { Content = content };
        request.Headers.Add("X-Lifetime", ((int)Lifetime(lifetime).TotalSeconds).ToString());
        request.Headers.Add("X-Encrypted", key != null ? "1" : "0");

        using var response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ErrorFrom(body, response.StatusCode));
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string url = root.GetProperty("url").GetString() ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !uri.IsLoopback))
                throw new FormatException();
            return new UploadResult(
                key != null ? $"{url}#{Base64Url(key)}" : url,
                root.GetProperty("id").GetString() ?? "",
                root.GetProperty("deleteToken").GetString() ?? "",
                root.GetProperty("expires").GetDateTime().ToLocalTime());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            throw new InvalidOperationException("fortefile.com sent an answer LiteShot doesn't understand.");
        }
    }

    /// <summary>Deletes an upload now. A file that's already gone counts as deleted.</summary>
    public static async Task DeleteAsync(UploadRecord record)
    {
        if (string.IsNullOrEmpty(record.Id) || string.IsNullOrEmpty(record.DeleteToken))
            throw new InvalidOperationException("This link was made by an older version of LiteShot and can't be deleted early.");
        string server = new Uri(record.Url).GetLeftPart(UriPartial.Authority);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{server}/api/files/{Uri.EscapeDataString(record.Id)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", record.DeleteToken);
        using var response = await Http.SendAsync(request);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound) return;
        throw new InvalidOperationException(ErrorFrom(await response.Content.ReadAsStringAsync(), response.StatusCode));
    }

    /// <summary>AES-256-GCM: nonce (12 bytes), then ciphertext, then tag (16 bytes) — what the fortefile viewer expects.</summary>
    internal static byte[] Encrypt(byte[] plain, byte[] key)
    {
        var output = new byte[12 + plain.Length + 16];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(output.AsSpan(0, 12), plain, output.AsSpan(12, plain.Length), output.AsSpan(12 + plain.Length));
        return output;
    }

    internal static byte[] Decrypt(byte[] data, byte[] key)
    {
        var plain = new byte[data.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(12, plain.Length), data.AsSpan(data.Length - 16), plain);
        return plain;
    }

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromBase64Url(string text)
    {
        string s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }

    static string ErrorFrom(string body, HttpStatusCode status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } message)
                return status == HttpStatusCode.TooManyRequests ? "Too many uploads in a row. Try again in a minute." : $"fortefile.com: {message}.";
        }
        catch (JsonException) { }
        return $"fortefile.com answered {(int)status}.";
    }
}
