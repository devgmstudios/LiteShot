using System.Drawing.Imaging;
using System.Net;
using System.Text;
using System.Text.Json;

namespace LiteShot.Harness;

/// <summary>
/// Ask AI against a stand-in for SyncNode on localhost, so no real key or credit is used: what goes up (key, model,
/// the screenshot once, the conversation so far), what comes back, errors, and the chat window itself.
/// </summary>
internal static class AskAiChecks
{
    /// <summary>
    /// One question, waited for off the UI thread: earlier checks leave WinForms' synchronization context on this
    /// thread, and blocking on it here would deadlock the reply.
    /// </summary>
    static string Ask(AskAi chat, string question, string key, string model) =>
        Task.Run(() => chat.AskAsync(question, key, model, CancellationToken.None)).GetAwaiter().GetResult();

    /// <summary>
    /// One real question and one follow-up through the live SyncNode with the saved key (costs about $0.06; only run
    /// with the owner's OK). Uses a made-up report; prints the answers, never the key.
    /// </summary>
    public static void Live(Action<bool, string> check)
    {
        string? key = AiService.ApiKey;
        check(key != null, "live: a SyncNode key is saved");
        if (key == null) return;
        using var shot = new Bitmap(900, 420, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(shot))
        {
            g.Clear(Color.White);
            using var title = new Font("Segoe UI Semibold", 34, GraphicsUnit.Pixel);
            using var body = new Font("Segoe UI", 24, GraphicsUnit.Pixel);
            g.DrawString("Quarterly report", title, Brushes.Black, 40, 40);
            g.DrawString("Revenue grew 24% year over year.", body, Brushes.DimGray, 40, 120);
            g.DrawString("Customer satisfaction reached 92%.", body, Brushes.DimGray, 40, 170);
            g.DrawString("Next quarter: onboarding and performance.", body, Brushes.DimGray, 40, 220);
        }
        var chat = new AskAi(shot);
        string model = AskAi.ModelId;
        Console.WriteLine($"model: {model}");
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string a1 = Ask(chat, "What does this screenshot say? One sentence.", key, model);
            Console.WriteLine($"answer 1 ({watch.Elapsed.TotalSeconds:0.0}s): {a1}");
            check(a1.Contains("24") || a1.Contains("revenue", StringComparison.OrdinalIgnoreCase), "live: the model read the screenshot");
            watch.Restart();
            string a2 = Ask(chat, "What was the satisfaction number you just saw? Just the number.", key, model);
            Console.WriteLine($"answer 2 ({watch.Elapsed.TotalSeconds:0.0}s): {a2}");
            check(a2.Contains("92"), "live: a follow-up remembers the screenshot");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"shown to the user: {AiService.Friendly(ex)}");
            string raw = SyncNodeClient.LastChatReply ?? "(none)";
            raw = System.Text.RegularExpressions.Regex.Replace(raw, @"(?i)(""(api_?key|uid|token|authorization)""\s*:\s*)""[^""]*""", "$1\"[hidden]\"");
            raw = raw.Replace(key, "[hidden]");
            Console.WriteLine($"raw reply: {(raw.Length > 1500 ? raw[..1500] + "…" : raw)}");
            check(false, "live: the chat answered");
        }
    }

    public static void Run(string outDir, Action<bool, string> check)
    {
        var requests = new List<JsonDocument>();
        int port = 18000 + Random.Shared.Next(1000);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                string body = new StreamReader(ctx.Request.InputStream).ReadToEnd();
                // "slowly" in a question gives time to press Stop.
                if (body.Contains("slowly")) await Task.Delay(2500);
                var doc = JsonDocument.Parse(body);
                lock (requests) requests.Add(doc);
                string model = doc.RootElement.GetProperty("model").GetString() ?? "";
                bool ok = ctx.Request.Url!.AbsolutePath == "/chat-completion" && model != "nobody/no-such-model";
                // The real SyncNode wraps OpenRouter's reply: {"prediction": {..., "output": {"choices": …}}} (as seen live);
                // the "openrouter/bare" model gets a bare OpenRouter reply, in case SyncNode ever drops the wrapper.
                var openRouter = new { id = "gen-test", @object = "chat.completion", choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = "**Revenue** grew 24%.\n\n- Customers up 9%\n- Account team named" } } } };
                string reply = !ok
                    ? JsonSerializer.Serialize(new { error = new { message = "No endpoints found for nobody/no-such-model." } })
                    : model == "openrouter/bare" ? JsonSerializer.Serialize(openRouter)
                    : JsonSerializer.Serialize(new { prediction = new { id = "p1", uid = "hidden", model, provider = "OpenRouter", status = "completed", job_id = "j1", output = openRouter, cost = 0, error = (string?)null } });
                ctx.Response.StatusCode = ok ? 200 : 404;
                ctx.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(reply);
                ctx.Response.OutputStream.Write(bytes);
                ctx.Response.Close();
            }
        });
        string savedBase = SyncNodeClient.Base;
        SyncNodeClient.Base = $"http://localhost:{port}";
        try
        {
            using var shot = new Bitmap(800, 500, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(shot))
            {
                g.Clear(Color.White);
                using var font = new Font("Segoe UI", 28, GraphicsUnit.Pixel);
                g.DrawString("Q3 revenue $4.2M (+24%)", font, Brushes.Black, 40, 60);
            }

            // The conversation, as the server sees it.
            var chat = new AskAi(shot);
            string first = Ask(chat, "What's in this screenshot?", "sn_test_key", "anthropic/claude-sonnet-5.5");
            check(first.StartsWith("**Revenue**"), "ask: the model's answer comes back");
            var r1 = requests[^1].RootElement;
            var m1 = r1.GetProperty("messages");
            var userParts = m1[1].GetProperty("content");
            check(r1.GetProperty("apiKey").GetString() == "sn_test_key" && r1.GetProperty("model").GetString() == "anthropic/claude-sonnet-5.5",
                "ask: the SyncNode key and the chosen model go up");
            check(m1.GetArrayLength() == 2 && m1[0].GetProperty("role").GetString() == "system" && userParts.ValueKind == JsonValueKind.Array
                && userParts[1].GetProperty("image_url").GetProperty("url").GetString()!.StartsWith("data:image/jpeg;base64,"),
                "ask: the first question carries the screenshot, inside the request");
            Ask(chat, "And customers?", "sn_test_key", "anthropic/claude-sonnet-5.5");
            var m2 = requests[^1].RootElement.GetProperty("messages");
            check(m2.GetArrayLength() == 4 && m2[2].GetProperty("role").GetString() == "assistant" && m2[3].GetProperty("content").ValueKind == JsonValueKind.String,
                "ask: follow-ups send the conversation so far, the screenshot only once");

            // A model OpenRouter doesn't have: a clear message, and the failed turn leaves no trace.
            Exception? error = null;
            try { Ask(chat, "Hello?", "sn_test_key", "nobody/no-such-model"); }
            catch (Exception ex) { error = ex; }
            check(error != null && AiService.Friendly(error).Contains("doesn't know that model"), $"ask: an unknown model says so ({(error == null ? "no error" : AiService.Friendly(error))})");
            Ask(chat, "Thanks", "sn_test_key", "anthropic/claude-sonnet-5.5");
            check(requests[^1].RootElement.GetProperty("messages").GetArrayLength() == 6, "ask: a failed question isn't kept in the conversation");
            check(AiService.Friendly(new SyncNodeException("No OpenRouter API key found", 400)).Contains("OpenRouter key"), "ask: a missing OpenRouter key says where to add it");
            check(Ask(new AskAi(shot), "Bare?", "sn_test_key", "openrouter/bare").StartsWith("**Revenue**"), "ask: a bare OpenRouter reply is read too");
            check(AskAi.Plain("## Summary\n**Revenue** grew `24%`.\n- one\n* two\n```\ncode\n```") == "Summary\nRevenue grew 24%.\n• one\n• two\ncode",
                "ask: Markdown answers show as plain text");

            // The chat window: ask, wait for the answer, look at it.
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            using (var form = new AskAiForm((Bitmap)shot.Clone(), () => "sn_test_key") { StartPosition = FormStartPosition.Manual, Location = new Point(-6000, -6000) })
            {
                form.Show();
                Application.DoEvents();
                using (var start = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(start, new Rectangle(Point.Empty, form.Size));
                    start.Save(Path.Combine(outDir, "09-ask-ai-start.png"), ImageFormat.Png);
                }
                form.Ask("What's in this screenshot?");
                var until = DateTime.UtcNow.AddSeconds(15);
                while (form.Busy && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(20); }
                Application.DoEvents();
                var said = form.Messages.ToList();
                check(said.Count == 2 && said[0].Mine && said[0].Text == "What's in this screenshot?" && !said[1].Mine && said[1].Text.StartsWith("Revenue grew 24%."),
                    $"ask: the window shows the question and the answer ({said.Count})");
                // DoEvents can drop WinForms' synchronization context; put it back before each step, as the app's message loop would have it.
                void Ui() => SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                void Wait() { var end = DateTime.UtcNow.AddSeconds(15); while (form.Busy && DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(20); } Application.DoEvents(); }
                string Labels() => string.Join(" | ", form.ActionLabels.Select(a => string.Join(",", a)));
                check(Labels() == "Copy,Edit | Copy,Retry", $"ask: Copy and Edit under the question, Copy and Retry under the answer ({Labels()})");

                // Retry: the same question again, from the start, so the screenshot goes up again.
                int sent = requests.Count;
                Ui(); form.RetryLast();
                Wait();
                var retried = requests[^1].RootElement.GetProperty("messages");
                check(requests.Count == sent + 1 && form.Messages.Count() == 2 && retried.GetArrayLength() == 2 && retried[1].GetProperty("content").ValueKind == JsonValueKind.Array,
                    "ask: Retry asks the last question again in place of the old answer");

                // A follow-up, then Edit takes it back: only the latest question can be edited.
                Ui(); form.Ask("And customers?");
                Wait();
                check(Labels() == "Copy | Copy | Copy,Edit | Copy,Retry", $"ask: only the latest exchange has Edit and Retry ({Labels()})");
                using (var bmp = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                    bmp.Save(Path.Combine(outDir, "09-ask-ai.png"), ImageFormat.Png);
                }
                Ui(); form.EditLast();
                check(form.Draft == "And customers?" && form.Messages.Count() == 2, "ask: Edit puts the last question back in the box and takes it off the chat");
                Ui(); form.Ask("And churn?");
                Wait();
                var edited = requests[^1].RootElement.GetProperty("messages");
                check(edited.GetArrayLength() == 4 && edited[3].GetProperty("content").GetString() == "And churn?", "ask: the edited question replaces the old one in the conversation");

                // Stop while it's thinking: the question comes back to the box and nothing is kept.
                Ui(); form.Ask("Tell me slowly");
                var t = DateTime.UtcNow.AddMilliseconds(300);
                while (DateTime.UtcNow < t) { Application.DoEvents(); Thread.Sleep(10); }
                check(form.Busy, "ask: a slow answer keeps the window busy");
                form.Stop();
                Wait();
                check(!form.Busy && form.Draft == "Tell me slowly" && form.Messages.Count() == 4, "ask: Stop takes the question back to the box");
                Ui(); form.Ask("Thanks");
                Wait();
                check(requests[^1].RootElement.GetProperty("messages").GetArrayLength() == 6, "ask: a stopped question isn't kept in the conversation");

                // The screenshot opens big: fitted first, then the wheel zooms.
                var viewer = form.ShowZoom();
                viewer.StartPosition = FormStartPosition.Manual;
                viewer.Location = new Point(-9000, -9000);
                viewer.Size = new Size(1000, 760);
                Application.DoEvents();
                viewer.View.Fit();
                check(viewer.View.Fitted && viewer.View.Zoom > 0.9f && viewer.View.Zoom < 1.3f, $"zoom: the screenshot opens fitted to the window ({viewer.View.Zoom:0.00})");
                var hintLabel = viewer.Controls.OfType<Label>().First();
                check(hintLabel.Width >= viewer.ClientSize.Width - 2 && hintLabel.Text.Contains("scroll to zoom"), $"zoom: the hint runs along the bottom ({hintLabel.Bounds} {hintLabel.Dock} {hintLabel.AutoSize} canvas {viewer.View.Bounds} of {viewer.ClientSize})");
                check(ReferenceEquals(form.ShowZoom(), viewer), "zoom: clicking again brings back the same viewer");
                viewer.View.ZoomBy(3f, new Point(200, 200));
                check(!viewer.View.Fitted && viewer.View.Zoom > 2.5f, "zoom: zooming in leaves the fitted view");
                using (var zb = new Bitmap(viewer.Width, viewer.Height))
                {
                    viewer.DrawToBitmap(zb, new Rectangle(Point.Empty, viewer.Size));
                    zb.Save(Path.Combine(outDir, "09-ask-ai-zoom.png"), ImageFormat.Png);
                }
                form.Close();
                check(viewer.IsDisposed, "zoom: closing the chat closes the viewer");
            }
            using (var tall = new Bitmap(500, 1400, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(tall))
                {
                    g.Clear(Color.White);
                    using var font = new Font("Segoe UI", 26, GraphicsUnit.Pixel);
                    for (int i = 0; i < 20; i++) g.DrawString($"Line {i + 1} of a long page", font, Brushes.Black, 30, 30 + i * 66);
                }
                using var tallForm = new AskAiForm((Bitmap)tall.Clone(), () => "sn_test_key") { StartPosition = FormStartPosition.Manual, Location = new Point(-6000, -6000) };
                tallForm.Show();
                Application.DoEvents();
                using var tb = new Bitmap(tallForm.Width, tallForm.Height);
                tallForm.DrawToBitmap(tb, new Rectangle(Point.Empty, tallForm.Size));
                tb.Save(Path.Combine(outDir, "09-ask-ai-tall.png"), ImageFormat.Png);
                tallForm.Close();
            }
            using (var noKey = new AskAiForm((Bitmap)shot.Clone(), () => null) { StartPosition = FormStartPosition.Manual, Location = new Point(-6000, -6000) })
            {
                noKey.Show();
                int before = requests.Count;
                noKey.Ask("Hello");
                Application.DoEvents();
                check(!noKey.Busy && requests.Count == before && !noKey.Messages.Any(), "ask: without a key nothing is sent");
                noKey.Close();
            }
            // Settings › Cloud & AI, tall enough to show the Ask AI model row at the bottom. Nothing here is saved.
            using (var settings = new SettingsForm(null!) { StartPosition = FormStartPosition.Manual, Location = new Point(-10000, -10000) })
            {
                settings.Show();
                settings.GetType().GetMethod("ShowPage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
                    .Invoke(settings, new object[] { 3 });
                settings.Height = 2300;
                Application.DoEvents();
                using var page = new Bitmap(settings.Width, settings.Height);
                settings.DrawToBitmap(page, new Rectangle(Point.Empty, settings.Size));
                page.Save(Path.Combine(outDir, "09-ask-ai-settings.png"), ImageFormat.Png);
                settings.Close();
            }
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        finally
        {
            SyncNodeClient.Base = savedBase;
            listener.Stop();
            try { serving.Wait(2000); } catch { }
        }
    }
}
