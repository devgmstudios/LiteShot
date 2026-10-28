using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LiteShot;

internal static class ImageOutput
{
    public static byte[] ToPng(Bitmap img)
    {
        using var ms = new MemoryStream();
        using (var flat = ToRgb(img)) flat.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>Copies as both a bitmap and PNG, so apps that prefer lossless PNG (browsers, chat apps) get it.</summary>
    public static void CopyToClipboard(Bitmap img)
    {
        var data = new DataObject();
        data.SetData(DataFormats.Bitmap, true, img);
        data.SetData("PNG", false, new MemoryStream(ToPng(img)));
        Clipboard.SetDataObject(data, true, 10, 100);
    }

    public static string Extension => AppSettings.Current.ImageFormat switch
    {
        "jpg" => ".jpg",
        "bmp" => ".bmp",
        _ => ".png",
    };

    public static string BuildFileName(string pattern, DateTime time)
    {
        string name = Regex.Replace(pattern, @"\{([^{}]+)\}", m =>
        {
            try { return time.ToString(m.Groups[1].Value, CultureInfo.InvariantCulture); }
            catch (FormatException) { return m.Value; }
        });
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "Screenshot" : name.Trim();
    }

    public static string SaveToFolder(Bitmap img)
    {
        var settings = AppSettings.Current;
        Directory.CreateDirectory(settings.SaveFolder);
        string path = UniquePath(Path.Combine(settings.SaveFolder, BuildFileName(settings.FileNamePattern, DateTime.Now) + Extension));
        Save(img, path);
        return path;
    }

    static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public static void Save(Bitmap img, string path)
    {
        using var flat = ToRgb(img);
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".jpg":
            case ".jpeg":
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using (var ps = new EncoderParameters(1))
                {
                    ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(AppSettings.Current.JpegQuality, 1, 100));
                    flat.Save(path, codec, ps);
                }
                break;
            case ".bmp":
                flat.Save(path, ImageFormat.Bmp);
                break;
            default:
                flat.Save(path, ImageFormat.Png);
                break;
        }
    }

    /// <summary>Screenshots are opaque; dropping the alpha channel keeps files smaller.</summary>
    static Bitmap ToRgb(Bitmap img)
    {
        var flat = new Bitmap(img.Width, img.Height, PixelFormat.Format24bppRgb);
        flat.SetResolution(96, 96);
        using var g = Graphics.FromImage(flat);
        g.Clear(Color.White);
        g.CompositingMode = CompositingMode.SourceOver;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel);
        return flat;
    }

    public static string? SaveWithDialog(IWin32Window owner, Bitmap img)
    {
        var settings = AppSettings.Current;
        string dir = Directory.Exists(settings.LastSaveDirectory) ? settings.LastSaveDirectory! : settings.SaveFolder;
        try { Directory.CreateDirectory(dir); } catch { /* the dialog falls back to its own default */ }
        using var dialog = new SaveFileDialog
        {
            Title = "Save screenshot",
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg|Bitmap (*.bmp)|*.bmp",
            FilterIndex = settings.ImageFormat switch { "jpg" => 2, "bmp" => 3, _ => 1 },
            FileName = BuildFileName(settings.FileNamePattern, DateTime.Now) + Extension,
            InitialDirectory = dir,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
        Save(img, dialog.FileName);
        settings.LastSaveDirectory = Path.GetDirectoryName(dialog.FileName);
        settings.Save();
        return dialog.FileName;
    }

    public static bool Print(IWin32Window owner, Bitmap img)
    {
        using var doc = new PrintDocument { DocumentName = "LiteShot screenshot" };
        doc.DefaultPageSettings.Landscape = img.Width > img.Height;
        doc.PrintPage += (_, e) =>
        {
            var m = e.MarginBounds;
            float w = img.Width * 100f / 96f, h = img.Height * 100f / 96f;
            float k = Math.Min(1f, Math.Min(m.Width / w, m.Height / h));
            w *= k;
            h *= k;
            e.Graphics!.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(img, m.X + (m.Width - w) / 2, m.Y + (m.Height - h) / 2, w, h);
        };
        using var dialog = new PrintDialog { Document = doc, UseEXDialog = true };
        if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
        doc.Print();
        return true;
    }

    public static void RevealInExplorer(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch { /* Explorer unavailable */ }
    }

    public static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start("explorer.exe", $"\"{folder}\"");
        }
        catch { /* Explorer unavailable */ }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no default browser */ }
    }
}
