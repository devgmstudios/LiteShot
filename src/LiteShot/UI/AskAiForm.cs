using System.Drawing.Drawing2D;

namespace LiteShot;

/// <summary>
/// Ask AI: a chat about one screenshot. The picture sits at the top, sized to its shape, and opens big when clicked.
/// The conversation is below it, and questions go to the model chosen in Settings › Cloud &amp; AI through SyncNode →
/// OpenRouter. Enter sends; while it's thinking Ask becomes Stop. Every message can be copied, the latest question
/// edited (↑ in an empty box does it too), and the latest answer asked for again.
/// </summary>
internal sealed class AskAiForm : Form
{
    readonly SettingsTheme theme = new();
    readonly Bitmap screenshot;
    readonly AskAi chat;
    readonly Func<string?> key;
    readonly Preview preview;
    readonly Panel transcript;
    readonly FlowLayoutPanel chips;
    readonly FieldBox input;
    readonly PillButton send;
    readonly Label status;
    readonly System.Windows.Forms.Timer dots = new() { Interval = 400 };
    readonly List<Exchange> exchanges = new();
    ImageViewerForm? viewer;
    Bubble? thinking;
    CancellationTokenSource? cts;

    /// <summary>One question and its answer (or the error, or "Thinking…" while waiting).</summary>
    sealed class Exchange
    {
        public required string Question;
        public required Bubble Q;
        public Bubble? A;
        /// <summary>Whether the model answered, so the turn is part of the conversation it remembers.</summary>
        public bool Answered;
    }

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
        ClientSize = new Size(760, 780);

        preview = new Preview(theme, screenshot) { Dock = DockStyle.Top, Height = 240 };
        preview.Click += (_, _) => ShowZoom();
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
            // Like most chats: ↑ in an empty box takes the last question back to edit it.
            else if (e.KeyCode == Keys.Up && input.Box.TextLength == 0 && !Busy && exchanges.Count > 0) { e.SuppressKeyPress = true; EditLast(); }
        };
        send = new PillButton(theme, "Ask", 84, primary: true);
        send.Click += (_, _) => { if (Busy) Stop(); else Ask(input.Box.Text); };
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
        FitPreview();

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

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (preview != null) FitPreview();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { if (Busy) Stop(); else Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        cts?.Cancel();
        dots.Dispose();
        viewer?.Close();
        screenshot.Dispose();
        base.OnFormClosed(e);
    }

    public bool Busy => cts != null;

    /// <summary>The conversation as shown, for the harness: who said what.</summary>
    public IEnumerable<(bool Mine, string Text)> Messages =>
        exchanges.SelectMany(x => x.A == null ? new[] { x.Q } : new[] { x.Q, x.A }).Select(b => (b.Mine, b.Text));

    /// <summary>What's in the question box.</summary>
    public string Draft => input.Box.Text;

    /// <summary>The links under each message, in order, for the harness.</summary>
    public IEnumerable<string[]> ActionLabels =>
        exchanges.SelectMany(x => x.A == null ? new[] { x.Q } : new[] { x.Q, x.A }).Select(b => b.ActionLabels);

    /// <summary>The preview's height for this window: as tall as the picture wants at this width, up to about 40% of the window.</summary>
    void FitPreview()
    {
        float s = DeviceDpi / 96f;
        float width = ClientSize.Width - 32 * s;
        if (width < 50) return;
        float scale = Math.Min(width / screenshot.Width, 2f);
        int want = (int)(screenshot.Height * scale + 26 * s);
        // Before the first question there's room to show more of it; once chatting, the conversation gets the space.
        int max = (int)(ClientSize.Height * (exchanges.Count == 0 ? 0.6f : 0.42f)), min = (int)(110 * s);
        int h = Math.Clamp(want, min, Math.Max(min, max));
        if (preview.Height != h) preview.Height = h;
    }

    /// <summary>Opens the screenshot big, to zoom into; one viewer at a time.</summary>
    internal ImageViewerForm ShowZoom()
    {
        if (viewer is { IsDisposed: false }) { viewer.Activate(); return viewer; }
        viewer = new ImageViewerForm(screenshot, "LiteShot — Ask AI screenshot");
        viewer.FormClosed += (_, _) => viewer = null;
        viewer.Show(this);
        return viewer;
    }

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
        var turn = new Exchange { Question = question, Q = new Bubble(theme, question, mine: true) };
        exchanges.Add(turn);
        transcript.Controls.Add(turn.Q);
        thinking = turn.A = new Bubble(theme, "Thinking", mine: false, quiet: true);
        transcript.Controls.Add(thinking);
        dots.Start();
        SetBusy(true);
        RefreshActions();
        ScrollToEnd();
        string model = AskAi.ModelId;
        try
        {
            string answer = await chat.AskAsync(question, apiKey, model, cts!.Token);
            SetAnswer(turn, new Bubble(theme, AskAi.Plain(answer), mine: false));
            turn.Answered = true;
            status.Text = $"{AskAi.LabelFor(model)} through SyncNode · each message costs $0.02 plus the model's price";
        }
        catch (OperationCanceledException)
        {
            if (IsDisposed) return;
            // Stopped: the question comes off the chat and goes back in the box.
            RemoveFrom(exchanges.IndexOf(turn));
            input.Box.Text = question;
            input.Box.SelectionStart = question.Length;
            status.Text = "Stopped. Your question is back in the box to change or send again.";
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            SetAnswer(turn, new Bubble(theme, AiService.Friendly(ex), mine: false, error: true));
        }
        finally
        {
            if (!IsDisposed)
            {
                thinking = null;
                dots.Stop();
                SetBusy(false);
                RefreshActions();
                ScrollToEnd();
                input.Box.Focus();
            }
        }
    }

    /// <summary>Stops waiting for an answer.</summary>
    public void Stop() => cts?.Cancel();

    /// <summary>Takes the last question back into the box to change it; its answer is forgotten.</summary>
    public void EditLast()
    {
        if (Busy || exchanges.Count == 0) return;
        string question = exchanges[^1].Question;
        RemoveFrom(exchanges.Count - 1);
        input.Box.Text = question;
        input.Box.Focus();
        input.Box.SelectionStart = question.Length;
        status.Text = "Change your question and press Enter to send it again.";
    }

    /// <summary>Asks the last question again, for a fresh answer.</summary>
    public void RetryLast()
    {
        if (Busy || exchanges.Count == 0) return;
        string question = exchanges[^1].Question;
        RemoveFrom(exchanges.Count - 1);
        Ask(question);
    }

    /// <summary>Takes exchanges <paramref name="index"/> onward off the chat and out of what the model remembers.</summary>
    void RemoveFrom(int index)
    {
        if (index < 0) return;
        chat.Rewind(exchanges.Take(index).Count(x => x.Answered));
        foreach (var x in exchanges.Skip(index).ToList())
        {
            foreach (var b in new[] { x.Q, x.A })
                if (b != null) { transcript.Controls.Remove(b); b.Dispose(); }
            exchanges.Remove(x);
        }
        if (thinking is { IsDisposed: true }) thinking = null;
        chips.Visible = exchanges.Count == 0;
        RefreshActions();
    }

    void SetAnswer(Exchange turn, Bubble answer)
    {
        if (turn.A != null) { transcript.Controls.Remove(turn.A); turn.A.Dispose(); }
        turn.A = answer;
        transcript.Controls.Add(answer);
    }

    /// <summary>Copy under everything; Edit under the last question and Retry under the last answer, once it's back.</summary>
    void RefreshActions()
    {
        for (int i = 0; i < exchanges.Count; i++)
        {
            var x = exchanges[i];
            bool last = i == exchanges.Count - 1 && !Busy;
            var q = new List<(string, Action<LinkLabel>)> { ("Copy", l => Copy(x.Q, l)) };
            if (last) q.Add(("Edit", _ => EditLast()));
            x.Q.SetActions(q.ToArray());
            if (x.A == null || x.A == thinking) { x.A?.SetActions(); continue; }
            var a = new List<(string, Action<LinkLabel>)>();
            if (x.Answered) a.Add(("Copy", l => Copy(x.A, l)));
            if (last) a.Add((x.Answered ? "Retry" : "Try again", _ => RetryLast()));
            x.A.SetActions(a.ToArray());
        }
        FitPreview();
        LayoutTranscript();
    }

    void Copy(Bubble b, LinkLabel link)
    {
        try { Clipboard.SetText(b.Text.Replace("\n", "\r\n")); }
        catch { status.Text = "Couldn't reach the clipboard; try again."; return; }
        b.Flash(link, "Copied ✓");
    }

    void SetBusy(bool busy)
    {
        if (busy) cts = new CancellationTokenSource();
        else { cts?.Dispose(); cts = null; }
        send.Text = send.AccessibleName = busy ? "Stop" : "Ask";
        input.Box.ReadOnly = busy;
        send.Invalidate();
    }

    void ScrollToEnd()
    {
        LayoutTranscript();
        var last = exchanges.LastOrDefault();
        if ((last?.A ?? last?.Q) is { } b) transcript.ScrollControlIntoView(b);
    }

    /// <summary>Bubbles stack top to bottom: answers on the left, questions on the right, each at most 82% wide.</summary>
    void LayoutTranscript()
    {
        float s = DeviceDpi / 96f;
        // Leave room for the scroll bar even before it shows, so questions on the right never push out a sideways one.
        int width = transcript.ClientSize.Width - transcript.Padding.Horizontal
            - (transcript.VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth);
        int y = transcript.Padding.Top + transcript.AutoScrollPosition.Y, gap = (int)(6 * s);
        transcript.SuspendLayout();
        foreach (var x in exchanges)
            foreach (var b in new[] { x.Q, x.A })
            {
                if (b == null) continue;
                var size = b.Measure((int)(width * 0.82f));
                int left = b.Mine ? transcript.Padding.Left + width - size.Width : transcript.Padding.Left;
                b.SetBounds(left, y, size.Width, size.Height);
                y += size.Height + gap;
            }
        transcript.ResumeLayout();
    }

    /// <summary>One message: a rounded bubble around selectable text, with small links (Copy, Edit, Retry) under it.</summary>
    sealed class Bubble : Panel
    {
        readonly SettingsTheme theme;
        readonly TextBox box;
        readonly bool error, quiet;
        readonly List<LinkLabel> links = new();
        readonly Font linkFont;
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
            linkFont = new Font("Segoe UI", 9.5F);
        }

        public override string Text => box?.Text.Replace("\r\n", "\n") ?? "";

        public void SetText(string text) => box.Text = text;

        public string[] ActionLabels => links.Select(l => (string)l.Tag!).ToArray();

        Color Fill => theme.HighContrast ? SystemColors.Window
            : Mine ? Ui.Blend(theme.Window, theme.Accent, theme.Dark ? 0.32f : 0.16f)
            : error ? (theme.Dark ? Color.FromArgb(70, 38, 38) : Color.FromArgb(253, 236, 234))
            : theme.Card;

        Color Ink => quiet ? theme.SubText : theme.Text;

        Color LinkInk => theme.HighContrast ? SystemColors.HotTrack : Ui.Blend(theme.Accent, theme.Text, theme.Dark ? 0.35f : 0.12f);

        float S => DeviceDpi / 96f;
        int Pad => (int)(12 * S);
        int StripH => links.Count == 0 ? (int)(4 * S) : (int)(26 * S);
        int LinkGap => (int)(14 * S);

        public void SetActions(params (string Label, Action<LinkLabel> Run)[] actions)
        {
            if (actions.Select(a => a.Label).SequenceEqual(ActionLabels))
            {
                // Same links: just point them at the new actions.
                for (int i = 0; i < actions.Length; i++) links[i].Links[0].LinkData = actions[i].Run;
                return;
            }
            foreach (var l in links) { Controls.Remove(l); l.Dispose(); }
            links.Clear();
            foreach (var (label, run) in actions)
            {
                var link = new LinkLabel
                {
                    Text = label, Tag = label, AutoSize = true, UseMnemonic = false, Font = linkFont, TabStop = true,
                    LinkBehavior = LinkBehavior.HoverUnderline, LinkColor = LinkInk, ActiveLinkColor = theme.Text, VisitedLinkColor = LinkInk,
                    BackColor = theme.Window, Padding = Padding.Empty, Margin = Padding.Empty,
                    AccessibleName = $"{label} {(Mine ? "your question" : "the answer")}",
                };
                link.Links[0].LinkData = run;
                link.LinkClicked += (_, e) => ((Action<LinkLabel>)e.Link!.LinkData!)(link);
                links.Add(link);
                Controls.Add(link);
            }
            PerformLayout();
            Invalidate();
        }

        /// <summary>Shows "Copied ✓" on a link for a moment.</summary>
        public void Flash(LinkLabel link, string text)
        {
            string label = (string)link.Tag!;
            link.Text = text;
            var timer = new System.Windows.Forms.Timer { Interval = 1400 };
            timer.Tick += (_, _) =>
            {
                timer.Dispose();
                if (!link.IsDisposed) link.Text = label;
            };
            timer.Start();
        }

        public Size Measure(int maxWidth)
        {
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;
            var text = box.Text.Length == 0 ? " " : box.Text;
            var natural = TextRenderer.MeasureText(text, box.Font, new Size(maxWidth - Pad * 2, int.MaxValue), flags);
            int linksW = links.Sum(l => l.PreferredSize.Width) + LinkGap * Math.Max(0, links.Count - 1) + (int)(10 * S);
            int w = Math.Min(maxWidth, Math.Max(natural.Width + Pad * 2 + 6, linksW));
            var wrapped = TextRenderer.MeasureText(text, box.Font, new Size(w - Pad * 2 - 6, int.MaxValue), flags);
            return new Size(w, wrapped.Height + Pad * 2 + StripH);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int body = Height - StripH;
            box.SetBounds(Pad, Pad, Math.Max(10, Width - Pad * 2), Math.Max(10, body - Pad * 2));
            int x = Mine ? Width - (int)(6 * S) : (int)(6 * S);
            foreach (var l in Mine ? Enumerable.Reverse(links) : links)
            {
                var size = l.PreferredSize;
                int top = body + (StripH - size.Height) / 2;
                if (Mine) { x -= size.Width; l.SetBounds(x, top, size.Width, size.Height); x -= LinkGap; }
                else { l.SetBounds(x, top, size.Width, size.Height); x += size.Width + LinkGap; }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Ui.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - StripH - 1f), 14 * S);
            using (var fill = new SolidBrush(Fill)) g.FillPath(fill, path);
            if (!Mine)
                using (var edge = new Pen(theme.CardBorder, 1f)) g.DrawPath(edge, path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) linkFont.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>The screenshot, as big as fits and centred, with a "Click to zoom" tag; clicking opens it big.</summary>
    sealed class Preview : Control
    {
        readonly SettingsTheme theme;
        readonly Bitmap image;
        bool hover;

        public Preview(SettingsTheme theme, Bitmap image)
        {
            this.theme = theme;
            this.image = image;
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = theme.Window;
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = "Your screenshot. Click to zoom in.";
        }

        RectangleF ImageRect
        {
            get
            {
                float s = DeviceDpi / 96f, pad = 16 * s;
                var area = new RectangleF(pad, pad, Width - pad * 2, Height - pad - 10 * s);
                // Small selections get up to twice their size, so they're readable without zooming.
                float scale = Math.Min(Math.Min(area.Width / image.Width, area.Height / image.Height), 2f);
                float w = image.Width * scale, h = image.Height * scale;
                return new RectangleF(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Enter or Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            float s = DeviceDpi / 96f;
            var r = ImageRect;
            using var path = Ui.RoundRect(r, 10 * s);
            var state = g.Save();
            g.SetClip(path);
            g.DrawImage(image, r);
            g.Restore(state);
            using (var edge = new Pen(hover || Focused ? theme.Accent : theme.CardBorder, hover || Focused ? 2f : 1f)) g.DrawPath(edge, path);

            // "Click to zoom", in the picture's corner: a dark pill with a magnifier.
            const string tag = "Click to zoom";
            using var font = new Font("Segoe UI Semibold", 9F);
            var text = TextRenderer.MeasureText(tag, font, Size.Empty, TextFormatFlags.NoPadding);
            float icon = 12 * s, ph = Math.Max(text.Height, icon) + 8 * s, pw = text.Width + icon + 22 * s;
            var pill = new RectangleF(r.Right - pw - 8 * s, r.Bottom - ph - 8 * s, pw, ph);
            // A narrow picture has no room inside; the tag sits beside it instead.
            if (pill.Left < r.Left + 4 * s || pill.Top < r.Top + 4 * s) pill = new RectangleF(r.Right + 8 * s, r.Bottom - ph, pw, ph);
            if (pill.Right > Width) return;
            using (var p = Ui.RoundRect(pill, ph / 2))
            using (var fill = new SolidBrush(Color.FromArgb(hover ? 225 : 175, 20, 20, 22)))
                g.FillPath(fill, p);
            float cx = pill.Left + 8 * s + icon * 0.4f, cy = pill.Top + ph / 2 - icon * 0.08f, rad = icon * 0.32f;
            using (var pen = new Pen(Color.White, 1.6f * s))
            {
                g.DrawEllipse(pen, cx - rad, cy - rad, rad * 2, rad * 2);
                g.DrawLine(pen, cx + rad * 0.7f, cy + rad * 0.7f, cx + rad * 1.75f, cy + rad * 1.75f);
            }
            TextRenderer.DrawText(g, tag, font, new Point((int)(pill.Left + icon + 14 * s), (int)(pill.Top + (ph - text.Height) / 2)), Color.White,
                TextFormatFlags.NoPadding);
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
