using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteShot;

internal sealed class UploadRecord
{
    public string Url { get; set; } = "";
    public DateTime Time { get; set; }
    public DateTime? Expires { get; set; }
    /// <summary>fortefile.com file id and the token that deletes it early (none for links from older versions).</summary>
    public string? Id { get; set; }
    public string? DeleteToken { get; set; }
    public bool Expired => Expires is DateTime e && e <= DateTime.Now;
}

internal sealed class AppSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))] public Keys HotkeyCapture { get; set; } = Keys.PrintScreen;
    [JsonConverter(typeof(JsonStringEnumConverter))] public Keys HotkeyFullScreen { get; set; } = Keys.Shift | Keys.PrintScreen;
    [JsonConverter(typeof(JsonStringEnumConverter))] public Keys HotkeyLastRegion { get; set; } = Keys.Control | Keys.PrintScreen;

    public string SaveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LiteShot");
    public string FileNamePattern { get; set; } = "Screenshot_{yyyy-MM-dd_HH-mm-ss}";
    public string ImageFormat { get; set; } = "png";
    public int JpegQuality { get; set; } = 92;
    public bool AskWhereToSave { get; set; } = true;
    public bool AutoSaveCopies { get; set; }
    public string? LastSaveDirectory { get; set; }

    /// <summary>How long uploaded links live on fortefile.com: 1h, 12h or 24h.</summary>
    public string UploadLifetime { get; set; } = "24h";
    /// <summary>Overrides the upload server (for testing a local fortefile).</summary>
    public string? FortefileServer { get; set; }

    /// <summary>SyncNode API key, encrypted for the current Windows user (see <see cref="AiService.ApiKey"/>).</summary>
    public string? SyncNodeKey { get; set; }
    public string AiEditModel { get; set; } = "kontext-pro";
    public string AiEditCustomModel { get; set; } = "";
    public string AiEditCustomField { get; set; } = "input_image";
    public string AiVideoModel { get; set; } = "seedance-lite";
    public string AiVideoCustomModel { get; set; } = "";
    public string AiVideoCustomField { get; set; } = "image";
    public int AiVideoDuration { get; set; } = 5;
    public List<string> AiEditPrompts { get; set; } = new();
    public List<string> AiVideoPrompts { get; set; } = new();

    public bool ShowMagnifier { get; set; }
    public bool DetectWindows { get; set; } = true;
    public bool CaptureCursor { get; set; }
    /// <summary>Blur text that looks like a person's name as soon as a screenshot is taken.</summary>
    public bool AutoBlurNames { get; set; }
    public bool ShowNotifications { get; set; } = true;
    /// <summary>Off unless the user opts in: check for, download and install updates on its own.</summary>
    public bool AutoUpdate { get; set; }
    public string UpdateChannel { get; set; } = "stable";
    /// <summary>Overrides the update server built into the app (for testing a local server).</summary>
    public string? UpdateServer { get; set; }
    public DateTime? LastUpdateCheck { get; set; }
    public bool FirstRunShown { get; set; }

    public int AnnotationColor { get; set; } = unchecked((int)0xFFFF3B30);
    public string TextFamily { get; set; } = "Segoe UI Semibold";
    public bool TextBold { get; set; }
    public bool TextItalic { get; set; }
    public string TextAlign { get; set; } = "Left";
    public string TextLook { get; set; } = "Plain";
    public Dictionary<string, float> ToolSizes { get; set; } = new();
    public int[]? LastRegion { get; set; }
    public List<UploadRecord> Uploads { get; set; } = new();

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteShot");
    static string FilePath => Path.Combine(Directory, "settings.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = Load();

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch { /* a corrupt file falls back to defaults */ }
        return new();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch { /* settings are best effort */ }
    }
}
