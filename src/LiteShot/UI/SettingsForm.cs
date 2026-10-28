namespace LiteShot;

/// <summary>
/// Settings in the Windows 11 idiom: sidebar navigation, grouped cards, toggles and segmented controls.
/// Changes apply immediately, like the system Settings app.
/// </summary>
internal sealed class SettingsForm : Form
{
    readonly TrayApp host;
    readonly SettingsTheme theme = new();
    readonly Panel content;
    readonly List<(NavButton Nav, Panel Page)> pages = new();
    static AppSettings Settings => AppSettings.Current;

    public const int ShortcutsPage = 1;
    public const int CloudPage = 3;

    public SettingsForm(TrayApp host, int startPage = 0)
    {
        this.host = host;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10F);
        Text = "LiteShot settings";
        Icon = TrayApp.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(880, 600);
        BackColor = theme.Window;
        ForeColor = theme.Text;
        KeyPreview = true;

        content = new Panel { Dock = DockStyle.Fill, BackColor = theme.Window };
        var nav = new Panel { Dock = DockStyle.Left, Width = 252, Padding = new Padding(12, 8, 8, 12), BackColor = theme.Window };
        Controls.Add(content);
        Controls.Add(nav);

        AddPage(nav, "Capture", Icons.Capture, BuildCapturePage());
        AddPage(nav, "Shortcuts", Icons.Keyboard, BuildShortcutsPage());
        AddPage(nav, "Saving", Icons.Save, BuildSavingPage());
        AddPage(nav, "Cloud & AI", Icons.Sparkles, BuildCloudPage());
        AddPage(nav, "General", Icons.Gear, BuildGeneralPage());
        nav.Controls.Add(new HeaderPanel(theme) { Dock = DockStyle.Top });
        ShowPage(Math.Clamp(startPage, 0, pages.Count - 1));
        ResumeLayout(false);
        PerformLayout();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyWindowChrome(Handle, theme.Dark);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && KeyboardHook.Interceptor == null) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        KeyboardHook.Interceptor = null;
        base.OnFormClosed(e);
    }

    void AddPage(Panel nav, string title, Icons.Glyph glyph, Panel page)
    {
        int index = pages.Count;
        var button = new NavButton(theme, title, glyph) { Dock = DockStyle.Top };
        button.Click += (_, _) => ShowPage(index);
        nav.Controls.Add(button);
        button.BringToFront();
        page.Visible = false;
        content.Controls.Add(page);
        pages.Add((button, page));
    }

    public void ShowPageAt(int index) => ShowPage(Math.Clamp(index, 0, pages.Count - 1));

    void ShowPage(int index)
    {
        for (int i = 0; i < pages.Count; i++)
        {
            pages[i].Nav.Selected = i == index;
            pages[i].Page.Visible = i == index;
        }
    }

    void Changed()
    {
        Settings.Save();
        host.ApplySettings();
    }

    // ───────────────────────────── page building ─────────────────────────────

    Panel NewPage(string title, out Action<Control> add)
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(28, 18, 32, 28), BackColor = theme.Window };
        void Add(Control c)
        {
            c.Dock = DockStyle.Top;
            page.Controls.Add(c);
            c.BringToFront();
        }
        add = Add;
        Add(new Label
        {
            Text = title,
            Font = new Font("Segoe UI Semibold", 20F),
            AutoSize = false,
            Height = 54,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = theme.Text,
            UseMnemonic = false,
        });
        return page;
    }

    Label Section(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI Semibold", 10.5F),
        AutoSize = false,
        Height = 40,
        Padding = new Padding(2, 0, 0, 6),
        TextAlign = ContentAlignment.BottomLeft,
        ForeColor = theme.Text,
        UseMnemonic = false,
    };

    Label Note(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", 9F),
        AutoSize = false,
        Height = 52,
        Padding = new Padding(2, 6, 0, 0),
        ForeColor = theme.SubText,
        UseMnemonic = false,
    };

    ToggleSwitch Toggle(bool value, Action<bool> apply)
    {
        var t = new ToggleSwitch(theme) { Checked = value };
        t.CheckedChanged += (_, _) => { apply(t.Checked); Changed(); };
        return t;
    }

    Panel BuildCapturePage()
    {
        var page = NewPage("Capture", out var add);
        add(Section("While selecting"));
        add(new Card(theme)
            .Add(new SettingRow(theme, "Magnifier", "A pixel loupe with coordinates and color. Press C to copy the color.",
                Toggle(Settings.ShowMagnifier, v => Settings.ShowMagnifier = v)))
            .Add(new SettingRow(theme, "Window detection", "Hover a window and click once to capture exactly that window.",
                Toggle(Settings.DetectWindows, v => Settings.DetectWindows = v))));
        add(Section("Screenshot"));
        add(new Card(theme)
            .Add(new SettingRow(theme, "Include the mouse pointer", "Draws the pointer into captured screenshots.",
                Toggle(Settings.CaptureCursor, v => Settings.CaptureCursor = v))));
        add(Section("Privacy"));
        var faceprivacy = new PillButton(theme, "FacePrivacy.ai", 128);
        faceprivacy.Click += (_, _) => ImageOutput.OpenUrl("https://faceprivacy.ai/blur/");
        add(new Card(theme)
            .Add(new SettingRow(theme, "Blur names automatically",
                "Blurs text that looks like a person's name as you capture. Ctrl+Z brings it back.",
                Toggle(Settings.AutoBlurNames, v => Settings.AutoBlurNames = v)))
            .Add(new SettingRow(theme, "Blur faces · FacePrivacy.ai", "Press F to blur every face, or paint over one with the blur brush (U). Runs on your PC.", faceprivacy)));
        return page;
    }

    Panel BuildShortcutsPage()
    {
        var page = NewPage("Shortcuts", out var add);
        add(Section("Global shortcuts"));
        HotkeyBox Box(Keys value, Action<Keys> apply)
        {
            var box = new HotkeyBox(theme) { Hotkey = value };
            box.HotkeyChanged += (_, _) => { apply(box.Hotkey); Changed(); };
            return box;
        }
        add(new Card(theme)
            .Add(new SettingRow(theme, "Capture an area", "Select, annotate, then copy, save or share.",
                Box(Settings.HotkeyCapture, k => Settings.HotkeyCapture = k)))
            .Add(new SettingRow(theme, "Capture the full screen", "Saves to the folder and copies it.",
                Box(Settings.HotkeyFullScreen, k => Settings.HotkeyFullScreen = k)))
            .Add(new SettingRow(theme, "Capture the last area", "Reopens the editor on the area you used last.",
                Box(Settings.HotkeyLastRegion, k => Settings.HotkeyLastRegion = k))));
        add(Note("Click a shortcut and press new keys. Backspace clears it. LiteShot takes PrtScn over from the Snipping Tool while it runs."));

        add(Section("In the editor"));
        var card = new Card(theme);
        foreach (var (name, keys) in new (string, string[])[]
        {
            ("Copy and close", new[] { "Ctrl", "C", "/", "Enter" }),
            ("Save", new[] { "Ctrl", "S" }),
            ("Upload and copy the link", new[] { "Ctrl", "D" }),
            ("Search similar images", new[] { "Ctrl", "K" }),
            ("Pin to screen", new[] { "Ctrl", "T" }),
            ("Copy text (OCR)", new[] { "Ctrl", "Shift", "C" }),
            ("Text: bold, italic", new[] { "Ctrl", "B", "/", "Ctrl", "I" }),
            ("Text: align left, center, right", new[] { "Ctrl", "L", "E", "R" }),
            ("Text: smaller, larger", new[] { "Ctrl", "[", "/", "Ctrl", "]" }),
            ("Undo, redo", new[] { "Ctrl", "Z", "/", "Ctrl", "Y" }),
            ("Tools: pen, line, arrow, rectangle, ellipse, marker, text, number, pixelate", new[] { "P", "L", "A", "R", "E", "M", "T", "N", "B" }),
            ("Stroke or font size", new[] { "Mouse wheel" }),
            ("Move the selection, resize with Shift", new[] { "←", "↑", "→", "↓" }),
            ("Select a whole monitor", new[] { "Ctrl", "A" }),
            ("Close", new[] { "Esc" }),
        })
        {
            var label = new KeycapLabel(theme, keys) { Width = keys.Length > 6 ? 330 : 240 };
            card.Add(new SettingRow(theme, name, (string?)null, label));
        }
        add(card);
        return page;
    }

    Panel BuildSavingPage()
    {
        var page = NewPage("Saving", out var add);

        var folder = new FieldBox(theme);
        folder.Box.Text = Settings.SaveFolder;
        folder.Box.TextChanged += (_, _) =>
        {
            if (folder.Box.Text.Trim().Length == 0) return;
            Settings.SaveFolder = folder.Box.Text.Trim();
            Changed();
        };
        var browse = new PillButton(theme, "Browse…", 100);
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Choose where screenshots are saved",
                UseDescriptionForTitle = true,
                SelectedPath = Settings.SaveFolder,
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) folder.Box.Text = dialog.SelectedPath;
        };
        var open = new PillButton(theme, "Open", 80);
        open.Click += (_, _) => ImageOutput.OpenFolder(Settings.SaveFolder);
        var folderRow = new FlowRow(theme, folder, browse, open);

        SettingRow? nameRow = null;
        var pattern = new FieldBox(theme);
        pattern.Box.Text = Settings.FileNamePattern;
        pattern.Box.TextChanged += (_, _) =>
        {
            Settings.FileNamePattern = pattern.Box.Text;
            Changed();
            nameRow?.RefreshText();
        };
        nameRow = new SettingRow(theme, "File name",
            () => $"Date parts go in braces, like {{yyyy-MM-dd}}.  Preview: {ImageOutput.BuildFileName(Settings.FileNamePattern, DateTime.Now)}{ImageOutput.Extension}",
            pattern, wide: true);

        add(Section("Location"));
        add(new Card(theme)
            .Add(new SettingRow(theme, "Screenshots folder", "Used for quick saves, full-screen captures and automatic copies.", folderRow, wide: true))
            .Add(nameRow));

        string[] formats = { "png", "jpg", "bmp" };
        var format = new Segmented(theme, 72, "PNG", "JPG", "BMP") { SelectedIndex = Math.Max(0, Array.IndexOf(formats, Settings.ImageFormat)) };
        int[] qualities = { 75, 85, 92, 100 };
        var quality = new Segmented(theme, 64, "75", "85", "92", "100")
        {
            SelectedIndex = Math.Max(0, Array.IndexOf(qualities, Settings.JpegQuality)),
            Enabled = Settings.ImageFormat == "jpg",
        };
        format.SelectedChanged += (_, _) =>
        {
            Settings.ImageFormat = formats[format.SelectedIndex];
            quality.Enabled = Settings.ImageFormat == "jpg";
            Changed();
            nameRow.RefreshText();
        };
        quality.SelectedChanged += (_, _) => { Settings.JpegQuality = qualities[quality.SelectedIndex]; Changed(); };

        add(Section("Format"));
        add(new Card(theme)
            .Add(new SettingRow(theme, "Image format", "PNG is lossless and best for screenshots.", format))
            .Add(new SettingRow(theme, "JPEG quality", "Only used when the format is JPG.", quality)));

        add(Section("Behavior"));
        add(new Card(theme)
            .Add(new SettingRow(theme, "Ask where to save", "When off, Ctrl+S saves straight to the screenshots folder.",
                Toggle(Settings.AskWhereToSave, v => Settings.AskWhereToSave = v)))
            .Add(new SettingRow(theme, "Keep a copy of everything", "Also saves screenshots you copy or upload to the folder.",
                Toggle(Settings.AutoSaveCopies, v => Settings.AutoSaveCopies = v))));
        return page;
    }

    Panel BuildCloudPage()
    {
        var page = NewPage("Cloud & AI", out var add);

        string[] lifetimes = Uploader.Lifetimes.Select(l => l.Key).ToArray();
        var lifetime = new Segmented(theme, 80, "1 h", "12 h", "24 h")
        {
            SelectedIndex = Math.Max(0, Array.IndexOf(lifetimes, Uploader.Normalize(Settings.UploadLifetime))),
        };
        lifetime.SelectedChanged += (_, _) => { Settings.UploadLifetime = lifetimes[lifetime.SelectedIndex]; Changed(); };
        add(Section("Uploads"));
        add(new Card(theme)
            .Add(new SettingRow(theme, "Delete uploaded links after",
                "Uploads go to fortefile.com, encrypted on your PC first. Delete one sooner from Recent uploads in the tray menu.", lifetime, wide: true)));

        // SyncNode key: stored encrypted for this Windows user; Check shows the balance.
        string keyHelp = "From syncnode.ai/api_keys. Save your Replicate token there too.";
        string keyStatus = AiService.HasKey ? "Saved and encrypted for your Windows account. Use Check to see your balance." : keyHelp;
        SettingRow? keyRow = null;
        var key = new FieldBox(theme);
        key.Box.UseSystemPasswordChar = true;
        key.Box.Text = AiService.ApiKey ?? "";
        Native.SetCueBanner(key.Box, "SyncNode API key");
        key.Box.TextChanged += (_, _) =>
        {
            AiService.ApiKey = key.Box.Text;
            Settings.Save();
            keyStatus = key.Box.Text.Trim().Length == 0 ? keyHelp : "Saved and encrypted for your Windows account. Use Check to see your balance.";
            keyRow?.RefreshText();
        };
        var check = new PillButton(theme, "Check", 84, primary: true);
        check.Click += async (_, _) =>
        {
            string? apiKey = AiService.ApiKey;
            if (apiKey == null) { keyStatus = keyHelp; keyRow?.RefreshText(); return; }
            keyStatus = "Checking…";
            keyRow?.RefreshText();
            try
            {
                decimal? balance = await SyncNodeClient.BalanceAsync(apiKey);
                keyStatus = balance is decimal b ? $"Connected · balance ${b:0.00}" : "Connected.";
            }
            catch (Exception ex) { keyStatus = AiService.Friendly(ex); }
            if (!IsDisposed) keyRow?.RefreshText();
        };
        var getKey = new PillButton(theme, "Get a key", 104);
        getKey.Click += (_, _) => ImageOutput.OpenUrl("https://syncnode.ai/api_keys");
        keyRow = new SettingRow(theme, "SyncNode API key", () => keyStatus, new FlowRow(theme, key, check, getKey), wide: true);
        add(Section("SyncNode"));
        add(new Card(theme).Add(keyRow));

        Control CustomRow(Func<string> getModel, Action<string> setModel, Func<string> getField, Action<string> setField)
        {
            var model = new FieldBox(theme);
            model.Box.Text = getModel();
            Native.SetCueBanner(model.Box, "owner/model-name");
            model.Box.TextChanged += (_, _) => { setModel(model.Box.Text); Changed(); };
            var field = new FieldBox(theme) { Width = 190 };
            field.Box.Text = getField();
            Native.SetCueBanner(field.Box, "image input name");
            field.Box.TextChanged += (_, _) => { setField(field.Box.Text); Changed(); };
            return new FlowRow(theme, model, field);
        }

        string[] editKeys = AiService.EditModels.Select(m => m.Key).Append("custom").ToArray();
        var editModel = new Segmented(theme, 150, AiService.EditModels.Select(m => m.Label).Append("Custom").ToArray())
        {
            SelectedIndex = Math.Max(0, Array.IndexOf(editKeys, Settings.AiEditModel)),
        };
        editModel.SelectedChanged += (_, _) => { Settings.AiEditModel = editKeys[editModel.SelectedIndex]; Changed(); };
        add(Section("Edit with AI"));
        add(new Card(theme)
            .Add(new SettingRow(theme, "Model", "Kontext is great at precise edits; Nano Banana at bigger changes.", editModel, wide: true))
            .Add(new SettingRow(theme, "Custom Replicate model", "Used when Custom is chosen: the model, and the name of its image input.",
                CustomRow(() => Settings.AiEditCustomModel, v => Settings.AiEditCustomModel = v, () => Settings.AiEditCustomField, v => Settings.AiEditCustomField = v), wide: true)));

        string[] videoKeys = AiService.VideoModels.Select(m => m.Key).Append("custom").ToArray();
        var videoModel = new Segmented(theme, 130, AiService.VideoModels.Select(m => m.Label).Append("Custom").ToArray())
        {
            SelectedIndex = Math.Max(0, Array.IndexOf(videoKeys, Settings.AiVideoModel)),
        };
        videoModel.SelectedChanged += (_, _) => { Settings.AiVideoModel = videoKeys[videoModel.SelectedIndex]; Changed(); };
        var length = new Segmented(theme, 72, "5 s", "10 s") { SelectedIndex = Settings.AiVideoDuration >= 10 ? 1 : 0 };
        length.SelectedChanged += (_, _) => { Settings.AiVideoDuration = length.SelectedIndex == 1 ? 10 : 5; Changed(); };
        add(Section("Make a video"));
        add(new Card(theme)
            .Add(new SettingRow(theme, "Model", "Videos are saved to your screenshots folder and copied, ready to paste.", videoModel, wide: true))
            .Add(new SettingRow(theme, "Length", "Seedance and Kling support 5 or 10 seconds.", length))
            .Add(new SettingRow(theme, "Custom Replicate model", "Used when Custom is chosen: the model, and the name of its image input.",
                CustomRow(() => Settings.AiVideoCustomModel, v => Settings.AiVideoCustomModel = v, () => Settings.AiVideoCustomField, v => Settings.AiVideoCustomField = v), wide: true)));
        add(Note("Each run costs $0.02 on SyncNode plus the model's price on your Replicate account. The screenshot is sent inside the request; it's never posted publicly."));
        return page;
    }

    Panel BuildGeneralPage()
    {
        var page = NewPage("General", out var add);
        add(Section("General"));
        add(new Card(theme)
            .Add(new SettingRow(theme, "Start with Windows", "Keeps LiteShot in the tray so PrtScn always works.",
                Toggle(StartupRegistration.IsEnabled, StartupRegistration.Set)))
            .Add(new SettingRow(theme, "Notifications", "Shows a notification after saving, uploading or recognizing text.",
                Toggle(Settings.ShowNotifications, v => Settings.ShowNotifications = v))));

        add(Section("Updates"));
        string IdleStatus() => !Updater.Configured
            ? "Updates aren't set up in this build yet."
            : Settings.LastUpdateCheck is DateTime t
                ? $"You have version {Updater.CurrentVersion}. Last checked {t:g}."
                : $"You have version {Updater.CurrentVersion}.";
        string updateStatus = IdleStatus();
        SettingRow? updateRow = null;
        UpdateInfo? available = null;
        var checkNow = new PillButton(theme, "Check now", 116) { Enabled = Updater.Configured };
        void Status(string text)
        {
            updateStatus = text;
            if (!IsDisposed) updateRow?.RefreshText();
        }
        checkNow.Click += async (_, _) =>
        {
            if (!checkNow.Enabled) return;
            checkNow.Enabled = false;
            try
            {
                if (available == null)
                {
                    Status("Checking…");
                    available = await Task.Run(() => Updater.CheckAsync());
                    Settings.LastUpdateCheck = DateTime.Now;
                    Settings.Save();
                    if (available == null) { Status($"LiteShot {Updater.CurrentVersion} is the latest version."); return; }
                    string notes = string.IsNullOrWhiteSpace(available.Notes) ? "" : " " + available.Notes.Trim();
                    Status($"Version {available.Version} is available.{notes}");
                    checkNow.Text = "Install";
                    checkNow.Invalidate();
                    return;
                }
                var release = available;
                int shown = -1;
                var progress = new Progress<double>(p =>
                {
                    int percent = (int)(p * 100);
                    if (percent != shown) { shown = percent; Status($"Downloading version {release.Version}… {percent}%"); }
                });
                string file = await Task.Run(() => Updater.DownloadAsync(release, Updater.IsInstalled, progress));
                Status("Installing… LiteShot will restart in a moment.");
                host.InstallUpdate(file);
            }
            catch (Exception ex)
            {
                Status(ex is UpdateException ? ex.Message
                    : ex is HttpRequestException or TaskCanceledException ? "Couldn't reach the update server. Check your connection and try again."
                    : ex.Message);
            }
            finally
            {
                if (!IsDisposed) checkNow.Enabled = true;
            }
        };
        updateRow = new SettingRow(theme, "Check for updates", () => updateStatus, checkNow);
        var autoUpdate = Toggle(Settings.AutoUpdate, v => Settings.AutoUpdate = v);
        autoUpdate.Enabled = Updater.Configured;
        add(new Card(theme)
            .Add(new SettingRow(theme, "Update automatically",
                "Checks every few hours and installs signed updates while LiteShot is idle.",
                autoUpdate))
            .Add(updateRow));

        add(Section("About"));
        var openSettings = new PillButton(theme, "Open folder", 120);
        openSettings.Click += (_, _) => ImageOutput.OpenFolder(AppSettings.Directory);
        string version = typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        add(new Card(theme)
            .Add(new SettingRow(theme, $"LiteShot {version}", "© 2026 Devgm, LLC. The classic two-click screenshot tool, rebuilt with a glass interface.", (Control?)null))
            .Add(new SettingRow(theme, "Uploads", "Encrypted on fortefile.com, and deleted when your chosen time is up.", (Control?)null))
            .Add(new SettingRow(theme, "Settings file", AppSettings.Directory, openSettings)));
        return page;
    }

    /// <summary>Lays out a stretching field followed by fixed-width buttons.</summary>
    sealed class FlowRow : Panel
    {
        readonly Control field;
        readonly Control[] buttons;

        public FlowRow(SettingsTheme theme, Control field, params Control[] buttons)
        {
            this.field = field;
            this.buttons = buttons;
            BackColor = theme.Card;
            Height = 36;
            Controls.Add(field);
            Controls.AddRange(buttons);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int gap = (int)(8 * DeviceDpi / 96f), x = Width;
            foreach (var b in buttons.Reverse())
            {
                x -= b.Width;
                b.SetBounds(x, (Height - b.Height) / 2, b.Width, b.Height);
                x -= gap;
            }
            field.SetBounds(0, 0, Math.Max(40, x), Height);
        }
    }

    /// <summary>App identity at the top of the sidebar.</summary>
    sealed class HeaderPanel : Panel
    {
        readonly SettingsTheme theme;

        public HeaderPanel(SettingsTheme theme)
        {
            this.theme = theme;
            Height = 72;
            DoubleBuffered = true;
            BackColor = theme.Window;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = DeviceDpi / 96f;
            int icon = (int)(32 * s);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            using (var ico = new Icon(TrayApp.AppIcon, new Size(icon, icon)))
                g.DrawIcon(ico, new Rectangle((int)(14 * s), (Height - icon) / 2, icon, icon));
            using var title = new Font("Segoe UI Semibold", 12F);
            using var sub = new Font("Segoe UI", 8.5F);
            int x = (int)(56 * s);
            int th = TextRenderer.MeasureText(g, "LiteShot", title).Height, sh = TextRenderer.MeasureText(g, "Ag", sub).Height;
            int top = (Height - th - sh) / 2;
            TextRenderer.DrawText(g, "LiteShot", title, new Point(x, top), theme.Text, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "by Devgm, LLC", sub, new Point(x, top + th), theme.SubText, TextFormatFlags.NoPadding);
        }
    }
}
