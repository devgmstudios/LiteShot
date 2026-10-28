using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LiteShot.Overlay;

internal sealed partial class CaptureOverlay
{
    static readonly (string Name, string Url)[] ShareTargets =
    {
        ("X (Twitter)", "https://twitter.com/intent/tweet?url={0}"),
        ("Facebook", "https://www.facebook.com/sharer/sharer.php?u={0}"),
        ("Reddit", "https://www.reddit.com/submit?url={0}"),
        ("Pinterest", "https://pinterest.com/pin/create/button/?url={0}&media={0}"),
        ("VK", "https://vk.com/share.php?url={0}"),
        ("Email", "mailto:?subject=Screenshot&body={0}"),
    };

    /// <summary>Crops the frozen screen to the selection and burns the annotations in.</summary>
    Bitmap RenderResult()
    {
        CommitText();
        ApplyNameBlurs(wait: true);
        var img = new Bitmap(sel.Width, sel.Height, PixelFormat.Format32bppArgb);
        img.SetResolution(96, 96);
        using var g = Graphics.FromImage(img);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(shot, new Rectangle(0, 0, sel.Width, sel.Height), sel.X, sel.Y, sel.Width, sel.Height, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(-sel.X, -sel.Y);
        foreach (var a in shapes) a.Draw(g, shot);
        return img;
    }

    void Finish()
    {
        host.RememberRegion(ScreenSel);
        CloseOverlay();
    }

    static void SaveCopyIfEnabled(Bitmap img)
    {
        if (!AppSettings.Current.AutoSaveCopies) return;
        try { ImageOutput.SaveToFolder(img); }
        catch { /* the main action still succeeded */ }
    }

    void DoCopy()
    {
        if (!hasSel || closing) return;
        using var img = RenderResult();
        try
        {
            ImageOutput.CopyToClipboard(img);
        }
        catch (Exception ex)
        {
            host.Notify("Couldn't copy the screenshot", ex.Message, error: true);
            return;
        }
        SaveCopyIfEnabled(img);
        Finish();
    }

    void DoSave()
    {
        if (!hasSel || closing) return;
        using var img = RenderResult();
        string? path;
        try
        {
            if (AppSettings.Current.AskWhereToSave)
            {
                HideTooltip();
                path = ImageOutput.SaveWithDialog(this, img);
                Invalidate();
                if (path == null) return;
            }
            else path = ImageOutput.SaveToFolder(img);
        }
        catch (Exception ex)
        {
            host.Notify("Couldn't save the screenshot", ex.Message, error: true);
            return;
        }
        host.NotifySaved(path);
        Finish();
    }

    void DoPrint()
    {
        if (!hasSel || closing) return;
        HideTooltip();
        using var img = RenderResult();
        bool printed;
        try { printed = ImageOutput.Print(this, img); }
        catch (Exception ex)
        {
            host.Notify("Couldn't print", ex.Message, error: true);
            printed = false;
        }
        if (printed) Finish();
        else Invalidate();
    }

    void DoUpload(UploadKind kind, string? shareTemplate)
    {
        if (!hasSel || closing) return;
        var img = RenderResult();
        SaveCopyIfEnabled(img);
        Finish();
        host.Upload(img, kind, shareTemplate);
    }

    void ShowShareMenu()
    {
        if (!hasSel || closing) return;
        HideTooltip();
        var menu = new ContextMenuStrip { Renderer = new ModernMenuRenderer(), ShowImageMargin = false };
        foreach (var (name, url) in ShareTargets)
            menu.Items.Add(new ToolStripMenuItem(name, null, (_, _) => DoUpload(UploadKind.Share, url)) { Padding = new Padding(4, 3, 4, 3) });
        ModernMenuRenderer.Attach(menu);
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        var b = shareButton.Bounds;
        menu.Show(this, new Point((int)b.Left, (int)b.Bottom + Px(8)));
    }

    void DoPin()
    {
        if (!hasSel || closing) return;
        var img = RenderResult();
        var location = ScreenSel.Location;
        Finish();
        host.Pin(img, location);
    }

    void DoOcr()
    {
        if (!hasSel || closing) return;
        var img = RenderResult();
        Finish();
        host.Ocr(img);
    }

    void CopyColor()
    {
        string hex = Hex(PixelAt(mouse));
        try
        {
            Clipboard.SetText(hex);
            ShowFlash($"Copied {hex}");
        }
        catch (Exception ex)
        {
            ShowFlash(ex.Message);
        }
    }
}
