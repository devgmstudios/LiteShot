using System.Drawing.Drawing2D;

namespace LiteShot;

/// <summary>
/// Ask AI: a chat about one screenshot. The picture sits at the top, the conversation below it, and questions go to the
/// model chosen in Settings › Cloud &amp; AI through SyncNode → OpenRouter. Enter sends; the answers can be selected and copied.
/// </summary>
internal sealed class AskAiForm : Form
{
    readonly SettingsTheme theme = new();
    readonly Bitmap screenshot;
    readonly AskAi chat;
    readonly Func<string?> key;
    readonly Panel transcript;
    readonly FlowLayoutPanel chips;
    readonly FieldBox input;
    readonly PillButton send;
    readonly Label status;
    readonly System.Windows.Forms.Timer dots = new() { Interval = 400 };
    Bubble? thinking;
    CancellationTokenSource? cts;

    /// <param name="key">Where the SyncNode key comes from; the saved one unless the harness passes its own.</param>
    public AskAiForm(Bitmap screenshot, Func<string?>? key = null)
    {
        this.screenshot = screenshot;
        this.key = key ?? (() => AiService.ApiKey);
        chat = new AskAi(screenshot);

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10.5F);
        Text = "LiteShot — Ask AI";
        Icon = TrayApp.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = theme.Window;
        ForeColor = theme.Text;
        KeyPreview = true;
        MinimumSize = new Size(560, 560);
        ClientSize = new Size(760, 760);

        var preview = new Preview(theme, screenshot) { Dock = DockStyle.Top, Height = 220 };
        transcript = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = theme.Window, Padding = new Padding(16, 8, 16, 8) };
        transcript.Resize += (_, _) => LayoutTranscript();

        chips = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14, 6, 14, 2),
            BackColor = theme.Window, WrapContents = true,
        };
        // Widths in 96-dpi units, like every other size here; the form scales them with the rest.
        float dpi = DeviceDpi / 96f;
        foreach (string s in AskAi.Suggestions)
        {
            var chip = new PillButton(theme, s, (int)(TextRenderer.MeasureText(s, Font).Width / dpi) + 28) { Margin = new Padding(3), BackColor = theme.Window };
            chip.Click += (_, _) => Ask(s);
            chips.Controls.Add(chip);
        }

        input = new FieldBox(theme) { Height = 40 };
        Native.SetCueBanner(input.Box, "Ask anything about this screenshot…");
        input.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Ask(input.Box.Text); }
        };
        send = new PillButton(theme, "Ask", 84, primary: true);
        send.Click += (_, _) => Ask(input.Box.Text);
        status = new Label
        {
            AutoSize = false, ForeColor = theme.SubText, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false,
            Text = $"{AskAi.LabelFor(AskAi.ModelId)} through SyncNode · change it in Settings › Cloud & AI",
        };
        var bar = new InputBar(theme, input, send, status) { Dock = DockStyle.Bottom, Height = 92 };

        Controls.Add(transcript);
        Controls.Add(chips);
        Controls.Add(bar);
        Controls.Add(preview);
        ResumeLayout(false);
        PerformLayout();

        dots.Tick += (_, _) =>
        {
            if (thinking == null) return;
            int n = (thinking.Text.Length - "Thinking".Length + 1) % 4;
            thinking.SetText("Thinking" + new string('.', n));
            LayoutTranscript();
        };
        Shown += (_, _) => input.Box.Focus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyWindowChrome(Handle, theme.Dark);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        cts?.Cancel();
        dots.Dispose();
        screenshot.Dispose();
        base.OnFormClosed(e);
    }

    public bool Busy => cts != null;

    /// <summary>The conversation as shown, for the harness: who said what.</summary>
    public IEnumerable<(bool Mine, string Text)> Messages => transcript.Controls.OfType<Bubble>().Select(b => (b.Mine, b.Text));

    /// <summary>Sends a question; the reply (or what went wrong) appears in the chat.</summary>
    public async void Ask(string question)
    {
        question = question.Trim();
        if (question.Length == 0 || Busy) return;
        string? apiKey = key();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            status.Text = "Add your SyncNode API key in Settings › Cloud & AI first.";
            return;
        }
        input.Box.Text = "";
        chips.Visible = false;
        Add(new Bubble(theme, question, mine: true));
        thinking = new Bubble(theme, "Thinking", mine: false, quiet: true);
        Add(thinking);
        dots.Start();
        SetBusy(true);
        string model = AskAi.ModelId;
        try
        {
            string answer = await chat.AskAsync(question, apiKey, model, cts!.Token);
            Replace(thinking, new Bubble(theme, AskAi.Plain(answer), mine: false));
            status.Text = $"{AskAi.LabelFor(model)} through SyncNode · each message costs $0.02 plus the model's price";
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            Replace(thinking, new Bubble(theme, AiService.Friendly(ex), mine: false, error: true));
            input.Box.Text = question;
        }
        finally
        {
            if (!IsDisposed)
            {
                thinking = null;
                dots.Stop();
                SetBusy(false);
                input.Box.Focus();
            }
        }
    }

    void SetBusy(bool busy)
    {
        if (busy) cts = new CancellationTokenSource();
        else { cts?.Dispose(); cts = null; }
        send.Enabled = !busy;
        input.Box.ReadOnly = busy;
        send.Invalidate();
    }

    void Add(Bubble b)
    {
        transcript.Controls.Add(b);
        LayoutTranscript();
        transcript.ScrollControlIntoView(b);
    }

    void Replace(Bubble? old, Bubble fresh)
    {
        if (old != null) { transcript.Controls.Remove(old); old.Dispose(); }
        Add(fresh);
    }

    /// <summary>Bubbles stack top to bottom: answers on the left, questions on the right, each at most 82% wide.</summary>
    void LayoutTranscript()
    {
        float s = DeviceDpi / 96f;
        int width = transcript.ClientSize.Width - transcript.Padding.Horizontal;
        int y = transcript.Padding.Top + transcript.AutoScrollPosition.Y, gap = (int)(10 * s);
        transcript.SuspendLayout();
        foreach (var b in transcript.Controls.OfType<Bubble>())
        {
            var size = b.Measure((int)(width * 0.82f));
            int x = b.Mine ? transcript.Padding.Left + width - size.Width : transcript.Padding.Left;
            b.SetBounds(x, y, size.Width, size.Height);
            y += size.Height + gap;
        }
        transcript.ResumeLayout();
    }

    /// <summary>One message: a rounded bubble around selectable text.</summary>
    sealed class Bubble : Panel
    {
        readonly SettingsTheme theme;
        readonly TextBox box;
        readonly bool error, quiet;
        public readonly bool Mine;

        public Bubble(SettingsTheme theme, string text, bool mine, bool error = false, bool quiet = false)
        {
            this.theme = theme;
            Mine = mine;
            this.error = error;
            this.quiet = quiet;
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = theme.Window;
            box = new TextBox
            {
                Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, TabStop = false, WordWrap = true,
                BackColor = Fill, ForeColor = Ink, Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            };
            Controls.Add(box);
        }

        public override string Text => box?.Text.Replace("\r\n", "\n") ?? "";

        public void SetText(string text) => box.Text = text;

        Color Fill => theme.HighContrast ? SystemColors.Window
            : Mine ? Ui.Blend(theme.Window, theme.Accent, theme.Dark ? 0.32f : 0.16f)
            : error ? (theme.Dark ? Color.FromArgb(70, 38, 38) : Color.FromArgb(253, 236, 234))
            : theme.Card;

        Color Ink => quiet ? theme.SubText : theme.Text;

        int Pad => (int)(12 * DeviceDpi / 96f);

        public Size Measure(int maxWidth)
        {
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;
            var text = box.Text.Length == 0 ? " " : box.Text;
            var natural = TextRenderer.MeasureText(text, box.Font, new Size(maxWidth - Pad * 2, int.MaxValue), flags);
            int w = Math.Min(maxWidth, natural.Width + Pad * 2 + 6);
            var wrapped = TextRenderer.MeasureText(text, box.Font, new Size(w - Pad * 2 - 6, int.MaxValue), flags);
            return new Size(w, wrapped.Height + Pad * 2);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            box.SetBounds(Pad, Pad, Math.Max(10, Width - Pad * 2), Math.Max(10, Height - Pad * 2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = DeviceDpi / 96f;
            using var path = Ui.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 14 * s);
            using (var fill = new SolidBrush(Fill)) g.FillPath(fill, path);
            if (!Mine)
                using (var edge = new Pen(theme.CardBorder, 1f)) g.DrawPath(edge, path);
        }
    }

    /// <summary>The screenshot, fitted and centred, with a "Your screenshot" tag.</summary>
    sealed class Preview : Control
    {
        readonly SettingsTheme theme;
        readonly Bitmap image;

        public Preview(SettingsTheme theme, Bitmap image)
        {
            this.theme = theme;
            this.image = image;
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = theme.Window;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float s = DeviceDpi / 96f, pad = 16 * s;
            var area = new RectangleF(pad, pad, Width - pad * 2, Height - pad * 1.5f);
            float scale = Math.Min(area.Width / image.Width, area.Height / image.Height);
            float w = image.Width * scale, h = image.Height * scale;
            var r = new RectangleF(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
            using var path = Ui.RoundRect(r, 10 * s);
            var state = g.Save();
            g.SetClip(path);
            g.DrawImage(image, r);
            g.Restore(state);
            using (var edge = new Pen(theme.CardBorder, 1f)) g.DrawPath(edge, path);
        }
    }

    /// <summary>The question field and Ask button on one row, the model and cost underneath.</summary>
    sealed class InputBar : Panel
    {
        readonly Control field, button, note;

        public InputBar(SettingsTheme theme, Control field, Control button, Control note)
        {
            this.field = field;
            this.button = button;
            this.note = note;
            BackColor = theme.Window;
            foreach (var c in new[] { field, button, note })
            {
                c.BackColor = theme.Window;
                Controls.Add(c);
            }
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            float s = DeviceDpi / 96f;
            int pad = (int)(16 * s), gap = (int)(8 * s), rowH = (int)(40 * s);
            button.SetBounds(Width - pad - button.Width, (int)(8 * s) + (rowH - button.Height) / 2, button.Width, button.Height);
            field.SetBounds(pad, (int)(8 * s), Math.Max(60, button.Left - gap - pad), rowH);
            note.SetBounds(pad, (int)(8 * s) + rowH + (int)(6 * s), Width - pad * 2, (int)(24 * s));
        }
    }
}
