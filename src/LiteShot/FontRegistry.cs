using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace LiteShot;

/// <summary>
/// Fonts for the text tool: a curated set (system fonts plus open-licensed fonts bundled in the exe)
/// and any font file the user adds, which is copied to %APPDATA%\LiteShot\Fonts.
/// Bundled and added fonts are registered with both GDI+ (drawing) and GDI (menus), privately to this process.
/// </summary>
internal static class FontRegistry
{
    public sealed record Choice(string Family, string Label);

    /// <summary>Shown first in the font menu, each in its own typeface.</summary>
    public static readonly Choice[] Curated =
    {
        new("Segoe UI Semibold", "Clean"),
        new("Poppins SemiBold", "Modern"),
        new("Georgia", "Serif"),
        new("Cascadia Mono", "Code"),
        new("Permanent Marker", "Marker"),
        new("Patrick Hand", "Handwritten"),
        new("Pacifico", "Script"),
        new("Bebas Neue", "Headline"),
        new("Anton", "Impact"),
        new("Archivo Black", "Heavy"),
    };

    public const string DefaultFamily = "Segoe UI Semibold";

    static readonly PrivateFontCollection collection = new();
    static FontFamily[] privateFamilies = Array.Empty<FontFamily>();
    static readonly object gate = new();

    public static string UserFontDirectory => Path.Combine(AppSettings.Directory, "Fonts");

    static FontRegistry()
    {
        var asm = typeof(FontRegistry).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("LiteShot.Fonts.", StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) continue;
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            // GDI+ reads the font from this memory for the life of the process, so it is never freed.
            IntPtr mem = Marshal.AllocCoTaskMem(data.Length);
            Marshal.Copy(data, 0, mem, data.Length);
            try
            {
                collection.AddMemoryFont(mem, data.Length);
                Native.AddFontMemResourceEx(mem, (uint)data.Length, IntPtr.Zero, out _);
            }
            catch { /* a broken font is skipped */ }
        }
        try
        {
            if (Directory.Exists(UserFontDirectory))
                foreach (var file in Directory.EnumerateFiles(UserFontDirectory).Where(IsFontFile))
                    LoadFile(file);
        }
        catch { /* unreadable folder */ }
        privateFamilies = collection.Families;
    }

    static bool IsFontFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf";

    static void LoadFile(string path)
    {
        try
        {
            collection.AddFontFile(path);
            Native.AddFontResourceEx(path, Native.FR_PRIVATE, IntPtr.Zero);
        }
        catch { /* not a usable font */ }
    }

    /// <summary>Families the user added from font files.</summary>
    public static IReadOnlyList<string> UserFamilies
    {
        get
        {
            var bundled = Curated.Select(c => c.Family).ToHashSet(StringComparer.OrdinalIgnoreCase);
            lock (gate) return privateFamilies.Select(f => f.Name).Where(n => !bundled.Contains(n)).ToList();
        }
    }

    public static IEnumerable<string> InstalledFamilies =>
        new InstalledFontCollection().Families.Select(f => f.Name);

    public static FontFamily Resolve(string? name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            lock (gate)
                foreach (var f in privateFamilies)
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
            try { return new FontFamily(name); }
            catch (ArgumentException) { }
        }
        return Ui.SemiboldFamily;
    }

    public static bool IsAvailable(string name)
    {
        lock (gate)
            if (privateFamilies.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))) return true;
        try { using var _ = new FontFamily(name); return true; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Copies a font file into LiteShot's font folder, loads it and returns its family name.</summary>
    public static string? AddUserFont(string path)
    {
        Directory.CreateDirectory(UserFontDirectory);
        string target = Path.Combine(UserFontDirectory, Path.GetFileName(path));
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            File.Copy(path, target, overwrite: true);

        lock (gate)
        {
            var before = privateFamilies.Select(f => f.Name).ToHashSet();
            LoadFile(target);
            privateFamilies = collection.Families;
            var added = privateFamilies.Select(f => f.Name).FirstOrDefault(n => !before.Contains(n));
            if (added != null) return added;
        }
        // Already loaded earlier, or the family name differs from the file name: read it back.
        using var probe = new PrivateFontCollection();
        probe.AddFontFile(target);
        return probe.Families.FirstOrDefault()?.Name;
    }
}
