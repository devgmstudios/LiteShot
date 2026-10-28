using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;

namespace LiteShot;

internal enum AiKind { Edit, Video }

/// <summary>A Replicate model and how to hand it the screenshot.</summary>
internal sealed record AiModel(string Key, string Label, string Id, string ImageField, bool ImageAsArray, bool SupportsDuration,
    IReadOnlyDictionary<string, object>? Extras = null);

/// <summary>AI image editing and image-to-video through SyncNode → Replicate.</summary>
internal static class AiService
{
    public const string DefaultVideoPrompt = "Bring this image to life with subtle, natural motion and a slow camera push-in";

    public static readonly AiModel[] EditModels =
    {
        new("kontext-pro", "FLUX Kontext Pro", "black-forest-labs/flux-kontext-pro", "input_image", false, false,
            new Dictionary<string, object> { ["aspect_ratio"] = "match_input_image", ["output_format"] = "png", ["safety_tolerance"] = 2 }),
        new("nano-banana", "Nano Banana", "google/nano-banana", "image_input", true, false,
            new Dictionary<string, object> { ["output_format"] = "png" }),
    };

    public static readonly AiModel[] VideoModels =
    {
        new("seedance-lite", "Seedance 1 Lite", "bytedance/seedance-1-lite", "image", false, true,
            new Dictionary<string, object> { ["resolution"] = "720p" }),
        new("kling-2.1", "Kling 2.1", "kwaivgi/kling-v2.1", "start_image", false, true,
            new Dictionary<string, object> { ["mode"] = "standard" }),
        new("wan-2.2", "Wan 2.2 Fast", "wan-video/wan-2.2-i2v-fast", "image", false, false),
    };

    public static readonly string[] EditSuggestions =
    {
        "Remove the background", "Blur faces and names", "Turn it into a hand-drawn sketch",
        "Make it dark mode", "Upscale and sharpen", "Translate the text to Spanish",
    };

    public static readonly string[] VideoSuggestions =
    {
        "Slow cinematic zoom in", "Gentle parallax pan", "Bring it to life with subtle motion", "Animate the cursor clicking",
    };

    static AppSettings Settings => AppSettings.Current;

    // ───────────────────────────── key ─────────────────────────────

    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LiteShot.SyncNode");

    /// <summary>The SyncNode key, stored encrypted with Windows DPAPI so only this Windows user can read it.</summary>
    public static string? ApiKey
    {
        get
        {
            string? stored = Settings.SyncNodeKey;
            if (string.IsNullOrEmpty(stored)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
        set
        {
            Settings.SyncNodeKey = string.IsNullOrWhiteSpace(value) ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), Entropy, DataProtectionScope.CurrentUser));
        }
    }

    public static bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);

    // ───────────────────────────── models ─────────────────────────────

    public static AiModel ModelFor(AiKind kind)
    {
        if (kind == AiKind.Edit)
            return Settings.AiEditModel == "custom" && Settings.AiEditCustomModel.Trim().Length > 0
                ? new AiModel("custom", Settings.AiEditCustomModel.Trim(), Settings.AiEditCustomModel.Trim(), Field(Settings.AiEditCustomField, "input_image"), false, false)
                : EditModels.FirstOrDefault(m => m.Key == Settings.AiEditModel) ?? EditModels[0];
        return Settings.AiVideoModel == "custom" && Settings.AiVideoCustomModel.Trim().Length > 0
            ? new AiModel("custom", Settings.AiVideoCustomModel.Trim(), Settings.AiVideoCustomModel.Trim(), Field(Settings.AiVideoCustomField, "image"), false, false)
            : VideoModels.FirstOrDefault(m => m.Key == Settings.AiVideoModel) ?? VideoModels[0];
    }

    static string Field(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    public static IEnumerable<string> Suggestions(AiKind kind)
    {
        var recent = kind == AiKind.Edit ? Settings.AiEditPrompts : Settings.AiVideoPrompts;
        var defaults = kind == AiKind.Edit ? EditSuggestions : VideoSuggestions;
        return recent.Take(2).Concat(defaults).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static void RememberPrompt(AiKind kind, string prompt)
    {
        var list = kind == AiKind.Edit ? Settings.AiEditPrompts : Settings.AiVideoPrompts;
        list.RemoveAll(p => string.Equals(p, prompt, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, prompt);
        if (list.Count > 8) list.RemoveRange(8, list.Count - 8);
        Settings.Save();
    }

    // ───────────────────────────── runs ─────────────────────────────

    public static async Task<Bitmap> EditAsync(Bitmap image, string prompt, IProgress<string>? progress, CancellationToken ct)
    {
        var model = ModelFor(AiKind.Edit);
        var input = BuildInput(model, image, prompt, maxSide: 1600);
        string url = await RunAsync(model, input, progress, TimeSpan.FromMinutes(5), ct);
        progress?.Report("Downloading the result…");
        var bytes = await SyncNodeClient.DownloadAsync(url, ct);
        using var ms = new MemoryStream(bytes);
        using var decoded = new Bitmap(ms);
        var result = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
        result.SetResolution(96, 96);
        using (var g = Graphics.FromImage(result))
            g.DrawImage(decoded, new Rectangle(0, 0, decoded.Width, decoded.Height), 0, 0, decoded.Width, decoded.Height, GraphicsUnit.Pixel);
        return result;
    }

    /// <summary>Animates the image and saves the video to the screenshots folder; returns its path.</summary>
    public static async Task<string> VideoAsync(Bitmap image, string prompt, IProgress<string>? progress, CancellationToken ct)
    {
        var model = ModelFor(AiKind.Video);
        var input = BuildInput(model, image, prompt, maxSide: 1280);
        if (model.SupportsDuration) input["duration"] = Settings.AiVideoDuration >= 10 ? 10 : 5;
        string url = await RunAsync(model, input, progress, TimeSpan.FromMinutes(15), ct);
        progress?.Report("Downloading the video…");
        var bytes = await SyncNodeClient.DownloadAsync(url, ct);

        string ext = Path.GetExtension(new Uri(url, UriKind.Absolute).AbsolutePath);
        if (ext.Length is < 2 or > 5) ext = ".mp4";
        Directory.CreateDirectory(Settings.SaveFolder);
        string name = ImageOutput.BuildFileName(Settings.FileNamePattern.Replace("Screenshot", "Video"), DateTime.Now);
        string path = Path.Combine(Settings.SaveFolder, name + ext);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(Settings.SaveFolder, $"{name} ({i}){ext}");
        await File.WriteAllBytesAsync(path, bytes, ct);
        return path;
    }

    static async Task<string> RunAsync(AiModel model, Dictionary<string, object> input, IProgress<string>? progress, TimeSpan timeout, CancellationToken ct)
    {
        string key = ApiKey ?? throw new SyncNodeException("Add your SyncNode API key in Settings › Cloud & AI.");
        progress?.Report($"Sending to {model.Label}…");
        string job = await SyncNodeClient.SubmitAsync(key, model.Id, input, ct);
        return await SyncNodeClient.WaitAsync(key, job, progress, timeout, ct);
    }

    /// <summary>
    /// The screenshot travels inside the request as a JPEG data URI, so it never sits on a public host;
    /// it is scaled down first to keep the request small.
    /// </summary>
    static Dictionary<string, object> BuildInput(AiModel model, Bitmap image, string prompt, int maxSide)
    {
        string dataUri = ToDataUri(image, maxSide);
        var input = new Dictionary<string, object> { ["prompt"] = prompt };
        input[model.ImageField] = model.ImageAsArray ? new[] { dataUri } : dataUri;
        if (model.Extras != null)
            foreach (var (k, v) in model.Extras) input[k] = v;
        return input;
    }

    static string ToDataUri(Bitmap image, int maxSide)
    {
        float scale = Math.Min(1f, maxSide / (float)Math.Max(image.Width, image.Height));
        int w = Math.Max(16, (int)(image.Width * scale)), h = Math.Max(16, (int)(image.Height * scale));
        using var flat = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(flat))
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(image, new Rectangle(0, 0, w, h), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
        }
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ps = new EncoderParameters(1);
        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
        using var ms = new MemoryStream();
        flat.Save(ms, codec, ps);
        return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Turns SyncNode's errors into a next step the user can take.</summary>
    public static string Friendly(Exception ex)
    {
        string msg = ex.Message;
        if (ex is SyncNodeException { Status: 402 } || msg.Contains("Insufficient balance", StringComparison.OrdinalIgnoreCase))
            return "Your SyncNode balance is empty. Top up at syncnode.ai/billing.";
        if (msg.Contains("replicate", StringComparison.OrdinalIgnoreCase) && msg.Contains("key", StringComparison.OrdinalIgnoreCase))
            return "Add your Replicate token under Connected Provider Credentials at syncnode.ai/api_keys.";
        if (ex is SyncNodeException { Status: 401 } || msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) || msg.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase))
            return "SyncNode didn't accept the API key. Check it in Settings › Cloud & AI.";
        if (ex is HttpRequestException) return "Couldn't reach SyncNode. Check your internet connection.";
        return msg;
    }
}
