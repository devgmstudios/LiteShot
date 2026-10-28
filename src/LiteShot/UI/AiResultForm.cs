using System.Drawing.Drawing2D;

namespace LiteShot;

/// <summary>
/// Shows an AI edit next to its original (hold the image to compare), keeps every version, and lets you
/// refine with another prompt, turn it into a video, or copy, save and pin it.
/// </summary>
internal sealed class AiResultForm : Form
{
    readonly TrayApp host;
    readonly SettingsTheme theme = new();
    readonly Bitmap original;
    readonly List<Bitmap> versions = new();
    readonly ResultView view;
    readonly Segmented mode;
    readonly FieldBox prompt;
    readonly PillButton generate, prev, next;
    readonly Label status;
    int index;
    CancellationTokenSource? cts;

    Bitmap Current => versions[index];

    public AiResultForm(TrayApp host, Bitmap original, Bitmap result, string firstPrompt)
    {
        this.host = host;
        this.original = original;
        versions.Add(result);

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10F);
        Text = "LiteShot — AI edit";
        Icon = TrayApp.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = theme.Window;
        ForeColor = theme.Text;
        KeyPreview = true;
        MinimumSize = new Size(680, 520);
        float aspect = result.Width / (float)Math.Max(1, result.Height);
        int imageW = Math.Clamp((int)(560 * aspect), 640, 1040);
        ClientSize = new Size(imageW + 32, Math.Clamp((int)(imageW / aspect), 360, 640) + 32 + 112);

        view = new ResultView(theme, this) { Dock = DockStyle.Fill };
        mode = new Segmented(theme, 76, "Edit", "Video");
        prompt = new FieldBox(theme);
        Native.SetCueBanner(prompt.Box, "Describe another change, e.g. “make the background blue”");
        prompt.Box.Text = "";
        prompt.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Generate(); }
        };
        mode.SelectedChanged += (_, _) =>
        {
            Native.SetCueBanner(prompt.Box, mode.SelectedIndex == 0
                ? "Describe another change, e.g. “make the background blue”"
                : "Describe the motion, e.g. “slow zoom in” (optional)");
            generate!.Text = mode.SelectedIndex == 0 ? "Generate" : "Make video";
            generate.Invalidate();
        };
        generate = new PillButton(theme, "Generate", 124, primary: true);
        generate.Click += (_, _) => Generate();
        prev = new PillButton(theme, "‹", 40);
        prev.Click += (_, _) => ShowVersion(index - 1);
        next = new PillButton(theme, "›", 40);
        next.Click += (_, _) => ShowVersion(index + 1);
        status = new Label { AutoSize = false, ForeColor = theme.SubText, TextAlign = ContentAlignment.MiddleLeft, Text = $"“{firstPrompt}”", AutoEllipsis = true };
        var copy = new PillButton(theme, "Copy", 84);
        copy.Click += (_, _) => Copy();
        var save = new PillButton(theme, "Save", 84);
        save.Click += (_, _) => Save();
        var pin = new PillButton(theme, "Pin", 72);
        pin.Click += (_, _) => host.Pin((Bitmap)Current.Clone(), new Point(Left + 40, Top + 60));

        var bar = new BottomBar(theme,
            new Control[] { mode, prompt, generate },
            new Control[] { prev, next, status, copy, save, pin }) { Dock = DockStyle.Bottom, Height = 112 };
        Controls.Add(view);
        Controls.Add(bar);
        ResumeLayout(false);
        PerformLayout();
        UpdateNav();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyWindowChrome(Handle, theme.Dark);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: Close(); return true;
            case Keys.Control | Keys.C when !prompt.Box.Focused || prompt.Box.SelectionLength == 0: Copy(); return true;
            case Keys.Control | Keys.S: Save(); return true;
            case Keys.Left when !prompt.Box.Focused: ShowVersion(index - 1); return true;
            case Keys.Right when !prompt.Box.Focused: ShowVersion(index + 1); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public Bitmap Original => original;
    public Bitmap ShownImage => Current;
    public bool Busy { get; private set; }
    public string BusyText { get; private set; } = "";

    void ShowVersion(int i)
    {
        if (Busy || i < 0 || i >= versions.Count) return;
        index = i;
        UpdateNav();
        view.Invalidate();
    }

    void UpdateNav()
    {
        prev.Enabled = index > 0;
        next.Enabled = index < versions.Count - 1;
        prev.Invalidate();
        next.Invalidate();
        view.Badge = versions.Count > 1 ? $"Edited · {index + 1} of {versions.Count}" : "Edited";
    }

    void SetBusy(bool busy, string text = "")
    {
        Busy = busy;
        BusyText = text;
        generate.Enabled = !busy;
        prompt.Box.Enabled = !busy;
        generate.Invalidate();
        view.SetBusy(busy);
    }

    async void Generate()
    {
        if (Busy) return;
        string text = prompt.Box.Text.Trim();
        if (mode.SelectedIndex == 1)
        {
            host.RunAi((Bitmap)Current.Clone(), AiKind.Video, text.Length > 0 ? text : AiService.DefaultVideoPrompt);
            status.Text = "Making the video — LiteShot will let you know when it's ready.";
            return;
        }
        if (text.Length == 0)
        {
            status.Text = "Describe the change you want first.";
            prompt.Box.Focus();
            return;
        }
        AiService.RememberPrompt(AiKind.Edit, text);
        cts = new CancellationTokenSource();
        SetBusy(true, "Sending…");
        var progress = new Progress<string>(s => { BusyText = s; view.Invalidate(); });
        try
        {
            var result = await AiService.EditAsync(Current, text, progress, cts.Token);
            if (IsDisposed) { result.Dispose(); return; }
            // Editing from an older version drops the versions after it, like undo history.
            for (int i = versions.Count - 1; i > index; i--)
            {
                versions[i].Dispose();
                versions.RemoveAt(i);
            }
            versions.Add(result);
            index = versions.Count - 1;
            status.Text = $"“{text}”";
            prompt.Box.Text = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed) status.Text = AiService.Friendly(ex);
        }
        finally
        {
            if (!IsDisposed)
            {
                SetBusy(false);
                UpdateNav();
            }
        }
    }

    void Copy()
    {
        try
        {
            ImageOutput.CopyToClipboard(Current);
            status.Text = "Copied to the clipboard.";
        }
        catch (Exception ex) { status.Text = ex.Message; }
    }

    void Save()
    {
        try
        {
            if (ImageOutput.SaveWithDialog(this, Current) is string path)
            {
                status.Text = $"Saved {Path.GetFileName(path)}";
                host.NotifySaved(path);
            }
        }
        catch (Exception ex) { status.Text = ex.Message; }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        cts?.Cancel();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var v in versions) v.Dispose();
            original.Dispose();
            cts?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Two rows: [mode][prompt…][generate] and [‹][›][status…][copy][save][pin].</summary>
    sealed class BottomBar : Panel
    {
        readonly Control[] top, bottom;

        public BottomBar(SettingsTheme theme, Control[] top, Control[] bottom)
        {
            this.top = top;
            this.bottom = bottom;
            BackColor = theme.Window;
            foreach (var c in top.Concat(bottom))
            {
                c.BackColor = theme.Window;
                Controls.Add(c);
            }
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            float s = DeviceDpi / 96f;
            int pad = (int)(16 * s), gap = (int)(8 * s), rowH = (int)(36 * s);
            int y1 = (int)(10 * s), y2 = y1 + rowH + (int)(12 * s);
            Row(top, 1, y1);
            Row(bottom, 2, y2);

            void Row(Control[] row, int stretch, int y)
            {
                int left = pad, right = Width - pad;
                for (int i = 0; i < stretch; i++)
                {
                    var c = row[i];
                    c.SetBounds(left, y + (rowH - c.Height) / 2, c.Width, c.Height);
                    left += c.Width + gap;
                }
                for (int i = row.Length - 1; i > stretch; i--)
                {
                    var c = row[i];
                    right -= c.Width;
                    c.SetBounds(right, y + (rowH - c.Height) / 2, c.Width, c.Height);
                    right -= gap;
                }
                var fill = row[stretch];
                fill.SetBounds(left, y + (rowH - Math.Min(fill.Height, rowH)) / 2, Math.Max(40, right - left), Math.Min(Math.Max(fill.Height, (int)(30 * s)), rowH));
            }
        }
    }

    /// <summary>The image, fitted with rounded corners; press and hold to see the original.</summary>
    sealed class ResultView : Control
    {
        readonly SettingsTheme theme;
        readonly AiResultForm owner;
        readonly System.Windows.Forms.Timer spin = new() { Interval = 33 };
        float angle;
        bool comparing;
        public string Badge = "Edited";

        public ResultView(SettingsTheme theme, AiResultForm owner)
        {
            this.theme = theme;
            this.owner = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = theme.Window;
            Cursor = Cursors.Hand;
            spin.Tick += (_, _) => { angle = (angle + 9) % 360; Invalidate(); };
        }

        public void SetBusy(bool busy)
        {
            if (busy && SystemTheme.AnimationsEnabled) spin.Start(); else spin.Stop();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); comparing = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); comparing = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            float s = DeviceDpi / 96f, pad = 16 * s;
            var image = comparing ? owner.Original : owner.ShownImage;
            var area = new RectangleF(pad, pad, Width - pad * 2, Height - pad);
            float k = Math.Min(area.Width / image.Width, area.Height / image.Height);
            var r = new RectangleF(area.X + (area.Width - image.Width * k) / 2, area.Y + (area.Height - image.Height * k) / 2, image.Width * k, image.Height * k);
            using var clip = Ui.RoundRect(r, 10 * s);
            var state = g.Save();
            g.SetClip(clip);
            g.DrawImage(image, r);
            if (owner.Busy)
            {
                using var veil = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
                g.FillRectangle(veil, r);
            }
            g.Restore(state);
            using (var edge = new Pen(theme.CardBorder, 1f)) g.DrawPath(edge, clip);

            using var font = Ui.MakeFont(12.5f * s, semibold: true);
            Pill(g, comparing ? "Original" : Badge, font, new PointF(r.X + 12 * s, r.Y + 12 * s), s);
            if (!owner.Busy && !comparing)
            {
                using var small = Ui.MakeFont(12f * s);
                var hint = "Hold to compare with the original";
                var hs = Ui.Measure(hint, small);
                Pill(g, hint, small, new PointF(r.Right - hs.Width - 32 * s, r.Bottom - hs.Height - 26 * s), s);
            }
            if (owner.Busy)
            {
                float d = 44 * s;
                var c = new RectangleF(r.X + r.Width / 2 - d / 2, r.Y + r.Height / 2 - d / 2 - 14 * s, d, d);
                using (var track = new Pen(Color.FromArgb(70, 255, 255, 255), 4 * s)) g.DrawEllipse(track, c);
                using (var arc = new Pen(Color.White, 4 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(arc, c, angle, 100);
                Ui.DrawTextCentered(g, owner.BusyText, font, Color.White, new RectangleF(r.X, c.Bottom + 10 * s, r.Width, 24 * s));
            }
        }

        static void Pill(Graphics g, string text, Font font, PointF at, float s)
        {
            var ts = Ui.Measure(text, font);
            var r = new RectangleF(at.X, at.Y, ts.Width + 20 * s, ts.Height + 10 * s);
            using var path = Ui.RoundRect(r, r.Height / 2);
            using var bg = new SolidBrush(Color.FromArgb(185, 24, 24, 28));
            g.FillPath(bg, path);
            Overlay.GlassMaterial.DrawRim(g, r, r.Height / 2, s, 0.5f);
            Ui.DrawTextCentered(g, text, font, Color.White, r);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) spin.Dispose();
            base.Dispose(disposing);
        }
    }
}
