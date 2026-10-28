using System.Drawing.Imaging;
using System.Net;
using LiteShot;

namespace LiteShot.Harness;

/// <summary>Encrypted uploads to fortefile, against a local server (`npm run dev` in fortefile/, then --fortefile-server).</summary>
internal static class Fortefile
{
    public static void Run(Action<bool, string> check, string? server)
    {
        // The encryption itself, offline.
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        byte[] plain = "LiteShot screenshot bytes"u8.ToArray();
        byte[] sealedBytes = Uploader.Encrypt(plain, key);
        check(sealedBytes.Length == plain.Length + 28 && Uploader.Decrypt(sealedBytes, key).SequenceEqual(plain), "AES-256-GCM round trip (nonce + ciphertext + tag)");
        sealedBytes[20] ^= 1;
        check(Throws(() => Uploader.Decrypt(sealedBytes, key)), "a tampered file fails to decrypt");
        check(Uploader.FromBase64Url(Uploader.Base64Url(key)).SequenceEqual(key) && !Uploader.Base64Url(key).Contains('='), "keys travel as base64url without padding");

        if (server == null)
        {
            Console.WriteLine("  (skipped fortefile upload checks: run `npm run dev` in fortefile/ and pass --fortefile-server)");
            return;
        }

        var settings = AppSettings.Current;
        string? saved = settings.FortefileServer;
        settings.FortefileServer = server;
        try
        {
            using var bmp = new Bitmap(320, 200);
            using (var g = Graphics.FromImage(bmp)) g.Clear(Color.FromArgb(242, 107, 29));
            byte[] png = ImageOutput.ToPng(bmp);

            var result = Run(() => Uploader.UploadAsync(png, "24h", encrypt: true));
            var link = new Uri(result.Url);
            check(link.AbsolutePath == $"/f/{result.Id}" && link.Fragment.Length > 40, $"encrypted upload returns a /f/ link with the key after # ({result.Url.Split('#')[0]})");
            check(Math.Abs((result.Expires - DateTime.Now - TimeSpan.FromHours(24)).TotalMinutes) < 2, $"expires in 24 hours ({result.Expires:t})");

            using var http = new HttpClient();
            byte[] stored = Run(() => http.GetByteArrayAsync($"{server}/api/files/{result.Id}"));
            check(!stored.AsSpan().SequenceEqual(png) && stored.Length == png.Length + 28, "the server only holds ciphertext");
            byte[] opened = Uploader.Decrypt(stored, Uploader.FromBase64Url(link.Fragment.TrimStart('#')));
            check(opened.SequenceEqual(png), "the key in the link decrypts the original screenshot");

            var record = new UploadRecord { Url = result.Url, Id = result.Id, DeleteToken = result.DeleteToken, Time = DateTime.Now, Expires = result.Expires };
            var wrong = new UploadRecord { Url = result.Url, Id = result.Id, DeleteToken = "not-the-token", Time = DateTime.Now };
            check(Throws(() => Run(() => Uploader.DeleteAsync(wrong).ContinueWith(t => { t.GetAwaiter().GetResult(); return 0; }))), "a wrong delete token is refused");
            Run(() => Uploader.DeleteAsync(record).ContinueWith(t => { t.GetAwaiter().GetResult(); return 0; }));
            var gone = Run(() => http.GetAsync($"{server}/api/files/{result.Id}"));
            check(gone.StatusCode == HttpStatusCode.NotFound, "Delete now removes the file");

            var search = Run(() => Uploader.UploadAsync(png, "1h", encrypt: false));
            byte[] raw = Run(() => http.GetByteArrayAsync(search.Url));
            check(search.Url.EndsWith(".png") && raw.SequenceEqual(png) && search.Expires <= DateTime.Now.AddHours(1).AddMinutes(1),
                "search uploads are plain PNGs that last an hour");
            Run(() => Uploader.DeleteAsync(new UploadRecord { Url = search.Url, Id = search.Id, DeleteToken = search.DeleteToken }).ContinueWith(t => 0));
        }
        finally { settings.FortefileServer = saved; }
    }

    /// <summary>Uploads a PNG the way the app does and prints the link, for opening in a browser.</summary>
    public static void PrintLink(string server, string png)
    {
        AppSettings.Current.FortefileServer = server;
        var result = Run(() => Uploader.UploadAsync(File.ReadAllBytes(png), "24h", encrypt: true));
        Console.WriteLine(result.Url);
    }

    static T Run<T>(Func<Task<T>> f) => Task.Run(f).GetAwaiter().GetResult();

    static bool Throws(Action a)
    {
        try { a(); return false; }
        catch { return true; }
    }
}
