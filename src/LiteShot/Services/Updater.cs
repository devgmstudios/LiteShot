using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiteShot;

internal sealed record UpdateFile(string Kind, string Name, long Size, string Sha256);

internal sealed record UpdateInfo(Version Version, string Notes, DateTime Published, IReadOnlyList<UpdateFile> Files, string Server);

internal sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Updates from the LiteShot update server (server/, a Cloudflare Worker). Each release is signed with Devgm's private
/// key and checked against the public key built into the app, and every download must match the SHA-256 in that signed
/// release, so neither the server nor the connection can slip in other code.
/// </summary>
internal static class Updater
{
    // Static fields initialize in order: the version must exist before the client that sends it.
    public static Version CurrentVersion { get; } = Version.Parse(
        (typeof(Updater).Assembly.GetName().Version ?? new Version(1, 0, 0)).ToString(3));

    static readonly HttpClient Http = CreateClient();
    static readonly Regex FileName = new("^[A-Za-z0-9._-]{1,100}$"), Hex = new("^[0-9a-f]{64}$");

    /// <summary>The server: settings.json can override the one baked in at build time (for testing).</summary>
    public static string? Server
    {
        get
        {
            string? url = AppSettings.Current.UpdateServer;
            if (string.IsNullOrWhiteSpace(url))
                url = typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "UpdateServer")?.Value;
            return string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
        }
    }

    static string? PublicKeyPem { get; } = ReadPublicKey();

    /// <summary>False in builds without a server address or signing key; the settings explain that instead.</summary>
    public static bool Configured => Server != null && PublicKeyPem != null;

    static string? ReadPublicKey()
    {
        using var stream = typeof(Updater).Assembly.GetManifestResourceStream("LiteShot.update-public-key.pem");
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"LiteShot/{CurrentVersion}");
        return client;
    }

    /// <summary>The newest release on this copy's channel, even if it isn't newer than this copy; null if there's none.</summary>
    public static async Task<UpdateInfo?> LatestAsync(CancellationToken ct = default)
    {
        string server = Server ?? throw new UpdateException("Updates aren't set up in this build.");
        string channel = Uri.EscapeDataString(AppSettings.Current.UpdateChannel);
        using var response = await Http.GetAsync($"{server}/v1/latest?channel={channel}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new UpdateException($"The update server answered {(int)response.StatusCode}.");
        return Parse(await response.Content.ReadAsStringAsync(ct), server);
    }

    /// <summary>The newest release if it's newer than this copy, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default) =>
        await LatestAsync(ct) is { } info && info.Version > CurrentVersion ? info : null;

    /// <summary>The newer release the last check found, ready to install; null until a check finds one.</summary>
    public static UpdateInfo? Available { get; private set; }

    /// <summary>
    /// The newer version LiteShot knows about: the one the last check found, or, before this run has checked, the newest
    /// version an earlier run saw.
    /// </summary>
    public static Version? Newer =>
        Available?.Version ?? (Version.TryParse(AppSettings.Current.LatestVersion, out var seen) && seen > CurrentVersion ? seen : null);

    /// <summary>
    /// Checks for a newer release and remembers the answer: when it checked, the newest version, and that version's
    /// notes when it's this one (so a copy updated by hand can still say what's new). Call it from the UI thread;
    /// only the request runs in the background. Returns the newer release, or null when this copy is up to date.
    /// </summary>
    public static async Task<UpdateInfo?> CheckNowAsync()
    {
        var latest = await Task.Run(() => LatestAsync());
        var settings = AppSettings.Current;
        settings.LastUpdateCheck = DateTime.Now;
        settings.LatestVersion = latest?.Version.ToString();
        if (latest != null && latest.Version == CurrentVersion && !string.IsNullOrWhiteSpace(latest.Notes))
        {
            settings.NotesVersion = latest.Version.ToString();
            settings.Notes = latest.Notes.Trim();
        }
        settings.Save();
        Available = latest != null && latest.Version > CurrentVersion ? latest : null;
        return Available;
    }

    /// <summary>What to tell the user when checking for or downloading an update fails.</summary>
    public static string Describe(Exception ex) =>
        ex is UpdateException ? ex.Message
        : ex is HttpRequestException or TaskCanceledException ? "Couldn't reach the update server. Check your connection and try again."
        : ex.Message;

    /// <summary>
    /// True when this run is a newer version than the last one: it installed itself (<paramref name="justUpdated"/>,
    /// from --updated) or was installed by hand. A copy that has never recorded a version only counts with --updated.
    /// </summary>
    internal static bool WasUpdated(string? lastRunVersion, bool justUpdated) =>
        justUpdated || (Version.TryParse(lastRunVersion, out var before) && before < CurrentVersion);

    /// <summary>Checks the release's signature, then reads it. Anything unexpected throws, and nothing is installed.</summary>
    internal static UpdateInfo Parse(string envelopeJson, string server, string? publicKeyPem = null)
    {
        string payload;
        byte[] signature;
        try
        {
            using var envelope = JsonDocument.Parse(envelopeJson);
            payload = envelope.RootElement.GetProperty("payload").GetString() ?? "";
            signature = Convert.FromBase64String(envelope.RootElement.GetProperty("signature").GetString() ?? "");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            throw new UpdateException("The update server sent something LiteShot doesn't understand.");
        }

        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportFromPem(publicKeyPem ?? PublicKeyPem ?? throw new UpdateException("This build has no update signing key."));
            if (!ecdsa.VerifyData(Encoding.UTF8.GetBytes(payload), signature, HashAlgorithmName.SHA256))
                throw new UpdateException("The update isn't signed by Devgm, so it wasn't installed.");
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.GetProperty("product").GetString() != "LiteShot") throw new UpdateException("That update is for a different product.");
            var files = root.GetProperty("files").EnumerateArray().Select(f => new UpdateFile(
                f.GetProperty("kind").GetString() ?? "",
                f.GetProperty("name").GetString() ?? "",
                f.GetProperty("size").GetInt64(),
                f.GetProperty("sha256").GetString() ?? "")).ToList();
            if (files.Any(f => !FileName.IsMatch(f.Name) || f.Name.StartsWith('.') || !Hex.IsMatch(f.Sha256) || f.Size <= 0))
                throw new UpdateException("The update lists a file LiteShot won't download.");
            return new UpdateInfo(
                Version.Parse(root.GetProperty("version").GetString() ?? ""),
                root.TryGetProperty("notes", out var notes) ? notes.GetString() ?? "" : "",
                root.TryGetProperty("published", out var published) && published.TryGetDateTime(out var when) ? when : DateTime.MinValue,
                files, server);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or ArgumentException or InvalidOperationException)
        {
            throw new UpdateException("The update's details are incomplete.");
        }
    }

    static string InstallFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LiteShot");

    static string UpdatesFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteShot", "Updates");

    /// <summary>True when this copy came from the installer; the portable exe updates by swapping itself.</summary>
    public static bool IsInstalled =>
        string.Equals(Path.GetDirectoryName(Environment.ProcessPath), InstallFolder, StringComparison.OrdinalIgnoreCase);

    public static UpdateFile? FileFor(UpdateInfo info, bool installed) =>
        info.Files.FirstOrDefault(f => f.Kind == (installed ? "msi" : "portable"));

    /// <summary>Downloads the right file for this copy and checks its size and SHA-256. Returns the local path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, bool installed, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var file = FileFor(info, installed) ?? throw new UpdateException("This release has no download for your copy of LiteShot.");
        string version = info.Version.ToString(3);
        string dir = Path.Combine(UpdatesFolder, version);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, file.Name);
        if (File.Exists(path) && new FileInfo(path).Length == file.Size && await Sha256Async(path, ct) == file.Sha256) return path;

        string part = path + ".part";
        try
        {
            using var response = await Http.GetAsync($"{info.Server}/v1/download/{version}/{Uri.EscapeDataString(file.Name)}", HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new UpdateException($"The download failed ({(int)response.StatusCode}).");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(part))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += n;
                    if (total > file.Size) throw new UpdateException("The download is bigger than the release says.");
                    hash.AppendData(buffer, 0, n);
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                    progress?.Report((double)total / file.Size);
                }
            }
            if (total != file.Size || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != file.Sha256)
                throw new UpdateException("The download doesn't match the signed release, so it wasn't installed.");
            File.Move(part, path, overwrite: true);
            return path;
        }
        finally
        {
            try { File.Delete(part); } catch { /* best effort */ }
        }
    }

    static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    /// <summary>
    /// Hands over to the new version; the caller exits right after. Installed copies run the new MSI quietly (it closes
    /// this copy first), then start LiteShot again. The portable exe is swapped in place: Windows lets a running exe be
    /// renamed, so this one moves aside and the new one takes its name.
    /// </summary>
    public static void Install(string file, string exe)
    {
        if (file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            string command = $"/c start \"\" /wait msiexec /i \"{file}\" /qn /norestart & start \"\" \"{exe}\" --updated";
            Process.Start(new ProcessStartInfo("cmd.exe", command) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            return;
        }
        SwapExe(file, exe);
        Process.Start(new ProcessStartInfo(exe, $"--updated --wait {Environment.ProcessId}") { UseShellExecute = false });
    }

    /// <summary>Puts <paramref name="file"/> where <paramref name="exe"/> is, keeping the old one as .old until the next start.</summary>
    internal static void SwapExe(string file, string exe)
    {
        string old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Copy(file, exe); }
        catch
        {
            File.Move(old, exe);
            throw;
        }
    }

    /// <summary>Removes what an earlier update left behind: the old portable exe and downloads up to this version.</summary>
    public static void CleanUp()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null && File.Exists(exe + ".old")) File.Delete(exe + ".old");
        }
        catch { /* still in use; next start */ }
        try
        {
            if (!Directory.Exists(UpdatesFolder)) return;
            foreach (var dir in Directory.GetDirectories(UpdatesFolder))
                if (!Version.TryParse(Path.GetFileName(dir), out var v) || v <= CurrentVersion)
                    Directory.Delete(dir, recursive: true);
        }
        catch { /* best effort */ }
    }
}
