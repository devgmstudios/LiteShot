using System.Drawing.Drawing2D;

namespace LiteShot;

/// <summary>Windows 11 style menus: rounded corners from DWM, rounded hover highlights, light or dark to match the system.</summary>
internal sealed class ModernMenuRenderer : ToolStripProfessionalRenderer
{
    static readonly bool RoundedBySystem = Environment.OSVersion.Version.Build >= 22000;
    readonly bool dark = SystemTheme.Dark;

    public ModernMenuRenderer() : base(new MenuColors(SystemTheme.Dark)) => RoundedEdges = false;

    Color Fore => SystemTheme.HighContrast ? SystemColors.MenuText : dark ? Color.FromArgb(242, 242, 242) : Color.FromArgb(26, 26, 26);
    Color Muted => dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(130, 130, 130);

    /// <summary>Rounds the menu's popup window (and its submenus) through DWM as soon as each is created.</summary>
    public static void Attach(ToolStripDropDown menu)
    {
        void Round(object? sender, EventArgs e)
        {
            if (sender is Control c) Native.ApplyWindowChrome(c.Handle, SystemTheme.Dark, Native.DWMWCP_ROUND, mica: false);
        }
        menu.HandleCreated += Round;
        foreach (var item in menu.Items.OfType<ToolStripMenuItem>())
            if (item.HasDropDownItems || item.DropDownItems.Count > 0) Attach(item.DropDown);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(MenuColors.Background(dark));
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (RoundedBySystem) return; // DWM draws the border of rounded windows
        using var p = new Pen(dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(214, 214, 214));
        e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    /// <summary>Checked items get a small accent tile with a white check, like a Windows 11 toggle.</summary>
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = RectangleF.Inflate(e.ImageRectangle, -1, -1);
        using var tile = Ui.RoundRect(r, r.Height * 0.3f);
        using (var b = new SolidBrush(SystemTheme.HighContrast ? SystemColors.Highlight : SystemTheme.Accent)) g.FillPath(b, tile);
        Icons.Draw(g, RectangleF.Inflate(r, -1, -1), SystemTheme.HighContrast ? SystemColors.HighlightText : Color.White, Icons.Check, 2.6f);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = e.Item.Owner?.DeviceDpi / 96f ?? 1f;
        var r = new RectangleF(4 * s, 1 * s, e.Item.Width - 8 * s, e.Item.Height - 2 * s);
        using var path = Ui.RoundRect(r, 5 * s);
        using var b = new SolidBrush(SystemTheme.HighContrast ? SystemColors.Highlight
            : dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0));
        g.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? Muted
            : SystemTheme.HighContrast && e.Item.Selected ? SystemColors.HighlightText : Fore;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Fore;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var p = new Pen(dark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(226, 226, 226));
        e.Graphics.DrawLine(p, 10, y, e.Item.Width - 10, y);
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        readonly bool dark;
        public MenuColors(bool dark) { this.dark = dark; UseSystemColors = false; }
        public static Color Background(bool dark) => SystemTheme.HighContrast ? SystemColors.Menu : dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
        public override Color ToolStripDropDownBackground => Background(dark);
        public override Color ImageMarginGradientBegin => Background(dark);
        public override Color ImageMarginGradientMiddle => Background(dark);
        public override Color ImageMarginGradientEnd => Background(dark);
        public override Color MenuBorder => Background(dark);
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Background(dark);
    }
}
