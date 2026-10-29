using Microsoft.Win32;
using LiteShot.Overlay;

namespace LiteShot;

/// <summary>Lives in the notification area, owns the global hotkeys and runs captures, uploads, pins and OCR.</summary>
internal sealed class TrayApp : ApplicationContext
{
    public static TrayApp Instance { get; private set; } = null!;
    public static Icon AppIcon { get; } = LoadIcon();

    readonly Control invoker = new();
    readonly NotifyIcon tray;
    readonly ContextMenuStrip menu;
    readonly KeyboardHook hook;
    readonly EventWaitHandle captureSignal, exitSignal;
    readonly RegisteredWaitHandle captureWait, exitWait;
    readonly List<PinForm> pins = new();
    ToolStripMenuItem captureItem = null!, fullItem = null!, lastItem = null!, recentItem = null!, folderItem = null!, optionsItem = null!, exitItem = null!;
    ToolStripMenuItem shortcutsItem = null!, startupItem = null!;
    CaptureOverlay? overlay;
    SettingsForm? settingsForm;
    Action? balloonClick;

    static AppSettings Settings => AppSettings.Current;

    public TrayApp(bool launchedAtStartup, bool justUpdated = false)
    {
        Instance = this;
        invoker.CreateControl();
        hook = new KeyboardHook(invoker);
        hook.Pressed += OnHotkey;
        menu = BuildMenu();
        tray = new NotifyIcon
        {
            Icon = new Icon(AppIcon, SystemInformation.SmallIconSize),
            Text = "LiteShot",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Delayed(() => StartCapture()); };
        tray.BalloonTipClicked += (_, _) => { var action = balloonClick; balloonClick = null; action?.Invoke(); };
        tray.BalloonTipClosed += (_, _) => balloonClick = null;

        // A second launch of LiteShot.exe signals this event, and we take a screenshot — Lightshot behaves the same.
        captureSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.CaptureEventName);
        captureWait = ThreadPool.RegisterWaitForSingleObject(captureSignal, (_, _) => invoker.BeginInvoke(() => StartCapture()), null, -1, false);
        exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
        exitWait = ThreadPool.RegisterWaitForSingleObject(exitSignal, (_, _) => invoker.BeginInvoke(Exit), null, -1, true);

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        updateTimer.Tick += (_, _) => AutoUpdateTick();
        Updater.CleanUp();
        // Like Lightshot, LiteShot starts with Windows unless the user turns it off (tray menu or Settings).
        if (!Settings.FirstRunShown) StartupRegistration.Set(true);
        else StartupRegistration.Refresh();
        ApplySettings();

        if (justUpdated)
            Notify("LiteShot updated", $"You're on version {Updater.CurrentVersion}.", force: true);
        else if (!hook.IsInstalled)
            Notify("Hotkeys unavailable", "LiteShot couldn't register its keyboard shortcuts. Use the tray icon to take screenshots.", error: true);
        else if (!launchedAtStartup || !Settings.FirstRunShown)
            Notify("LiteShot is running", $"Press {HotkeyText.Format(Settings.HotkeyCapture)} to take a screenshot, or click the tray icon. Change the key under Change shortcuts in the tray menu.", force: true);
        Settings.FirstRunShown = true;
        Settings.Save();
    }

    static Icon LoadIcon()
    {
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("LiteShot.app.ico");
        return stream != null ? new Icon(stream) : SystemIcons.Application;
    }

    // ───────────────────────────── menu ─────────────────────────────

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip { Renderer = new ModernMenuRenderer() };
        captureItem = new ToolStripMenuItem("Take screenshot", null, (_, _) => Delayed(() => StartCapture()));
        captureItem.Font = new Font(captureItem.Font, FontStyle.Bold);
        fullItem = new ToolStripMenuItem("Capture full screen", null, (_, _) => Delayed(CaptureFullScreen));
        lastItem = new ToolStripMenuItem("Capture last area", null, (_, _) => Delayed(() => StartCapture(useLastRegion: true)));
        recentItem = new ToolStripMenuItem("Recent uploads");
        recentItem.DropDownItems.Add(new ToolStripMenuItem("No uploads yet") { Enabled = false });
        recentItem.DropDownOpening += (_, _) => FillRecentUploads();
        recentItem.DropDown.Renderer = new ModernMenuRenderer();
        folderItem = new ToolStripMenuItem("Open screenshots folder", null, (_, _) => ImageOutput.OpenFolder(Settings.SaveFolder));
        shortcutsItem = new ToolStripMenuItem("Change shortcuts…", null, (_, _) => ShowSettings(SettingsForm.ShortcutsPage));
        startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => StartupRegistration.Set(!StartupRegistration.IsEnabled));
        optionsItem = new ToolStripMenuItem("Settings", null, (_, _) => ShowSettings());
        exitItem = new ToolStripMenuItem("Exit", null, (_, _) => Exit());
        m.Items.AddRange(new ToolStripItem[]
        {
            captureItem, fullItem, lastItem, new ToolStripSeparator(),
            recentItem, folderItem, new ToolStripSeparator(),
            shortcutsItem, startupItem, optionsItem, exitItem,
        });
        foreach (ToolStripItem item in m.Items)
            if (item is ToolStripMenuItem) item.Padding = new Padding(4, 5, 4, 5);
        m.Opening += (_, _) => RefreshMenuIcons();
        ModernMenuRenderer.Attach(m);
        ModernMenuRenderer.Attach(recentItem.DropDown);
        return m;
    }

    /// <summary>Menu items for common actions carry icons; rendered on demand so they match the DPI and theme.</summary>
    void RefreshMenuIcons()
    {
        int size = (int)Math.Round(16 * menu.DeviceDpi / 96f);
        menu.ImageScalingSize = new Size(size, size);
        var color = SystemTheme.HighContrast ? SystemColors.MenuText : SystemTheme.Dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(32, 32, 32);
        void Set(ToolStripMenuItem item, Icons.Glyph glyph)
        {
            var old = item.Image;
            item.Image = Icons.Render(glyph, size, color);
            old?.Dispose();
        }
        Set(captureItem, Icons.Capture);
        Set(fullItem, Icons.Monitor);
        Set(lastItem, Icons.Repeat);
        Set(recentItem, Icons.Clock);
        Set(folderItem, Icons.Folder);
        Set(optionsItem, Icons.Gear);
        Set(shortcutsItem, Icons.Keyboard);
        startupItem.Checked = StartupRegistration.IsEnabled;
        Set(exitItem, Icons.Power);
    }

    void FillRecentUploads()
    {
        foreach (ToolStripItem item in recentItem.DropDownItems) item.Dispose();
        recentItem.DropDownItems.Clear();
        // Links that have already been deleted by the host are dropped from the list.
        if (Settings.Uploads.RemoveAll(u => u.Expired) > 0) Settings.Save();
        if (Settings.Uploads.Count == 0)
        {
            recentItem.DropDownItems.Add(new ToolStripMenuItem("No uploads yet") { Enabled = false });
            return;
        }
        foreach (var record in Settings.Uploads.Take(10))
        {
            string url = record.Url;
            string left = record.Expires is DateTime e ? $"deletes in {Remaining(e)}" : "permanent";
            // The key after # stays out of the menu; it's only needed when the link is copied or opened.
            string shown = url.Split('#')[0].Replace("https://", "");
            var item = new ToolStripMenuItem($"{record.Time:MMM d, HH:mm}   {shown}") { ShortcutKeyDisplayString = left, Padding = new Padding(4, 4, 4, 4) };
            item.DropDownItems.Add(new ToolStripMenuItem("Copy link", null, (_, _) =>
            {
                if (TrySetClipboardText(url)) Notify("Link copied", "Only people with this link can open it.", () => ImageOutput.OpenUrl(url));
            }));
            item.DropDownItems.Add(new ToolStripMenuItem("Open in browser", null, (_, _) => ImageOutput.OpenUrl(url)));
            if (!string.IsNullOrEmpty(record.DeleteToken))
            {
                item.DropDownItems.Add(new ToolStripSeparator());
                item.DropDownItems.Add(new ToolStripMenuItem("Delete now", null, (_, _) => DeleteUpload(record)));
            }
            foreach (ToolStripItem sub in item.DropDownItems)
                if (sub is ToolStripMenuItem) sub.Padding = new Padding(4, 4, 4, 4);
            item.DropDown.Renderer = new ModernMenuRenderer();
            ModernMenuRenderer.Attach(item.DropDown);
            recentItem.DropDownItems.Add(item);
        }
        recentItem.DropDownItems.Add(new ToolStripSeparator());
        recentItem.DropDownItems.Add(new ToolStripMenuItem("Clear history", null, (_, _) => { Settings.Uploads.Clear(); Settings.Save(); }) { Padding = new Padding(4, 4, 4, 4) });
    }

    /// <summary>Deletes an upload from fortefile.com now, then forgets it.</summary>
    async void DeleteUpload(UploadRecord record)
    {
        try
        {
            await Uploader.DeleteAsync(record);
            Settings.Uploads.Remove(record);
            Settings.Save();
            Notify("Link deleted", "The file is gone from fortefile.com, and the link no longer works.");
        }
        catch (Exception ex)
        {
            Notify("Couldn't delete the link", ex.Message, error: true);
        }
    }

    public void ApplySettings()
    {
        hook.SetHotkeys(Settings.HotkeyCapture, Settings.HotkeyFullScreen, Settings.HotkeyLastRegion);
        captureItem.ShortcutKeyDisplayString = Settings.HotkeyCapture == Keys.None ? "" : HotkeyText.Format(Settings.HotkeyCapture);
        fullItem.ShortcutKeyDisplayString = Settings.HotkeyFullScreen == Keys.None ? "" : HotkeyText.Format(Settings.HotkeyFullScreen);
        lastItem.ShortcutKeyDisplayString = Settings.HotkeyLastRegion == Keys.None ? "" : HotkeyText.Format(Settings.HotkeyLastRegion);
        tray.Text = Settings.HotkeyCapture == Keys.None ? "LiteShot" : $"LiteShot — {HotkeyText.Format(Settings.HotkeyCapture)} to capture";
        ScheduleUpdates();
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        invoker.BeginInvoke(() =>
        {
            SystemTheme.Refresh();
            menu.Renderer = new ModernMenuRenderer();
            recentItem.DropDown.Renderer = new ModernMenuRenderer();
        });
    }

    // ───────────────────────────── capture ─────────────────────────────

    void OnHotkey(Keys keys)
    {
        if (keys == Settings.HotkeyCapture) StartCapture();
        else if (keys == Settings.HotkeyFullScreen) CaptureFullScreen();
        else if (keys == Settings.HotkeyLastRegion) StartCapture(useLastRegion: true);
    }

    /// <summary>Waits for the tray flyout or menu to disappear so it isn't in the screenshot.</summary>
    void Delayed(Action action)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 220 };
        timer.Tick += (_, _) => { timer.Dispose(); action(); };
        timer.Start();
    }

    public void StartCapture(bool useLastRegion = false)
    {
        if (overlay != null)
        {
            overlay.Activate();
            return;
        }
        var virt = SystemInformation.VirtualScreen;
        Bitmap shot;
        try { shot = ScreenCapture.Capture(virt, Settings.CaptureCursor); }
        catch (Exception ex)
        {
            Notify("Screenshot failed", ex.Message, error: true);
            return;
        }
        var windows = Settings.DetectWindows ? ScreenCapture.GetWindowRects() : new List<Rectangle>();
        Rectangle? preselect = null;
        if (useLastRegion && Settings.LastRegion is { Length: 4 } r)
        {
            var rect = new Rectangle(r[0], r[1], r[2], r[3]);
            if (virt.Contains(rect)) preselect = rect;
        }

        var o = new CaptureOverlay(this, shot, virt, windows, preselect);
        overlay = o;
        o.FormClosed += (_, _) =>
        {
            overlay = null;
            invoker.BeginInvoke(() => GC.Collect());
        };
        o.Show();
    }

    async void CaptureFullScreen()
    {
        try
        {
            using var shot = ScreenCapture.Capture(SystemInformation.VirtualScreen, Settings.CaptureCursor);
            if (Settings.HidePersonalInfo)
            {
                var keywords = Settings.RedactKeywords.ToList();
                try { await Task.Run(() => PersonalInfo.BlurAsync(shot, keywords)); }
                catch (Exception ex) { Notify("Personal info wasn't hidden", ex.Message, error: true); return; }
            }
            string path = ImageOutput.SaveToFolder(shot);
            ImageOutput.CopyToClipboard(shot);
            NotifySaved(path);
        }
        catch (Exception ex)
        {
            Notify("Screenshot failed", ex.Message, error: true);
        }
    }

    public void RememberRegion(Rectangle screenRect)
    {
        Settings.LastRegion = new[] { screenRect.X, screenRect.Y, screenRect.Width, screenRect.Height };
        Settings.Save();
    }

    // ───────────────────────────── results ─────────────────────────────

    static string Remaining(DateTime expires)
    {
        var left = expires - DateTime.Now;
        return left.TotalHours >= 1 ? $"{Math.Ceiling(left.TotalHours)} h" : $"{Math.Max(1, Math.Ceiling(left.TotalMinutes))} min";
    }

    public async void Upload(Bitmap image, UploadKind kind, string? shareTemplate)
    {
        byte[] png;
        using (image) png = ImageOutput.ToPng(image);
        // Google has to see the picture to search for it, so search uploads aren't encrypted and last an hour.
        bool search = kind == UploadKind.Search;
        string lifetime = search ? "1h" : Uploader.Normalize(Settings.UploadLifetime);
        Notify("Uploading…", search
            ? "For the search, this upload isn't encrypted. It deletes itself after an hour."
            : $"Encrypted on your PC. The link deletes itself after {Uploader.Describe(lifetime)}.");
        uploadsInFlight++;
        try
        {
            var result = await Uploader.UploadAsync(png, lifetime, encrypt: !search);
            string url = result.Url;
            Settings.Uploads.Insert(0, new UploadRecord { Url = url, Time = DateTime.Now, Expires = result.Expires, Id = result.Id, DeleteToken = result.DeleteToken });
            if (Settings.Uploads.Count > 25) Settings.Uploads.RemoveRange(25, Settings.Uploads.Count - 25);
            Settings.Save();
            TrySetClipboardText(url);
            switch (kind)
            {
                case UploadKind.Search:
                    ImageOutput.OpenUrl("https://lens.google.com/uploadbyurl?url=" + Uri.EscapeDataString(url));
                    break;
                case UploadKind.Share when shareTemplate != null:
                    ImageOutput.OpenUrl(string.Format(shareTemplate, Uri.EscapeDataString(url)));
                    break;
                default:
                    Notify($"Link copied · deletes in {Uploader.Describe(lifetime)}", "Encrypted. Only people with this link can open it.", () => ImageOutput.OpenUrl(url));
                    break;
            }
        }
        catch (Exception ex)
        {
            try
            {
                using var ms = new MemoryStream(png);
                using var bmp = new Bitmap(ms);
                ImageOutput.CopyToClipboard(bmp);
            }
            catch { /* nothing more we can do */ }
            Notify("Upload failed", $"{ex.Message} The screenshot was copied to the clipboard instead.", error: true);
        }
        finally { uploadsInFlight--; }
    }

    /// <summary>Runs an AI edit or video in the background with a progress card, then shows the result.</summary>
    public async void RunAi(Bitmap image, AiKind kind, string prompt)
    {
        if (!AiService.HasKey)
        {
            image.Dispose();
            Notify("Connect SyncNode first", "Add your SyncNode API key to use AI editing and video.", force: true);
            ShowSettings(SettingsForm.CloudPage);
            return;
        }
        var cts = new CancellationTokenSource();
        var model = AiService.ModelFor(kind);
        var card = new AiProgressForm(kind == AiKind.Edit ? "Editing with AI" : "Making a video", model.Label, cts);
        card.Show();
        var progress = new Progress<string>(s => { if (!card.IsDisposed) card.SetStatus(s); });
        bool handedOver = false;
        try
        {
            if (kind == AiKind.Edit)
            {
                var result = await AiService.EditAsync(image, prompt, progress, cts.Token);
                var form = new AiResultForm(this, image, result, prompt);
                handedOver = true;
                form.Show();
                form.Activate();
            }
            else
            {
                string path = await AiService.VideoAsync(image, prompt, progress, cts.Token);
                try { Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { path }); } catch { }
                ImageOutput.OpenUrl(path);
                Notify("Video ready", $"{Path.GetFileName(path)} is saved and copied — paste it anywhere. Click to show it in the folder.", () => ImageOutput.RevealInExplorer(path), force: true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Notify(kind == AiKind.Edit ? "AI edit failed" : "Video failed", AiService.Friendly(ex), error: true);
        }
        finally
        {
            if (!card.IsDisposed) card.Close();
            if (!handedOver) image.Dispose();
            cts.Dispose();
        }
    }

    public void Pin(Bitmap image, Point screenLocation)
    {
        var pin = new PinForm(image, screenLocation);
        pins.Add(pin);
        pin.FormClosed += (_, _) => pins.Remove(pin);
        pin.Show();
        pin.Activate();
    }

    public void CloseAllPins()
    {
        foreach (var pin in pins.ToArray()) pin.Close();
    }

    public async void Ocr(Bitmap image)
    {
        try
        {
            string text;
            using (image) text = await OcrService.RecognizeAsync(image);
            if (string.IsNullOrWhiteSpace(text))
            {
                Notify("No text found", "LiteShot couldn't recognize any text in that area.");
                return;
            }
            if (TrySetClipboardText(text))
                Notify("Text copied", text.Length > 180 ? text[..180] + "…" : text);
        }
        catch (Exception ex)
        {
            Notify("Text recognition failed", ex.Message, error: true);
        }
    }

    static bool TrySetClipboardText(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, true, 10, 100);
            return true;
        }
        catch { return false; }
    }

    public void NotifySaved(string path) =>
        Notify("Screenshot saved", Path.GetFileName(path) + " — click to show it in the folder.", () => ImageOutput.RevealInExplorer(path));

    public void Notify(string title, string text, Action? onClick = null, bool error = false, bool force = false)
    {
        if (!Settings.ShowNotifications && !error && !force) return;
        balloonClick = onClick;
        tray.ShowBalloonTip(5000, title, string.IsNullOrEmpty(text) ? " " : text, error ? ToolTipIcon.Warning : ToolTipIcon.None);
    }

    public void ShowSettings(int page = 0)
    {
        if (settingsForm is { IsDisposed: false })
        {
            settingsForm.ShowPageAt(page);
            settingsForm.Activate();
            return;
        }
        settingsForm = new SettingsForm(this, page);
        settingsForm.FormClosed += (_, _) => settingsForm = null;
        settingsForm.Show();
        settingsForm.Activate();
    }

    // ───────────────────────────── updates ─────────────────────────────

    readonly System.Windows.Forms.Timer updateTimer = new();
    bool updating;
    int uploadsInFlight;

    /// <summary>Something is open or on its way; an automatic update never restarts LiteShot in the middle of it.</summary>
    bool Busy => uploadsInFlight > 0 || Application.OpenForms.Cast<Form>().Any(f => f.Visible);

    void ScheduleUpdates()
    {
        updateTimer.Stop();
        if (!Settings.AutoUpdate || !Updater.Configured) return;
        updateTimer.Interval = (int)TimeSpan.FromMinutes(2).TotalMilliseconds;   // let startup settle first
        updateTimer.Start();
    }

    async void AutoUpdateTick()
    {
        updateTimer.Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds;
        if (updating || !Settings.AutoUpdate) return;
        updating = true;
        try
        {
            var info = await Task.Run(() => Updater.CheckAsync());
            Settings.LastUpdateCheck = DateTime.Now;
            Settings.Save();
            if (info == null) return;
            string file = await Task.Run(() => Updater.DownloadAsync(info, Updater.IsInstalled));
            while (Busy) await Task.Delay(TimeSpan.FromSeconds(30));
            if (Settings.AutoUpdate) InstallUpdate(file);
        }
        catch (Exception ex)
        {
            Program.Log(ex);   // background checks stay quiet; the next one tries again
        }
        finally { updating = false; }
    }

    /// <summary>Starts the new version and quits this one.</summary>
    public void InstallUpdate(string file)
    {
        try { Updater.Install(file, Environment.ProcessPath!); }
        catch (Exception ex)
        {
            Notify("Couldn't install the update", ex.Message, error: true);
            return;
        }
        Exit();
    }

    void Exit()
    {
        updateTimer.Stop();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        hook.Dispose();
        captureWait.Unregister(null);
        exitWait.Unregister(null);
        captureSignal.Dispose();
        exitSignal.Dispose();
        CloseAllPins();
        overlay?.Close();
        settingsForm?.Close();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }
}
