using System.Text;
using System.Text.Json;

namespace LiteShot;

internal sealed class SyncNodeException : Exception
{
    public int Status { get; }
    public SyncNodeException(string message, int status = 0) : base(message) => Status = status;
}

/// <summary>
/// Thin client for SyncNode (run.syncnode.ai), which routes the call to Replicate with the user's own
/// provider credentials. Generation is asynchronous: submit, then poll the job until it finishes.
/// </summary>
internal static class SyncNodeClient
{
    const string Base = "https://run.syncnode.ai";
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LiteShot/1.0");
        return client;
    }

    /// <summary>Starts a Replicate model run and returns the SyncNode job id.</summary>
    public static async Task<string> SubmitAsync(string apiKey, string model, Dictionary<string, object> input, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object> { ["apiKey"] = apiKey, ["model"] = model, ["input"] = input });
        using var response = await Http.PostAsync(Base + "/generate", new StringContent(payload, Encoding.UTF8, "application/json"), ct);
        using var doc = await ReadJson(response, ct);
        if (!doc.RootElement.TryGetProperty("job_id", out var id))
            throw new SyncNodeException("SyncNode didn't return a job id.");
        return id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText();
    }

    /// <summary>Polls a job (gently, every 2–4 s) until it succeeds, and returns the first output URL.</summary>
    public static async Task<string> WaitAsync(string apiKey, string jobId, IProgress<string>? progress, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        int delay = 2000;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using (var response = await Http.GetAsync($"{Base}/prediction-status?apiKey={Uri.EscapeDataString(apiKey)}&job_id={Uri.EscapeDataString(jobId)}", ct))
            using (var doc = await ReadJson(response, ct))
            {
                var root = doc.RootElement;
                string status = (Text(root, "replicate_status") ?? Text(root, "status") ?? "").ToLowerInvariant();
                progress?.Report(status switch
                {
                    "starting" => "Starting the model…",
                    "processing" => "Generating…",
                    "" or "in_progress" => "Queued…",
                    _ => status,
                });
                if (status is "succeeded" or "completed")
                {
                    if (root.TryGetProperty("output", out var output) && FirstUrl(output) is string url) return url;
                    throw new SyncNodeException("The model finished but returned no file.");
                }
                if (status is "failed" or "canceled" or "cancelled")
                    throw new SyncNodeException(ErrorText(root) ?? "The model run failed.");
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The model is taking too long. Try again in a moment.");
            await Task.Delay(delay, ct);
            delay = Math.Min(4000, delay + 500);
        }
    }

    public static async Task<decimal?> BalanceAsync(string apiKey, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync($"{Base}/balance?apiKey={Uri.EscapeDataString(apiKey)}", ct);
        using var doc = await ReadJson(response, ct);
        if (!doc.RootElement.TryGetProperty("balance", out var b)) return null;
        if (b.ValueKind == JsonValueKind.Number) return b.GetDecimal();
        return decimal.TryParse(b.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public static async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]);
        return await Http.GetByteArrayAsync(url, ct);
    }

    static async Task<JsonDocument> ReadJson(HttpResponseMessage response, CancellationToken ct)
    {
        string text = await response.Content.ReadAsStringAsync(ct);
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { }
        string? error = doc != null ? ErrorText(doc.RootElement, onlyErrorFields: true) : null;
        if (response.IsSuccessStatusCode && doc != null && error == null) return doc;
        doc?.Dispose();
        error ??= text.Length is > 0 and < 300 ? text : $"SyncNode answered HTTP {(int)response.StatusCode}.";
        throw new SyncNodeException(error, (int)response.StatusCode);
    }

    static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string? ErrorText(JsonElement root, bool onlyErrorFields = false)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("error", out var err))
        {
            if (err.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(err.GetString())) return err.GetString();
            if (err.ValueKind == JsonValueKind.Object) return Text(err, "message") ?? err.GetRawText();
        }
        if (onlyErrorFields) return null;
        return Text(root, "message") ?? Text(root, "detail") ?? Text(root, "output");
    }

    /// <summary>Outputs come as a URL, a list of URLs, a JSON string of either, or an object holding one.</summary>
    static string? FirstUrl(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                string s = e.GetString() ?? "";
                if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase) || s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return s;
                if (s.StartsWith('[') || s.StartsWith('{'))
                {
                    try
                    {
                        using var inner = JsonDocument.Parse(s);
                        return FirstUrl(inner.RootElement);
                    }
                    catch (JsonException) { }
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    if (FirstUrl(item) is string url) return url;
                return null;
            case JsonValueKind.Object:
                foreach (var name in new[] { "url", "video", "image", "output", "uri" })
                    if (e.TryGetProperty(name, out var v) && FirstUrl(v) is string found) return found;
                return null;
            default:
                return null;
        }
    }
}
